using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Infrastructure.Identity;
using BorgStudio.Server.Web.Theming;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

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

    /// <summary>
    /// Renews the sign-in cookie with the account as it is now (pending tasks, security stamp).
    /// Uses a fresh service scope: the request's own one may still hold the user as it was loaded
    /// before a command changed it, and a cookie with that old stamp would be rejected.
    /// </summary>
    public static async Task RefreshSignInAsync(HttpContext context, string userId)
    {
        await using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        signInManager.Context = context;
        var user = await signInManager.UserManager.FindByIdAsync(userId);
        if (user is not null)
        {
            await signInManager.RefreshSignInAsync(user);
        }
    }

    /// <summary>Codes from authenticator apps are often typed or pasted with spaces or dashes.</summary>
    public static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
}
