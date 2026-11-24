using System.Globalization;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Accounts;
using BorgStudio.Server.Application.Settings;
using BorgStudio.Server.Application.Users;
using BorgStudio.Server.Domain.Users;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

public sealed class UserManagementTests
{
    private const string BaseUrl = "http://localhost/";
    private const string AlicePassword = "Alice-Secret-42";

    [Fact]
    public async Task TemporaryPassword_MustBeChangedAtTheFirstSignIn()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);

        var grant = await SendAsync(server, new CreateUserCommand(
            adminId, "alice", "alice@example.org", Roles.User, NewUserAccess.TemporaryPassword, 7, BaseUrl));
        Assert.True(grant.Succeeded);
        Assert.Null(grant.Value!.InvitationLink);

        using var browser = server.CreateBrowser();
        var page = await SignInAsync(browser, "alice", grant.Value.TemporaryPassword!);
        Assert.Equal("/account/complete", page.Path);
        Assert.Contains("Set a new password", page.Text);

        page = await FinishFirstSignInAsync(browser, page, AlicePassword);
        Assert.Equal("/", page.Path);

        // Users are not administrators: the user list stays closed.
        var users = await browser.GetAsync("/users");
        Assert.StartsWith("/account/access-denied", users.Path);
        Assert.Contains("No access", users.Text);
        Assert.StartsWith("/account/access-denied", (await browser.GetAsync("/settings")).Path);
    }

    [Fact]
    public async Task InvitationLink_LetsTheUserSetTheirPassword_Once()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);

        var grant = await SendAsync(server, new CreateUserCommand(
            adminId, "alice", null, Roles.User, NewUserAccess.Invitation, 7, BaseUrl));
        var link = grant.Value!.InvitationLink!;
        Assert.StartsWith("http://localhost/account/invitation?token=", link);
        Assert.Null(grant.Value.TemporaryPassword);

        var summary = (await QueryAsync(server, new GetUsersQuery(adminId))).Single(u => u.Account.UserName == "alice");
        Assert.False(summary.Account.HasPassword);
        Assert.NotNull(summary.InvitationExpiresAt);

        using var browser = server.CreateBrowser();
        var page = await browser.GetAsync(new Uri(link).PathAndQuery);
        Assert.Contains("You were invited as alice", page.Text);

        page = await browser.SubmitAsync(page, "accept-invitation", new()
        {
            ["Input.Password"] = AlicePassword,
            ["Input.ConfirmPassword"] = AlicePassword,
        });
        Assert.Equal("/account/complete", page.Path);
        Assert.Contains("Turn on two-factor authentication?", page.Text);

        page = await browser.SubmitAsync(page, "skip-two-factor");
        page = await browser.SubmitAsync(page, "reveal-codes");
        Assert.Equal(10, page.RecoveryCodes().Count);

        // Used up.
        using var other = server.CreateBrowser();
        Assert.Contains("invitation link is invalid", (await other.GetAsync(new Uri(link).PathAndQuery)).Text);
        Assert.Equal("/", (await SignInAsync(other, "alice", AlicePassword)).Path);
    }

    [Fact]
    public async Task InvitationLink_Expires()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var grant = await SendAsync(server, new CreateUserCommand(
            adminId, "alice", null, Roles.User, NewUserAccess.Invitation, 1, BaseUrl));

        server.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        using var browser = server.CreateBrowser();
        Assert.Contains("invitation link is invalid", (await browser.GetAsync(new Uri(grant.Value!.InvitationLink!).PathAndQuery)).Text);
        var summary = (await QueryAsync(server, new GetUsersQuery(adminId))).Single(u => u.Account.UserName == "alice");
        Assert.True(summary.InvitationExpired);

        // A new link replaces the old one.
        var again = await SendAsync(server, new CreateInvitationCommand(adminId, summary.Account.Id, 30, BaseUrl));
        Assert.Contains("You were invited as alice", (await browser.GetAsync(new Uri(again.Value!.InvitationLink!).PathAndQuery)).Text);
    }

    [Fact]
    public async Task InvitationLink_UsesThePublicAddress()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);

        var invalid = await SendAsync(server, new UpdateServerSettingsCommand(adminId, "Home", "ftp://backup.example.org"));
        Assert.False(invalid.Succeeded);

        var saved = await SendAsync(server, new UpdateServerSettingsCommand(adminId, "Home", "https://backup.example.org/borg"));
        Assert.True(saved.Succeeded);
        Assert.Equal("https://backup.example.org/borg/", (await QueryAsync(server, new GetServerSettingsQuery())).PublicUrl);

        var grant = await SendAsync(server, new CreateUserCommand(
            adminId, "alice", null, Roles.User, NewUserAccess.Invitation, 7, BaseUrl));
        Assert.StartsWith("https://backup.example.org/borg/account/invitation?token=", grant.Value!.InvitationLink);
    }

    [Fact]
    public async Task RevokedInvitation_StopsWorking()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var grant = await SendAsync(server, new CreateUserCommand(
            adminId, "alice", null, Roles.User, NewUserAccess.Invitation, 7, BaseUrl));

        await SendAsync(server, new RevokeInvitationCommand(adminId, grant.Value!.UserId));

        using var browser = server.CreateBrowser();
        Assert.Contains("invitation link is invalid", (await browser.GetAsync(new Uri(grant.Value.InvitationLink!).PathAndQuery)).Text);
    }

    [Fact]
    public async Task Administrators_CannotLockThemselvesOut()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);

        Assert.Contains("own account", Error(await SendAsync(server,
            new UpdateUserCommand(adminId, adminId, TestServer.AdminName, null, Roles.User))));
        Assert.Contains("own account", Error(await SendAsync(server, new SetUserDisabledCommand(adminId, adminId, true))));
        Assert.Contains("own account", Error(await SendAsync(server, new DeleteUserCommand(adminId, adminId))));

        // Without the self check, the last administrator is protected as well.
        await using var scope = server.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var self = await admin.RequireTargetAsync(adminId, CancellationToken.None);
        Assert.False(await admin.OtherAdministratorRemainsAsync(self, CancellationToken.None));
    }

    [Fact]
    public async Task OnlyAdministrators_ManageUsers()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => QueryAsync(server, new GetUsersQuery(alice)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            SendAsync(server, new DeleteUserCommand(alice, adminId)));
    }

    [Fact]
    public async Task EditingUsers_ChecksNamesAndChangesRoles()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId);

        var duplicate = await SendAsync(server, new UpdateUserCommand(adminId, alice, TestServer.AdminName, null, Roles.User));
        Assert.Contains("already taken", Error(duplicate));

        var promoted = await SendAsync(server, new UpdateUserCommand(adminId, alice, "alice.b", "alice@example.org", Roles.Administrator));
        Assert.True(promoted.Succeeded);
        var account = await QueryAsync(server, new GetAccountQuery(alice));
        Assert.Equal("alice.b", account!.UserName);
        Assert.Equal("alice@example.org", account.Email);
        Assert.True(account.IsAdministrator);
    }

    [Fact]
    public async Task DisabledAccount_CannotSignIn_AndLosesItsSession()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId);
        using var browser = server.CreateBrowser();
        Assert.Equal("/", (await SignInAsync(browser, "alice", AlicePassword)).Path);

        Assert.True((await SendAsync(server, new SetUserDisabledCommand(adminId, alice, true))).Succeeded);
        server.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.StartsWith("/account/login", (await browser.GetAsync("/")).Path);
        Assert.Contains("This account is disabled.", (await SignInAsync(browser, "alice", AlicePassword)).Text);

        await SendAsync(server, new SetUserDisabledCommand(adminId, alice, false));
        Assert.Equal("/", (await SignInAsync(browser, "alice", AlicePassword)).Path);
    }

    [Fact]
    public async Task ResetPassword_GivesANewTemporaryPassword_AndEndsSessions()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId);
        using var browser = server.CreateBrowser();
        await SignInAsync(browser, "alice", AlicePassword);

        var grant = await SendAsync(server, new ResetUserPasswordCommand(adminId, alice));
        server.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.StartsWith("/account/login", (await browser.GetAsync("/")).Path);
        var page = await SignInAsync(browser, "alice", grant.Value!.TemporaryPassword!);
        Assert.Contains("Set a new password", page.Text);
    }

    [Fact]
    public async Task TurningOffTwoFactor_OffersItAgain()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId, twoFactor: true);

        Assert.True((await SendAsync(server, new DisableUserTwoFactorCommand(adminId, alice))).Succeeded);

        using var browser = server.CreateBrowser();
        var page = await SignInAsync(browser, "alice", AlicePassword);
        Assert.Equal("/account/complete", page.Path);
        Assert.Contains("Turn on two-factor authentication?", page.Text);
    }

    [Fact]
    public async Task Unlock_LiftsTheLockout()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId);
        using var browser = server.CreateBrowser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync(browser, "alice", "Wrong-Password-1");
        }

        Assert.NotNull((await QueryAsync(server, new GetAccountQuery(alice)))!.LockedOutUntil);
        await SendAsync(server, new UnlockUserCommand(adminId, alice));

        Assert.Equal("/", (await SignInAsync(browser, "alice", AlicePassword)).Path);
    }

    [Fact]
    public async Task DeletedUser_IsGone()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId);

        Assert.True((await SendAsync(server, new DeleteUserCommand(adminId, alice))).Succeeded);

        Assert.Null(await QueryAsync(server, new GetAccountQuery(alice)));
        using var browser = server.CreateBrowser();
        Assert.Contains("User name or password is wrong.", (await SignInAsync(browser, "alice", AlicePassword)).Text);
    }

    [Fact]
    public async Task SettingsPage_ShowsServerAndCertificate()
    {
        using var server = new TestServer();
        using var browser = await SignInTests.CompleteSetupAsync(server, twoFactor: false);

        var page = await browser.GetAsync("/settings");

        Assert.Equal("/settings", page.Path);
        Assert.Contains("Test Server", page.Text);
        Assert.Contains("HTTPS certificate", page.Text);
        // The test server runs without HTTPS: no self-signed certificate is made just for the page.
        Assert.Contains("HTTPS is off in this installation", page.Text);
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "tls")));
    }

    [Fact]
    public async Task OwnEmail_NeedsThePassword_AndKeepsTheSession()
    {
        using var server = new TestServer();
        var adminId = await SetUpAsync(server);
        var alice = await CreateAliceAsync(server, adminId);
        using var browser = server.CreateBrowser();
        await SignInAsync(browser, "alice", AlicePassword);

        Assert.False((await SendAsync(server, new ChangeOwnEmailCommand(alice, "a@example.org", "wrong"))).Succeeded);
        Assert.True((await SendAsync(server, new ChangeOwnEmailCommand(alice, "a@example.org", AlicePassword))).Succeeded);

        Assert.Equal("a@example.org", (await QueryAsync(server, new GetAccountQuery(alice)))!.Email);
        Assert.Equal("/", (await browser.GetAsync("/")).Path);
    }

    private static async Task<string> SetUpAsync(TestServer server)
    {
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        return await SignInTests.AdminIdAsync(server);
    }

    /// <summary>Creates "alice" (role User) and goes through her first sign-in; returns her id.</summary>
    private static async Task<string> CreateAliceAsync(TestServer server, string adminId, bool twoFactor = false)
    {
        var grant = await SendAsync(server, new CreateUserCommand(
            adminId, "alice", null, Roles.User, NewUserAccess.TemporaryPassword, 7, BaseUrl));
        using var browser = server.CreateBrowser();
        var page = await SignInAsync(browser, "alice", grant.Value!.TemporaryPassword!);
        page = await browser.SubmitAsync(page, "new-password", new()
        {
            ["PasswordInput.NewPassword"] = AlicePassword,
            ["PasswordInput.ConfirmPassword"] = AlicePassword,
        });
        if (twoFactor)
        {
            await browser.SubmitAsync(page, "enable-two-factor", new() { ["EnableInput.Code"] = Totp.Code(page.AuthenticatorKey()) });
        }
        else
        {
            page = await browser.SubmitAsync(page, "skip-two-factor");
            await browser.SubmitAsync(page, "reveal-codes");
        }

        return grant.Value.UserId;
    }

    private static async Task<Page> FinishFirstSignInAsync(Browser browser, Page page, string newPassword)
    {
        page = await browser.SubmitAsync(page, "new-password", new()
        {
            ["PasswordInput.NewPassword"] = newPassword,
            ["PasswordInput.ConfirmPassword"] = newPassword,
        });
        page = await browser.SubmitAsync(page, "skip-two-factor");
        page = await browser.SubmitAsync(page, "reveal-codes");
        return await browser.GetAsync("/account/complete");
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

    private static string Error(AccountResult result) => string.Join(" ", result.Errors);

    private static async Task<TResult> SendAsync<TResult>(TestServer server, ICommand<TResult> command)
    {
        English();
        var dispatcher = server.Services.GetRequiredService<IDispatcher>();
        return await dispatcher.SendAsync(command);
    }

    private static async Task SendAsync(TestServer server, ICommand command)
    {
        English();
        var dispatcher = server.Services.GetRequiredService<IDispatcher>();
        await dispatcher.SendAsync(command);
    }

    /// <summary>Messages in English, whatever language the machine running the tests has.</summary>
    private static void English() => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

    private static async Task<TResult> QueryAsync<TResult>(TestServer server, IQuery<TResult> query)
    {
        English();
        var dispatcher = server.Services.GetRequiredService<IDispatcher>();
        return await dispatcher.QueryAsync(query);
    }
}
