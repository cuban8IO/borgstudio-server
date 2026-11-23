using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Mvc.Testing;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Starts the real web host in memory and requests pages like a browser would.</summary>
public sealed class WebHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Overview_RendersInEnglishByDefault()
    {
        var html = await GetAsync("/", language: null);

        Assert.Contains("<html lang=\"en\"", html);
        Assert.Contains("BorgStudio Server", html);
        Assert.Contains("Overview", html);
        Assert.Contains("Operating system", html);
    }

    [Fact]
    public async Task Overview_FollowsTheBrowserLanguage()
    {
        var html = await GetAsync("/", language: "de-DE");

        Assert.Contains("<html lang=\"de\"", html);
        Assert.Contains("Übersicht", html);
        Assert.Contains("Betriebssystem", html);
    }

    [Fact]
    public async Task UnsupportedLanguage_FallsBackToEnglish()
    {
        var html = await GetAsync("/", language: "fr-FR");

        Assert.Contains("<html lang=\"en\"", html);
    }

    [Theory]
    [InlineData("light", "rgba(245,245,245,1)")]
    [InlineData("dark", "rgba(20,20,20,1)")]
    [InlineData("system", "rgba(20,20,20,1)")]
    public async Task ThemeCookie_IsUsedOnTheFirstRender(string cookieValue, string expectedBackground)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", $"borgstudio.theme={cookieValue}");

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        // Without a known browser setting ("system"), the first render is dark like the app.
        Assert.Contains($"--mud-palette-background: {expectedBackground}", html);
    }

    [Fact]
    public async Task UnknownPage_ShowsNotFound()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/does-not-exist");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", html);
    }

    private async Task<string> GetAsync(string path, string? language)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (language is not null)
        {
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));
        }

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        // Blazor encodes non-ASCII characters (Ü becomes &#xDC;).
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }
}
