using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Accounts;
using BorgStudio.Server.Web.Authentication;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

/// <summary>A recovery code replaces password and authenticator; afterwards everything is renewed.</summary>
public sealed class RecoveryTests
{
    private const string NewPassword = "Battery-Staple-7";

    [Fact]
    public async Task WithoutTwoFactor_NewPasswordThenNewCodes()
    {
        using var server = new TestServer();
        IReadOnlyList<string> oldCodes;
        using (var setup = await server.SetUpAsync())
        {
            var page = await setup.SubmitAsync(setup.Last!, "skip-two-factor");
            oldCodes = (await setup.SubmitAsync(page, "reveal-codes")).RecoveryCodes();
        }

        using var browser = server.CreateBrowser();
        var current = await RecoverAsync(browser, oldCodes[0]);
        Assert.Equal("/account/complete", current.Path);
        Assert.Contains("Set a new password", current.Text);

        // Nothing else opens before the steps are done.
        Assert.Equal("/account/complete", (await browser.GetAsync("/account")).Path);

        current = await browser.SubmitAsync(current, "new-password", new()
        {
            ["PasswordInput.NewPassword"] = NewPassword,
            ["PasswordInput.ConfirmPassword"] = NewPassword,
        });
        Assert.Contains("Show my recovery codes", current.Text);
        Assert.DoesNotContain("Check your authenticator", current.Text);

        current = await browser.SubmitAsync(current, "reveal-codes");
        var newCodes = current.RecoveryCodes();
        Assert.Equal(10, newCodes.Count);
        Assert.Empty(newCodes.Intersect(oldCodes));
        Assert.Equal("/", (await browser.GetAsync("/account/complete")).Path);

        // The old codes stopped working, the new password works.
        using var other = server.CreateBrowser();
        Assert.Contains("recovery code is wrong", (await RecoverAsync(other, oldCodes[1])).Text);
        var signedIn = await SignInTests.SignInAsync(other, await other.GetAsync("/account/login"), NewPassword);
        Assert.Equal("/", signedIn.Path);
    }

    [Fact]
    public async Task WithTwoFactor_WorkingAuthenticatorStays()
    {
        using var server = new TestServer();
        var key = await SignInTests.CompleteSetupWithTwoFactorAsync(server);
        var code = await FirstRecoveryCodeAsync(server);

        using var browser = server.CreateBrowser();
        var page = await RecoverAsync(browser, code);
        page = await browser.SubmitAsync(page, "new-password", new()
        {
            ["PasswordInput.NewPassword"] = NewPassword,
            ["PasswordInput.ConfirmPassword"] = NewPassword,
        });
        Assert.Contains("Check your authenticator", page.Text);

        page = await browser.SubmitAsync(page, "verify-authenticator", new() { ["VerifyInput.Code"] = "123456" });
        Assert.Contains("The code is wrong or expired.", page.Text);

        page = await browser.SubmitAsync(page, "verify-authenticator", new() { ["VerifyInput.Code"] = Totp.Code(key) });
        page = await browser.SubmitAsync(page, "reveal-codes");
        Assert.Equal(10, page.RecoveryCodes().Count);

        // Same authenticator as before.
        using var other = server.CreateBrowser();
        page = await SignInTests.SignInAsync(other, await other.GetAsync("/account/login"), NewPassword);
        page = await other.SubmitAsync(page, "login-2fa", new() { ["Input.Code"] = Totp.Code(key) });
        Assert.Equal("/", page.Path);
    }

    [Fact]
    public async Task WithTwoFactor_LostAuthenticatorMustBeSetUpAgain()
    {
        using var server = new TestServer();
        var oldKey = await SignInTests.CompleteSetupWithTwoFactorAsync(server);
        var code = await FirstRecoveryCodeAsync(server);

        using var browser = server.CreateBrowser();
        var page = await RecoverAsync(browser, code);
        page = await browser.SubmitAsync(page, "new-password", new()
        {
            ["PasswordInput.NewPassword"] = NewPassword,
            ["PasswordInput.ConfirmPassword"] = NewPassword,
        });

        page = await browser.SubmitAsync(page, "lost-authenticator");
        Assert.Contains("Set up a new authenticator", page.Text);
        Assert.DoesNotContain("Continue without 2FA", page.Text);
        var newKey = page.AuthenticatorKey();
        Assert.NotEqual(oldKey, newKey);

        page = await browser.SubmitAsync(page, "enable-two-factor", new() { ["EnableInput.Code"] = Totp.Code(newKey) });
        Assert.Equal(10, page.RecoveryCodes().Count);
        Assert.Equal("/", (await browser.GetAsync("/account/complete")).Path);

        // The old authenticator no longer signs in, the new one does.
        using var other = server.CreateBrowser();
        page = await SignInTests.SignInAsync(other, await other.GetAsync("/account/login"), NewPassword);
        page = await other.SubmitAsync(page, "login-2fa", new() { ["Input.Code"] = Totp.Code(oldKey) });
        Assert.Contains("The code is wrong or expired.", page.Text);
        page = await other.SubmitAsync(page, "login-2fa", new() { ["Input.Code"] = Totp.Code(newKey) });
        Assert.Equal("/", page.Path);
    }

    [Fact]
    public async Task ResetAdminCommand_GivesATemporaryPassword()
    {
        using var server = new TestServer();
        await SignInTests.CompleteSetupWithTwoFactorAsync(server);

        using var output = new StringWriter();
        var exitCode = await ResetAdminConsole.RunAsync(server.Services, userName: null, output);
        Assert.Equal(0, exitCode);
        var password = output.ToString().Split('\n')
            .Single(line => line.StartsWith("Temporary password: ", StringComparison.Ordinal))["Temporary password: ".Length..].Trim();

        // Two-factor authentication is off: the temporary password alone signs in, then the steps follow.
        using var browser = server.CreateBrowser();
        var page = await SignInTests.SignInAsync(browser, await browser.GetAsync("/account/login"), password);
        Assert.Equal("/account/complete", page.Path);
        Assert.Contains("Set a new password", page.Text);

        page = await browser.SubmitAsync(page, "new-password", new()
        {
            ["PasswordInput.NewPassword"] = NewPassword,
            ["PasswordInput.ConfirmPassword"] = NewPassword,
        });
        Assert.Contains("Turn on two-factor authentication?", page.Text);
        page = await browser.SubmitAsync(page, "skip-two-factor");
        page = await browser.SubmitAsync(page, "reveal-codes");
        Assert.Equal(10, page.RecoveryCodes().Count);
    }

    [Fact]
    public async Task ResetAdminCommand_NamesTheAdministrator_WhenThereAreNone()
    {
        using var server = new TestServer();
        using var output = new StringWriter();

        var exitCode = await ResetAdminConsole.RunAsync(server.Services, userName: null, output);

        Assert.Equal(1, exitCode);
        Assert.Contains("no administrator yet", output.ToString());
    }

    private static async Task<Page> RecoverAsync(Browser browser, string code)
    {
        var page = await browser.GetAsync("/account/recover");
        return await browser.SubmitAsync(page, "recover", new()
        {
            ["Input.UserName"] = TestServer.AdminName,
            ["Input.Code"] = code,
        });
    }

    /// <summary>Creates a fresh set of recovery codes (as on the account page) and returns one of them.</summary>
    private static async Task<string> FirstRecoveryCodeAsync(TestServer server)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var result = await dispatcher.SendAsync(new RegenerateRecoveryCodesCommand(
            await SignInTests.AdminIdAsync(server), TestServer.AdminPassword));
        Assert.True(result.Succeeded);
        return result.Value![0];
    }
}
