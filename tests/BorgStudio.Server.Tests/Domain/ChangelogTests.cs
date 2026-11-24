using BorgStudio.Server.Domain.Changelogs;
using BorgStudio.Server.Infrastructure.Updates;

namespace BorgStudio.Server.Tests.Domain;

public sealed class ChangelogTests
{
    private const string Markdown = """
        # Changelog

        Some words.

        ## [0.2.0] - 2026-12-01

        ### New features
        - Add a changelog page (#12)
        - Something without a pull request

        ### Fixes
        - Fix a thing (#13)

        ## [0.1.0] - 2026-11-24

        ### New features
        - First version (#1)
        """;

    [Fact]
    public void Parse_ReadsReleasesSectionsAndPullRequests()
    {
        var releases = Changelog.Parse(Markdown);

        Assert.Equal([new Version(0, 2, 0), new Version(0, 1, 0)], releases.Select(r => r.Version));
        Assert.Equal(new DateOnly(2026, 12, 1), releases[0].Date);
        Assert.Equal(["New features", "Fixes"], releases[0].Sections.Select(s => s.Title));
        Assert.Equal(new ChangelogItem("Add a changelog page", 12), releases[0].Sections[0].Items[0]);
        Assert.Equal(new ChangelogItem("Something without a pull request", null), releases[0].Sections[0].Items[1]);
        Assert.Equal(new ChangelogItem("First version", 1), releases[1].Sections[0].Items[0]);
    }

    [Fact]
    public void ParseNotes_IgnoresTheInstallationTable()
    {
        var sections = Changelog.ParseNotes("### Fixes\n- Fix (#8)\n\n### Installation\n\n| Where | How |\n|---|---|\n| Linux | x |\n");

        var section = Assert.Single(sections);
        Assert.Equal("Fixes", section.Title);
        Assert.Single(section.Items);
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("v0.2.3", "0.2.3")]
    [InlineData("1.0", null)]
    [InlineData("0.1.0-beta", null)]
    [InlineData("1.2.3.4", null)]
    [InlineData(null, null)]
    public void ParseVersion(string? text, string? expected)
    {
        Assert.Equal(expected, Changelog.ParseVersion(text)?.ToString());
    }

    [Fact]
    public void TheRepositorysChangelog_CanBeRead()
    {
        var text = new EmbeddedChangelog().Read();

        var releases = Changelog.Parse(text);
        Assert.Contains(releases, r => r.Version == new Version(0, 1, 0));
        Assert.All(releases, r => Assert.NotEmpty(r.Sections));
    }

    [Fact]
    public void GitHubReleases_LeaveOutDraftsPreReleasesAndOddTags()
    {
        var selected = GitHubReleaseFeed.Select(
        [
            new("v0.1.0", "u1", "notes", DateTimeOffset.UtcNow, Draft: false, Prerelease: false),
            new("v0.3.0", "u3", "notes", DateTimeOffset.UtcNow, Draft: true, Prerelease: false),
            new("v0.4.0-rc1", "u4", "notes", DateTimeOffset.UtcNow, Draft: false, Prerelease: true),
            new("nightly", "u5", "notes", DateTimeOffset.UtcNow, Draft: false, Prerelease: false),
            new("v0.2.0", "u2", "notes", DateTimeOffset.UtcNow, Draft: false, Prerelease: false),
        ]);

        Assert.Equal([new Version(0, 2, 0), new Version(0, 1, 0)], selected.Select(r => r.Version));
    }
}
