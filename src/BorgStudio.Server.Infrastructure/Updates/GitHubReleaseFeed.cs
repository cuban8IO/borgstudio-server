using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

using BorgStudio.Server.Application.Abstractions.Updates;
using BorgStudio.Server.Domain.Changelogs;

namespace BorgStudio.Server.Infrastructure.Updates;

/// <summary>Reads the releases from the GitHub API (no account needed; nothing about this server is sent).</summary>
internal sealed class GitHubReleaseFeed(HttpClient http) : IReleaseFeed
{
    public const string Repository = "cuban8IO/borgstudio-server";

    public static void Configure(HttpClient http)
    {
        http.BaseAddress = new Uri("https://api.github.com/");
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BorgStudio-Server", null));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<IReadOnlyList<PublishedRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"repos/{Repository}/releases?per_page=30", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, cancellationToken: cancellationToken) ?? [];
        return Select(releases);
    }

    public async Task<PublishedRelease?> GetDevelopmentBuildAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"repos/{Repository}/releases/tags/dev", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken);
        return Development(release);
    }

    /// <summary>The development build's version comes from the pre-release's title.</summary>
    internal static PublishedRelease? Development(GitHubRelease? release) =>
        release is { Draft: false } && Changelog.FindDevelopmentBuild(release.Name) is { } name && Changelog.ParseDevelopmentBuild(name) is { } version
            ? new PublishedRelease(version, release.PublishedAt ?? DateTimeOffset.MinValue, release.Body ?? string.Empty, release.HtmlUrl ?? string.Empty, name)
            : null;

    internal static IReadOnlyList<PublishedRelease> Select(IEnumerable<GitHubRelease> releases) =>
        releases
            .Where(r => !r.Draft && !r.Prerelease)
            .Select(r => (Release: r, Version: Changelog.ParseVersion(r.TagName)))
            .Where(r => r.Version is not null)
            .Select(r => new PublishedRelease(r.Version!, r.Release.PublishedAt ?? DateTimeOffset.MinValue, r.Release.Body ?? string.Empty, r.Release.HtmlUrl ?? string.Empty))
            .OrderByDescending(r => r.Version)
            .ToList();

    internal sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("name")] string? Name = null);
}
