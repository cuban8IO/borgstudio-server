using System.Globalization;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Settings;
using BorgStudio.Server.Application.Updates;
using BorgStudio.Server.Application.Users;
using BorgStudio.Server.Domain.Users;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Changelog page, the daily update check and the hints administrators get.</summary>
public sealed class UpdateTests
{
    private const string NewNotes = "### New features\n- Add a changelog page and update notices (#12)\n\n### Fixes\n- Fix something (#13)\n\n### Installation\n\n| Where | How |\n|---|---|\n";

    [Fact]
    public async Task ChangelogPage_ShowsTheReleasesOfThisBuild()
    {
        using var server = new TestServer { Version = "0.1.1" };
        using var browser = await SignInTests.CompleteSetupAsync(server, twoFactor: false);

        var page = await new Browser(browser.Client, "de-DE").GetAsync("/changelog");

        Assert.Equal("/changelog", page.Path);
        Assert.Contains("Dieser Server läuft mit Version 0.1.1.", page.Text);
        Assert.Contains("Fehlerbehebungen", page.Text);
        Assert.Contains("Neue Funktionen", page.Text);
        Assert.Contains("Use the template of the host's architecture", page.Text);
        Assert.Contains("https://github.com/cuban8IO/borgstudio-server/pull/8", page.Html);
        Assert.Contains("installiert", page.Text);
    }

    [Fact]
    public async Task NewerRelease_IsShownToAdministratorsOnly()
    {
        using var server = new TestServer();
        server.Releases.Add("0.1.0", "### New features\n- First (#1)");
        server.Releases.Add("0.2.0", NewNotes);
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        var adminId = await SignInTests.AdminIdAsync(server);

        var status = await DispatchAsync(server, new RunUpdateCheckCommand());
        Assert.Equal(new Version(0, 2, 0), status.Latest!.Version);
        Assert.Single(status.Newer);

        var overview = await admin.GetAsync("/");
        Assert.Contains("BorgStudio Server 0.2.0 is available.", overview.Text);

        var changelog = await admin.GetAsync("/changelog");
        Assert.Contains("Version 0.2.0 is available", changelog.Text);
        Assert.Contains("Add a changelog page and update notices", changelog.Text);
        Assert.DoesNotContain("| Where | How |", changelog.Text);

        // Users get no update hints.
        var grant = await DispatchAsync(server, new CreateUserCommand(
            adminId, "alice", null, Roles.User, NewUserAccess.TemporaryPassword, 7, "http://localhost/"));
        using var alice = server.CreateBrowser();
        var page = await SignInAsync(alice, "alice", grant.Value!.TemporaryPassword!);
        page = await alice.SubmitAsync(page, "new-password", new()
        {
            ["PasswordInput.NewPassword"] = "Alice-Secret-42",
            ["PasswordInput.ConfirmPassword"] = "Alice-Secret-42",
        });
        page = await alice.SubmitAsync(page, "skip-two-factor");
        await alice.SubmitAsync(page, "reveal-codes");
        Assert.DoesNotContain("is available", (await alice.GetAsync("/")).Text);
        Assert.DoesNotContain("Version 0.2.0 is available", (await alice.GetAsync("/changelog")).Text);
    }

    [Fact]
    public async Task Check_RespectsTheSetting_AndSurvivesGitHubBeingDown()
    {
        using var server = new TestServer();
        server.Releases.Add("0.2.0", NewNotes);
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);

        await DispatchAsync(server, new UpdateServerSettingsCommand(adminId, "Test Server", null, CheckForUpdates: false));
        Assert.Null((await DispatchAsync(server, new RunUpdateCheckCommand())).CheckedAt);

        await DispatchAsync(server, new UpdateServerSettingsCommand(adminId, "Test Server", null, CheckForUpdates: true));
        Assert.Equal(new Version(0, 2, 0), (await DispatchAsync(server, new CheckForUpdatesNowCommand(adminId))).Latest!.Version);

        server.Releases.Fails = true;
        var failed = await DispatchAsync(server, new RunUpdateCheckCommand());
        Assert.True(failed.Failed);
        Assert.Equal(new Version(0, 2, 0), failed.Latest!.Version);
    }

    [Fact]
    public async Task DevelopmentBuilds_AreNotChecked()
    {
        using var server = new TestServer { Version = "0.0.0-dev" };
        server.Releases.Add("0.2.0", NewNotes);
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();

        Assert.Null((await DispatchAsync(server, new RunUpdateCheckCommand())).Latest);
    }

    [Fact]
    public async Task AfterAnUpdate_AdministratorsAreToldOnce()
    {
        using var server = new TestServer { Version = "0.1.0" };
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);

        // The first visit only remembers the running version.
        Assert.DoesNotContain("was updated to", (await admin.GetAsync("/")).Text);

        server.Version = "0.1.1";
        Assert.Contains("BorgStudio Server was updated to 0.1.1.", (await admin.GetAsync("/")).Text);
        Assert.Contains("BorgStudio Server was updated to 0.1.1.", (await admin.GetAsync("/account")).Text);

        // Reading the changelog counts as seen.
        await admin.GetAsync("/changelog");
        Assert.DoesNotContain("was updated to", (await admin.GetAsync("/")).Text);
    }

    [Fact]
    public async Task SettingsPage_ShowsTheUpdateCheck()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);

        var page = await admin.GetAsync("/settings");

        Assert.Contains("Look for new versions daily", page.Text);
        Assert.Contains("Not checked yet", page.Text);
    }

    private static async Task<Page> SignInAsync(Browser browser, string userName, string password)
    {
        var page = await browser.GetAsync("/account/login");
        return await browser.SubmitAsync(page, "login", new()
        {
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "false",
        });
    }

    private static async Task<TResult> DispatchAsync<TResult>(TestServer server, ICommand<TResult> command)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        return await server.Services.GetRequiredService<IDispatcher>().SendAsync(command);
    }
}
