namespace BorgStudio.Server.Web.Theming;

/// <summary>The color scheme a user picked for the web interface.</summary>
public enum ThemeMode
{
    /// <summary>Follow the browser's or operating system's light/dark setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Stores the chosen <see cref="ThemeMode"/> in a cookie, so the first server render already uses
/// the right colors and the page does not flash.
/// </summary>
public static class ThemePreference
{
    public const string CookieName = "borgstudio.theme";

    public static ThemeMode Parse(string? cookieValue) => cookieValue?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    public static string ToCookieValue(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "system",
    };

    /// <summary>Whether to show the dark palette for <paramref name="mode"/>.</summary>
    /// <param name="systemPrefersDark">The browser's setting; <c>null</c> while it is not known yet (first server render).</param>
    public static bool IsDark(ThemeMode mode, bool? systemPrefersDark) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        // Dark until the browser says otherwise: BorgStudio's home is the dark theme.
        _ => systemPrefersDark ?? true,
    };
}
