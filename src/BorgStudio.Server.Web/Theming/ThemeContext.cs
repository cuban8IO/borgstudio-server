using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Web.Theming;

/// <summary>The color scheme of the first request, from its cookies; cascaded to the layouts.</summary>
/// <param name="Mode">What the user picked.</param>
/// <param name="SystemPrefersDark">The browser's setting, if already known.</param>
public sealed record ThemeContext(ThemeMode Mode, bool? SystemPrefersDark)
{
    public bool IsDark => ThemePreference.IsDark(Mode, SystemPrefersDark);
}
