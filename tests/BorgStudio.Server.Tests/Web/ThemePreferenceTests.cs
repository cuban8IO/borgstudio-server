using BorgStudio.Server.Web.Theming;

namespace BorgStudio.Server.Tests.Web;

public sealed class ThemePreferenceTests
{
    [Theory]
    [InlineData("light", ThemeMode.Light)]
    [InlineData("DARK", ThemeMode.Dark)]
    [InlineData(" system ", ThemeMode.System)]
    [InlineData("purple", ThemeMode.System)]
    [InlineData(null, ThemeMode.System)]
    public void Parse(string? cookieValue, ThemeMode expected)
    {
        Assert.Equal(expected, ThemePreference.Parse(cookieValue));
    }

    [Theory]
    [InlineData(ThemeMode.System)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void CookieValue_RoundTrips(ThemeMode mode)
    {
        Assert.Equal(mode, ThemePreference.Parse(ThemePreference.ToCookieValue(mode)));
    }

    [Theory]
    [InlineData(ThemeMode.Light, true, false)]
    [InlineData(ThemeMode.Dark, false, true)]
    [InlineData(ThemeMode.System, false, false)]
    [InlineData(ThemeMode.System, true, true)]
    [InlineData(ThemeMode.System, null, true)]
    public void IsDark(ThemeMode mode, bool? systemPrefersDark, bool expected)
    {
        Assert.Equal(expected, ThemePreference.IsDark(mode, systemPrefersDark));
    }
}
