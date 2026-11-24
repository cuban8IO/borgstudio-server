using BorgStudio.Server.Infrastructure.Identity;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BorgStudio.Server.Web.Authentication;

/// <summary>Small sign-in related endpoints that only touch cookies and render nothing.</summary>
internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/account");

        account.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager, [FromForm] string? returnUrl) =>
            {
                await signInManager.SignOutAsync();
                return TypedResults.LocalRedirect(LocalUrl(returnUrl, "/account/login"));
            })
            .AllowAnonymous();

        // wwwroot/js/app.js calls this after 60 minutes without activity.
        account.MapGet("/logout-idle", async (SignInManager<ApplicationUser> signInManager) =>
            {
                await signInManager.SignOutAsync();
                return TypedResults.LocalRedirect("/account/login?status=idle");
            })
            .AllowAnonymous();

        // Issues a fresh cookie after the user changed their password or two-factor settings,
        // which renews the security stamp in the cookie.
        account.MapGet("/refresh", async Task<Results<RedirectHttpResult, NoContent, UnauthorizedHttpResult>> (
                HttpContext context,
                SignInManager<ApplicationUser> signInManager,
                string? returnUrl) =>
            {
                var user = await signInManager.UserManager.GetUserAsync(context.User);
                if (user is null)
                {
                    return TypedResults.Unauthorized();
                }

                await signInManager.RefreshSignInAsync(user);
                return string.IsNullOrEmpty(returnUrl)
                    ? TypedResults.NoContent()
                    : TypedResults.LocalRedirect(LocalUrl(returnUrl, "/"));
            })
            .RequireAuthorization(Policies.AccountTasks);

        // Activity on an interactive page renews the sliding sign-in cookie (see wwwroot/js/app.js).
        account.MapGet("/keepalive", () => TypedResults.NoContent())
            .RequireAuthorization(Policies.AccountTasks);

        return endpoints;
    }

    /// <summary>Only local paths, so a crafted link cannot send users to another site after signing in.</summary>
    public static string LocalUrl(string? url, string fallback) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : fallback;

    /// <summary>Ends the half-finished sign-in that waits for an authenticator code.</summary>
    public static Task ForgetTwoFactorUserAsync(this HttpContext context) =>
        context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
}
