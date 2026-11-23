using System.Net;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Language, colors and error pages of the running server.</summary>
public sealed class WebHostTests
{
    [Fact]
    public async Task Pages_AreEnglishByDefault()
    {
        using var server = new TestServer();
        using var browser = server.CreateBrowser();

        var page = await browser.GetAsync("/setup");

        Assert.Contains("<html lang=\"en\"", page.Html);
        Assert.Contains("Set up BorgStudio Server", page.Text);
    }

    [Fact]
    public async Task Pages_FollowTheBrowserLanguage()
    {
        using var server = new TestServer();
        using var browser = server.CreateBrowser("de-DE");

        var page = await browser.GetAsync("/setup");

        Assert.Contains("<html lang=\"de\"", page.Html);
        Assert.Contains("BorgStudio Server einrichten", page.Text);
        Assert.Contains("Einrichtungscode", page.Text);
    }

    [Fact]
    public async Task UnsupportedLanguage_FallsBackToEnglish()
    {
        using var server = new TestServer();
        using var browser = server.CreateBrowser("fr-FR");

        var page = await browser.GetAsync("/setup");

        Assert.Contains("<html lang=\"en\"", page.Html);
    }

    [Theory]
    [InlineData("light", null, "rgba(245,245,245,1)")]
    [InlineData("dark", "0", "rgba(20,20,20,1)")]
    [InlineData("system", "0", "rgba(245,245,245,1)")]
    [InlineData("system", "1", "rgba(20,20,20,1)")]
    [InlineData("system", null, "rgba(20,20,20,1)")]
    public async Task ThemeCookies_AreUsedOnTheFirstRender(string mode, string? systemDark, string expectedBackground)
    {
        using var server = new TestServer();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/setup");
        var cookie = $"borgstudio.theme={mode}";
        if (systemDark is not null)
        {
            cookie += $"; borgstudio.system-dark={systemDark}";
        }

        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        // Without a known browser setting, "system" renders dark like the app.
        Assert.Contains($"--mud-palette-background: {expectedBackground}", html);
    }

    [Fact]
    public async Task Overview_ShowsTheServerStatus()
    {
        using var server = new TestServer();
        using var browser = await SignInTests.CompleteSetupAsync(server, twoFactor: false);

        var page = await browser.GetAsync("/");

        Assert.Equal("/", page.Path);
        Assert.Contains("Test Server", page.Text);
        Assert.Contains("Operating system", page.Text);
        Assert.Contains("Running since", page.Text);
    }

    [Fact]
    public async Task UnknownPage_ShowsNotFound()
    {
        using var server = new TestServer();
        using var browser = await SignInTests.CompleteSetupAsync(server, twoFactor: false);

        var page = await browser.GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, page.Status);
        Assert.Contains("Page not found", page.Text);
    }
}
