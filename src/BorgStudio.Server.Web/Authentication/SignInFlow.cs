using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Infrastructure.Identity;
using BorgStudio.Server.Web.Theming;

using Microsoft.AspNetCore.Components;

namespace BorgStudio.Server.Web.Authentication;

/// <summary>What happens right after a successful sign-in, whichever way it went.</summary>
internal static class SignInFlow
{
    public const string CompletePath = "/account/complete";

    public static void Continue(HttpContext context, NavigationManager navigation, ApplicationUser user, string? returnUrl)
    {
        // The user's own color scheme replaces whatever this browser had.
        ThemePreference.WriteCookie(context.Response, user.ThemeMode);
        navigation.NavigateTo(user.PendingTasks != AccountTasks.None
            ? CompletePath
            : AccountEndpoints.LocalUrl(returnUrl, "/"));
    }

    /// <summary>Codes from authenticator apps are often typed or pasted with spaces or dashes.</summary>
    public static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
}
