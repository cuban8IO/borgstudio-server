using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Web.Theming;

/// <summary>
/// Cookies that let the first server render use the right colors, so the page does not flash:
/// the chosen <see cref="ThemeMode"/> (also stored per user) and the browser's own light/dark setting.
/// </summary>
public static class ThemePreference
{
    public const string CookieName = "borgstudio.theme";

    /// <summary>Written by wwwroot/js/app.js on every page load: "1" when the browser prefers dark colors.</summary>
    public const string SystemDarkCookieName = "borgstudio.system-dark";

    /// <summary>The chosen mode; without a (valid) choice BorgStudio is dark, like the app.</summary>
    public static ThemeMode Parse(string? cookieValue) => cookieValue?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "system" => ThemeMode.System,
        _ => ThemeMode.Dark,
    };

    public static string ToCookieValue(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "system",
    };

    /// <summary>The browser's setting from its cookie; <c>null</c> when it is not known yet (first visit).</summary>
    public static bool? ParseSystemDark(string? cookieValue) => cookieValue switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    /// <summary>Whether to show the dark palette for <paramref name="mode"/>.</summary>
    /// <param name="systemPrefersDark">The browser's setting; <c>null</c> while it is not known.</param>
    public static bool IsDark(ThemeMode mode, bool? systemPrefersDark) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        // Dark until the browser says otherwise: BorgStudio's home is the dark theme.
        _ => systemPrefersDark ?? true,
    };

    /// <summary>Writes the chosen mode, e.g. right after signing in with the mode stored for the user.</summary>
    public static void WriteCookie(HttpResponse response, ThemeMode mode) =>
        response.Cookies.Append(CookieName, ToCookieValue(mode), new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
            SameSite = SameSiteMode.Lax,
            Secure = response.HttpContext.Request.IsHttps,
            HttpOnly = false,
            IsEssential = true,
        });
}
