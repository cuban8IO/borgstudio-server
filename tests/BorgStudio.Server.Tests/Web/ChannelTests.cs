using BorgStudio.Server.Application.Abstractions.Updates;
using BorgStudio.Server.Application.Updates;
using BorgStudio.Server.Domain.Changelogs;
using BorgStudio.Server.Infrastructure.Updates;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Development builds: their versions, the update check on the dev channel, and what the pages say.</summary>
public sealed class ChannelTests
{
    [Theory]
    [InlineData("0.5.0-dev.42", "0.5.0.42")]
    [InlineData("v1.2.3-dev.7", "1.2.3.7")]
    [InlineData("0.5.0", null)]
    [InlineData("0.0.0-dev", null)]
    [InlineData("0.0.0-ci.12", null)]
    [InlineData("0.5.0-dev.x", null)]
    public void DevelopmentBuildVersions(string text, string? expected) =>
        Assert.Equal(expected, Changelog.ParseDevelopmentBuild(text)?.ToString());

    [Fact]
    public void ThePreReleaseTitle_NamesTheBuild()
    {
        var build = GitHubReleaseFeed.Development(new GitHubReleaseFeed.GitHubRelease(
            "dev", "https://github.com/x", "### New features\n- Thing (#20)", DateTimeOffset.UtcNow, false, true, "BorgStudio Server 0.5.0-dev.42 (development build)"))!;
        Assert.Equal("0.5.0-dev.42", build.Label);
        Assert.Equal(new Version(0, 5, 0, 42), build.Version);
        Assert.Null(GitHubReleaseFeed.Development(new GitHubReleaseFeed.GitHubRelease("dev", null, null, null, false, true, "Something else")));
    }

    [Fact]
    public async Task DevelopmentBuilds_LookForTheNextDevelopmentBuild()
    {
        using var server = new TestServer { Version = "0.5.0-dev.42" };
        server.Releases.Add("0.4.0", "### New features\n- Old (#1)");
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);

        Assert.Empty((await Run.SendAsync(server, new RunUpdateCheckCommand())).Newer);

        server.Releases.DevelopmentBuild = new PublishedRelease(new Version(0, 5, 0, 45), DateTimeOffset.UtcNow, "### Fixes\n- Fix (#21)", "https://github.com/x", "0.5.0-dev.45");
        var status = await Run.SendAsync(server, new RunUpdateCheckCommand());
        Assert.Equal("0.5.0-dev.45", status.Latest!.Label);

        var notices = await Run.SendAsync(server, new GetUpdateNoticesCommand(adminId));
        Assert.Equal("0.5.0-dev.45", notices.Available);
        Assert.True((await Run.QueryAsync(server, new GetUpdateChannelQuery())).IsDevelopment);

        var changelog = await Run.QueryAsync(server, new GetChangelogQuery(adminId));
        Assert.Equal("0.5.0-dev.45", changelog.Update!.Latest);
        Assert.Equal("0.5.0-dev.45", Assert.Single(changelog.Update.Releases).Label);
    }

    [Fact]
    public async Task Settings_ShowTheChannel()
    {
        using var server = new TestServer { Version = "0.5.0-dev.42" };
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        Assert.Contains("Kanal: dev", (await new Browser(admin.Client, "de-DE").GetAsync("/settings")).Text);

        server.Version = "0.4.0";
        Assert.False((await Run.QueryAsync(server, new GetUpdateChannelQuery())).IsDevelopment);
        Assert.Contains("Channel: stable", (await admin.GetAsync("/settings")).Text);
    }
}
