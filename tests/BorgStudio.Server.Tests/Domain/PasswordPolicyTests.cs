using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Tests.Domain;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("Correct-Horse-9", true)]
    [InlineData("Übergröße-7x", true)]
    [InlineData("short-A1", true)]
    [InlineData("Short-1", false)]
    [InlineData("no-upper-case-1", false)]
    [InlineData("NO-LOWER-CASE-1", false)]
    [InlineData("No-Digits-Here", false)]
    [InlineData("NoSymbols123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMet(string? password, bool expected)
    {
        Assert.Equal(expected, PasswordPolicy.IsMet(password));
    }
}
