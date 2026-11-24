using System.Net;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Accounts;
using BorgStudio.Server.Domain.Users;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

public sealed class SignInTests
{
    [Fact]
    public async Task SignedOut_PagesLeadToTheSignInPage()
    {
        using var server = new TestServer();
        (await CompleteSetupAsync(server, twoFactor: false)).Dispose();
        using var browser = server.CreateBrowser();

        var page = await browser.GetAsync("/account");

        Assert.StartsWith("/account/login", page.Path);
        Assert.Contains("returnUrl=%2Faccount", page.Path);
    }

    [Fact]
    public async Task Password_SignsIn_AndReturnsToThePageAskedFor()
    {
        using var server = new TestServer();
        (await CompleteSetupAsync(server, twoFactor: false)).Dispose();
        using var browser = server.CreateBrowser();

        var page = await browser.GetAsync("/account");
        page = await SignInAsync(browser, page, TestServer.AdminPassword);

        Assert.Equal("/account", page.Path);
        Assert.Contains("Change password", page.Text);
    }

    [Fact]
    public async Task FiveWrongPasswords_LockTheAccount()
    {
        using var server = new TestServer();
        (await CompleteSetupAsync(server, twoFactor: false)).Dispose();
        using var browser = server.CreateBrowser();
        var page = await browser.GetAsync("/account/login");

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            page = await SignInAsync(browser, page, "Wrong-Password-1");
            Assert.Contains("User name or password is wrong.", page.Text);
        }

        page = await SignInAsync(browser, page, "Wrong-Password-1");
        Assert.Contains("locked for 15 minutes", page.Text);

        // Even the right password does not help now.
        page = await SignInAsync(browser, page, TestServer.AdminPassword);
        Assert.Contains("locked for 15 minutes", page.Text);
    }

    [Fact]
    public async Task TwoFactor_AsksForTheAuthenticatorCode()
    {
        using var server = new TestServer();
        var key = await CompleteSetupWithTwoFactorAsync(server);
        using var browser = server.CreateBrowser();

        var page = await SignInAsync(browser, await browser.GetAsync("/account/login"), TestServer.AdminPassword);
        Assert.StartsWith("/account/login-2fa", page.Path);

        page = await browser.SubmitAsync(page, "login-2fa", new() { ["Input.Code"] = "000000" });
        Assert.Contains("The code is wrong or expired.", page.Text);

        page = await browser.SubmitAsync(page, "login-2fa", new() { ["Input.Code"] = Totp.Code(key) });
        Assert.Equal("/", page.Path);
    }

    [Fact]
    public async Task SignIn_UsesTheUsersColorScheme()
    {
        using var server = new TestServer();
        (await CompleteSetupAsync(server, twoFactor: false)).Dispose();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            var admin = await dispatcher.QueryAsync(new GetAccountQuery(await AdminIdAsync(server)));
            await dispatcher.SendAsync(new SetThemeModeCommand(admin!.Id, ThemeMode.Light));
        }

        using var browser = server.CreateBrowser();
        var login = await browser.GetAsync("/account/login");
        Assert.Contains("--mud-palette-background: rgba(20,20,20,1)", login.Html);

        var page = await SignInAsync(browser, login, TestServer.AdminPassword);
        Assert.Contains("--mud-palette-background: rgba(245,245,245,1)", page.Html);
    }

    [Fact]
    public async Task SignOut_EndsTheSession()
    {
        using var server = new TestServer();
        using var browser = await CompleteSetupAsync(server, twoFactor: false);
        Assert.Equal("/", (await browser.GetAsync("/")).Path);

        // The sign-out form sits in the user menu, which only renders when opened in a real browser;
        // any form of the signed-in user carries a valid token.
        var token = AntiforgeryToken((await browser.GetAsync("/account/recover")).Html);
        using var logout = await browser.Client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        Assert.StartsWith("/account/login", (await browser.GetAsync("/")).Path);
    }

    internal static async Task<Page> SignInAsync(Browser browser, Page page, string password, bool rememberMe = false) =>
        await browser.SubmitAsync(page, "login", new()
        {
            ["Input.UserName"] = TestServer.AdminName,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = rememberMe ? "true" : "false",
        });

    /// <summary>Setup plus all account steps; the returned browser is signed in.</summary>
    internal static async Task<Browser> CompleteSetupAsync(TestServer server, bool twoFactor)
    {
        var browser = await server.SetUpAsync();
        var page = browser.Last!;
        if (twoFactor)
        {
            await browser.SubmitAsync(page, "enable-two-factor", new() { ["EnableInput.Code"] = Totp.Code(page.AuthenticatorKey()) });
        }
        else
        {
            page = await browser.SubmitAsync(page, "skip-two-factor");
            await browser.SubmitAsync(page, "reveal-codes");
        }

        return browser;
    }

    /// <summary>Setup with two-factor authentication; returns the authenticator key.</summary>
    internal static async Task<string> CompleteSetupWithTwoFactorAsync(TestServer server)
    {
        using var browser = await server.SetUpAsync();
        var page = browser.Last!;
        var key = page.AuthenticatorKey();
        await browser.SubmitAsync(page, "enable-two-factor", new() { ["EnableInput.Code"] = Totp.Code(key) });
        return key;
    }

    internal static async Task<string> AdminIdAsync(TestServer server)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<global::BorgStudio.Server.Infrastructure.Identity.ApplicationUser>>();
        return (await users.FindByNameAsync(TestServer.AdminName))!.Id;
    }

    private static string AntiforgeryToken(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return html[start..html.IndexOf('"', start)];
    }
}
