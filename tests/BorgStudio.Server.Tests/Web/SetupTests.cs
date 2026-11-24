namespace BorgStudio.Server.Tests.Web;

public sealed class SetupTests
{
    [Fact]
    public async Task FreshServer_LeadsEveryPageToTheWizard()
    {
        using var server = new TestServer();
        using var browser = server.CreateBrowser();

        var page = await browser.GetAsync("/account");

        Assert.Equal("/setup", page.Path);
        Assert.Contains("Set up BorgStudio Server", page.Text);
        Assert.True(File.Exists(Path.Combine(server.DataDirectory, "setup-code.txt")));
    }

    [Fact]
    public async Task WrongSetupCode_IsRejected()
    {
        using var server = new TestServer();
        using var browser = server.CreateBrowser();
        var page = await browser.GetAsync("/setup");

        page = await browser.SubmitAsync(page, "setup", new()
        {
            ["Input.SetupCode"] = "AAAA-BBBB-CCCC-DDDD",
            ["Input.UserName"] = TestServer.AdminName,
            ["Input.Password"] = TestServer.AdminPassword,
            ["Input.ConfirmPassword"] = TestServer.AdminPassword,
        });

        Assert.Equal("/setup", page.Path);
        Assert.Contains("The setup code is wrong.", page.Text);
    }

    [Fact]
    public async Task WeakPassword_IsRejectedWithTheRules()
    {
        using var server = new TestServer();
        using var browser = server.CreateBrowser("de-DE");
        var page = await browser.GetAsync("/setup");

        page = await browser.SubmitAsync(page, "setup", new()
        {
            ["Input.SetupCode"] = server.SetupCode,
            ["Input.UserName"] = TestServer.AdminName,
            ["Input.Password"] = "short",
            ["Input.ConfirmPassword"] = "short",
        });

        Assert.Equal("/setup", page.Path);
        Assert.Contains("Das Passwort braucht mindestens 8 Zeichen.", page.Text);
    }

    [Fact]
    public async Task Setup_CreatesTheAdministrator_OffersTwoFactor_AndShowsRecoveryCodesOnce()
    {
        using var server = new TestServer();
        using var browser = await server.SetUpAsync("Backups at home");
        var page = browser.Last!;

        Assert.Equal("/account/complete", page.Path);
        Assert.Contains("Turn on two-factor authentication?", page.Text);
        Assert.Contains("Continue without 2FA", page.Text);
        Assert.Contains("Backups at home", page.Text);
        Assert.Contains("--mud-palette-background: rgba(20,20,20,1)", page.Html);
        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "setup-code.txt")));

        // Everything else stays closed until the steps are done.
        Assert.Equal("/account/complete", (await browser.GetAsync("/")).Path);

        page = await browser.SubmitAsync(page, "skip-two-factor");
        Assert.Contains("Show my recovery codes", page.Text);
        Assert.Contains("you need them even without two-factor authentication", page.Text);

        page = await browser.SubmitAsync(page, "reveal-codes");
        var codes = page.RecoveryCodes();
        Assert.Equal(10, codes.Count);

        // Shown once: afterwards the overview opens and the codes are gone.
        page = await browser.GetAsync("/account/complete");
        Assert.Equal("/", page.Path);
        Assert.Empty(page.RecoveryCodes());
        Assert.Contains("Backups at home", page.Text);

        // The wizard is gone for good.
        Assert.Equal("/", (await browser.GetAsync("/setup")).Path);
    }

    [Fact]
    public async Task Setup_WithTwoFactor_ShowsCodesRightAfterTurningItOn()
    {
        using var server = new TestServer();
        using var browser = await server.SetUpAsync();
        var page = browser.Last!;

        page = await browser.SubmitAsync(page, "enable-two-factor", new() { ["EnableInput.Code"] = Totp.Code(page.AuthenticatorKey()) });

        Assert.Equal(10, page.RecoveryCodes().Count);
        Assert.Equal("/", (await browser.GetAsync("/account/complete")).Path);
    }
}
