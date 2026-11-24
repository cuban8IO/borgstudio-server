using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using BorgStudio.Server.Tests.Web;

namespace BorgStudio.Server.Tests.EndToEnd;

/// <summary>
/// Against an installed server (CI installs the Linux archive as a service): set it up, pair an
/// "app" through the interface, and back up with real borg over the server's SSH service.
/// Runs only when BORGSTUDIO_E2E_URL is set; otherwise it passes without doing anything.
/// </summary>
/// <remarks>
/// BORGSTUDIO_E2E_URL: the server, e.g. https://localhost:8443 · BORGSTUDIO_E2E_SETUP_CODE: from data/setup-code.txt ·
/// BORGSTUDIO_E2E_BORG: a borg program for the client side.
/// </remarks>
public sealed class BorgOverSshTests : IDisposable
{
    private readonly string? url = Environment.GetEnvironmentVariable("BORGSTUDIO_E2E_URL");
    private readonly string work = Path.Combine(Path.GetTempPath(), "borgstudio-e2e", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackupOverSsh_WithAPairedApp()
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var setupCode = Environment.GetEnvironmentVariable("BORGSTUDIO_E2E_SETUP_CODE") ?? throw new InvalidOperationException("BORGSTUDIO_E2E_SETUP_CODE is missing.");
        var borg = Environment.GetEnvironmentVariable("BORGSTUDIO_E2E_BORG") ?? "borg";
        Directory.CreateDirectory(work);

        // Set up the server and sign in, like an administrator in the browser.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            CookieContainer = new CookieContainer(),
        };
        using var browser = new Browser(new HttpClient(handler) { BaseAddress = new Uri(url) }, "en");
        var page = await browser.GetAsync("/");
        page = await browser.SubmitAsync(page, "setup", new()
        {
            ["Input.SetupCode"] = setupCode,
            ["Input.ServerName"] = "E2E",
            ["Input.UserName"] = TestServer.AdminName,
            ["Input.Email"] = string.Empty,
            ["Input.Password"] = TestServer.AdminPassword,
            ["Input.ConfirmPassword"] = TestServer.AdminPassword,
        });
        page = await browser.SubmitAsync(page, "skip-two-factor");
        await browser.SubmitAsync(page, "reveal-codes");

        // Pair an app.
        using var app = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
        {
            BaseAddress = new Uri(url),
        };
        var started = await JsonAsync(await app.PostAsJsonAsync("/api/v1/pairing", new { deviceName = "e2e", platform = "Linux", appVersion = "test" }));
        page = await browser.GetAsync($"/pair?code={started.GetProperty("userCode").GetString()}");
        page = await browser.SubmitAsync(page, "approve-pairing");
        Assert.Contains("The app is paired", page.Text);
        var completed = await JsonAsync(await app.PostAsJsonAsync("/api/v1/pairing/complete", new { pairingCode = started.GetProperty("pairingCode").GetString() }));
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", completed.GetProperty("token").GetString());

        // The SSH service runs; its host key comes from the interface, so ssh can check it strictly.
        var server = await WaitForSshAsync(app);
        var ssh = server.GetProperty("ssh");
        var hostKey = ssh.GetProperty("hostKeys")[0].GetProperty("publicKey").GetString();
        var port = ssh.GetProperty("port").GetInt32();
        File.WriteAllText(Path.Combine(work, "known_hosts"), $"[localhost]:{port} {hostKey}\n");

        // A repository and the app's key for it.
        var repository = await JsonAsync(await app.PostAsJsonAsync("/api/v1/repositories", new { name = "e2e" }));
        var id = repository.GetProperty("id").GetGuid();
        var key = await NewKeyAsync("app");
        var registered = await JsonAsync(await app.PutAsJsonAsync($"/api/v1/repositories/{id}/ssh-key", new { publicKey = await File.ReadAllTextAsync(key + ".pub") }));
        var repositoryUrl = registered.GetProperty("url").GetString()!;
        Assert.Equal($"ssh://{ssh.GetProperty("user").GetString()}@localhost:{port}/./{id:N}", repositoryUrl);

        // borg as usual: create the repository, back up, list.
        var source = Path.Combine(work, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "Hello from BorgStudio Server");
        await BorgAsync(borg, key, "init", "--encryption=repokey-blake2", repositoryUrl);
        await BorgAsync(borg, key, "create", $"{repositoryUrl}::first", source);
        Assert.Contains("first", await BorgAsync(borg, key, "list", "--short", repositoryUrl));

        // The server noted the access and measured the repository.
        var stats = await JsonAsync(await app.GetAsync($"/api/v1/repositories/{id}"));
        Assert.Equal("e2e", stats.GetProperty("lastAccess").GetProperty("by").GetString());
        Assert.True(stats.GetProperty("usedBytes").GetInt64() > 0);

        // A key only opens its own repository; unknown keys do not get in.
        var other = await JsonAsync(await app.PostAsJsonAsync("/api/v1/repositories", new { name = "other" }));
        var otherUrl = repositoryUrl.Replace($"{id:N}", $"{other.GetProperty("id").GetGuid():N}", StringComparison.Ordinal);
        Assert.NotEqual(0, (await RunBorgAsync(borg, key, "list", otherUrl)).ExitCode);
        var stranger = await NewKeyAsync("stranger");
        Assert.NotEqual(0, (await RunBorgAsync(borg, stranger, "list", repositoryUrl)).ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {body}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }

    private static async Task<JsonElement> WaitForSshAsync(HttpClient app)
    {
        for (var attempt = 0; ; attempt++)
        {
            var server = await JsonAsync(await app.GetAsync("/api/v1/server"));
            var state = server.GetProperty("ssh").GetProperty("state").GetString();
            if (state == "running" || attempt == 30)
            {
                Assert.True(state == "running", $"SSH service: {state} {server.GetProperty("ssh").GetProperty("message")}");
                return server;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<string> NewKeyAsync(string name)
    {
        var key = Path.Combine(work, name);
        var result = await RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", string.Empty, "-C", name, "-f", key], null);
        Assert.True(result.ExitCode == 0, result.Output);
        return key;
    }

    private async Task<string> BorgAsync(string borg, string key, params string[] arguments)
    {
        var result = await RunBorgAsync(borg, key, arguments);
        Assert.True(result.ExitCode == 0, $"borg {string.Join(' ', arguments)}:\n{result.Output}");
        return result.Output;
    }

    private Task<(int ExitCode, string Output)> RunBorgAsync(string borg, string key, params string[] arguments) =>
        RunAsync(borg, arguments, new Dictionary<string, string>
        {
            ["BORG_RSH"] = $"ssh -i {key} -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile={Path.Combine(work, "known_hosts")}",
            ["BORG_PASSPHRASE"] = "end-to-end",
            ["BORG_BASE_DIR"] = Path.Combine(work, "borg-base"),
        });

    private static async Task<(int ExitCode, string Output)> RunAsync(string program, string[] arguments, Dictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? [])
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
