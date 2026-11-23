using BorgStudio.Server.Domain.Settings;
using BorgStudio.Server.Domain.Setup;
using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Tests.Domain;

public sealed class AccountRulesTests
{
    [Fact]
    public void SetupCode_HasFourGroupsWithoutLookalikes()
    {
        var code = SetupCode.Generate();

        Assert.Matches("^[A-HJ-KMNP-Z2-9]{4}(-[A-HJ-KMNP-Z2-9]{4}){3}$", code);
        Assert.NotEqual(code, SetupCode.Generate());
    }

    [Theory]
    [InlineData("K7QF-M2XA-9PDR-HT4W", "k7qf m2xa 9pdr ht4w", true)]
    [InlineData("K7QF-M2XA-9PDR-HT4W", "K7QFM2XA9PDRHT4W", true)]
    [InlineData("K7QF-M2XA-9PDR-HT4W", "K7QF-M2XA-9PDR-HT4X", false)]
    [InlineData(null, "", false)]
    [InlineData("", "", false)]
    public void SetupCode_Matches(string? expected, string entered, bool matches)
    {
        Assert.Equal(matches, SetupCode.Matches(expected, entered));
    }

    [Theory]
    [InlineData(AccountTasks.None, AccountTasks.None)]
    [InlineData(AccountTasks.ShowRecoveryCodes | AccountTasks.ChangePassword, AccountTasks.ChangePassword)]
    [InlineData(AccountTasks.ShowRecoveryCodes | AccountTasks.VerifyAuthenticator, AccountTasks.VerifyAuthenticator)]
    [InlineData(AccountTasks.ShowRecoveryCodes | AccountTasks.OfferTwoFactor, AccountTasks.OfferTwoFactor)]
    [InlineData(AccountTasks.ShowRecoveryCodes, AccountTasks.ShowRecoveryCodes)]
    public void AccountTasks_RunInOrder(AccountTasks tasks, AccountTasks next)
    {
        Assert.Equal(next, tasks.Next());
    }

    [Fact]
    public void ServerName_FallsBackToTheDefault()
    {
        var settings = new ServerSettings();

        settings.Rename("  Backups at home  ");
        Assert.Equal("Backups at home", settings.ServerName);

        settings.Rename(" ");
        Assert.Equal(ServerSettings.DefaultServerName, settings.ServerName);

        Assert.Throws<ArgumentException>(() => settings.Rename(new string('x', ServerSettings.MaxServerNameLength + 1)));
    }

    [Fact]
    public void TemporaryPassword_MeetsEveryRule()
    {
        for (var i = 0; i < 50; i++)
        {
            var password = Server.Application.Accounts.TemporaryPassword.Generate();

            Assert.Equal(20, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));
        }
    }
}
