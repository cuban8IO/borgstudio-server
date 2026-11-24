using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Application.Abstractions.Setup;
using BorgStudio.Server.Application.Abstractions.Updates;
using BorgStudio.Server.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BorgStudio.Server.Tests.Web;

/// <summary>A real server in memory with its own, empty data folder.</summary>
public sealed class TestServer : WebApplicationFactory<Program>
{
    public const string AdminName = "admin";
    public const string AdminPassword = "Correct-Horse-9";

    public TestServer()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "borgstudio-server-tests", Guid.NewGuid().ToString("N"));
    }

    public string DataDirectory { get; }

    /// <summary>The server's clock; starts at the real time and only moves when a test moves it.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>The version the server reports; tests change it to simulate an update.</summary>
    public string Version { get; set; } = "0.1.0";

    /// <summary>What "GitHub" answers to the update check.</summary>
    public FakeReleaseFeed Releases { get; } = new();

    public string SetupCode => Services.GetRequiredService<ISetupCodeStore>().Current
        ?? throw new InvalidOperationException("Setup is already done.");

    public Browser CreateBrowser(string? language = null) => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = true,
        HandleCookies = true,
    }), language);

    /// <summary>Runs the setup wizard and leaves the browser at the "turn on two-factor?" step.</summary>
    public async Task<Browser> SetUpAsync(string serverName = "Test Server")
    {
        var browser = CreateBrowser();
        var page = await browser.GetAsync("/");
        page = await browser.SubmitAsync(page, "setup", new()
        {
            ["Input.SetupCode"] = SetupCode,
            ["Input.ServerName"] = serverName,
            ["Input.UserName"] = AdminName,
            ["Input.Email"] = string.Empty,
            ["Input.Password"] = AdminPassword,
            ["Input.ConfirmPassword"] = AdminPassword,
        });
        browser.Last = page;
        return browser;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ServerPaths.ConfigurationKey, DataDirectory);
        // The test host has no network listeners; no need for a certificate.
        builder.UseSetting("BorgStudio:Https:Enabled", "false");
        builder.UseSetting("BorgStudio:Http:Enabled", "true");
        builder.UseEnvironment("Production");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IServerEnvironment>(new TestEnvironment(this));
            services.AddSingleton<IReleaseFeed>(Releases);
            // Check sessions on every request, so tests see sign-outs at once instead of after a minute.
            services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file still in use is left to the system's temp cleanup.
        }
    }
}

/// <summary>An HTML page as the browser received it.</summary>
public sealed record Page(HttpStatusCode Status, string Path, string Html)
{
    public string Text => WebUtility.HtmlDecode(Html);
}

/// <summary>A minimal browser: follows redirects, keeps cookies and submits forms with their hidden fields.</summary>
public sealed partial class Browser(HttpClient client, string? language) : IDisposable
{
    public Page? Last { get; set; }

    public HttpClient Client => client;

    public async Task<Page> GetAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return Last = await SendAsync(request);
    }

    /// <summary>Submits the form named <paramref name="formName"/> on <paramref name="page"/>.</summary>
    public async Task<Page> SubmitAsync(Page page, string formName, Dictionary<string, string>? fields = null)
    {
        var form = FindForm(page, formName);
        var values = HiddenInputs().Matches(form)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        foreach (var (name, value) in fields ?? [])
        {
            values[name] = value;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, page.Path) { Content = new FormUrlEncodedContent(values) };
        return Last = await SendAsync(request);
    }

    public void Dispose() => client.Dispose();

    private async Task<Page> SendAsync(HttpRequestMessage request)
    {
        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        return new Page(response.StatusCode, response.RequestMessage!.RequestUri!.PathAndQuery, html);
    }

    private static string FindForm(Page page, string formName)
    {
        foreach (Match match in Forms().Matches(page.Html))
        {
            if (match.Value.Contains($"name=\"_handler\" value=\"{formName}\"", StringComparison.Ordinal))
            {
                return match.Value;
            }
        }

        throw new InvalidOperationException($"No form \"{formName}\" on {page.Path}:\n{page.Text}");
    }

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex Forms();

    [GeneratedRegex("<input type=\"hidden\" name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]*)\"")]
    private static partial Regex HiddenInputs();
}

public static partial class PageExtensions
{
    public static IReadOnlyList<string> RecoveryCodes(this Page page) =>
        RecoveryCode().Matches(page.Html).Select(m => m.Groups["code"].Value).ToList();

    public static string AuthenticatorKey(this Page page)
    {
        var match = AuthenticatorKeyElement().Match(page.Html);
        return match.Success
            ? match.Groups["key"].Value.Replace(" ", string.Empty, StringComparison.Ordinal)
            : throw new InvalidOperationException($"No authenticator key on {page.Path}");
    }

    [GeneratedRegex("<li><code>(?<code>[^<]+)</code></li>")]
    private static partial Regex RecoveryCode();

    [GeneratedRegex("data-testid=\"authenticator-key\"[^>]*>(?<key>[^<]+)<")]
    private static partial Regex AuthenticatorKeyElement();
}

/// <summary>What an authenticator app does: RFC 6238 codes, 6 digits, 30 seconds.</summary>
public static class Totp
{
    public static string Code(string base32Key)
    {
        var key = Base32Decode(base32Key);
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

/// <summary>Stands in for GitHub's list of releases.</summary>
public sealed class FakeReleaseFeed : IReleaseFeed
{
    public List<PublishedRelease> Releases { get; } = [];

    public bool Fails { get; set; }

    public Task<IReadOnlyList<PublishedRelease>> GetReleasesAsync(CancellationToken cancellationToken) =>
        Fails
            ? throw new HttpRequestException("GitHub is not reachable.")
            : Task.FromResult<IReadOnlyList<PublishedRelease>>([.. Releases]);

    public void Add(string version, string notes) => Releases.Add(new PublishedRelease(
        Version.Parse(version), DateTimeOffset.UtcNow, notes, $"https://github.com/cuban8IO/borgstudio-server/releases/tag/v{version}"));
}

/// <summary>The running version comes from the test server, so a test can "update" it.</summary>
internal sealed class TestEnvironment(TestServer server) : IServerEnvironment
{
    public string InformationalVersion => server.Version + "+0123456789abcdef";

    public string OperatingSystem => "Test OS";

    public string Runtime => ".NET Test";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}
