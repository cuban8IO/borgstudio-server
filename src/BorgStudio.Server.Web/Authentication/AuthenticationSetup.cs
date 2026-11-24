using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Infrastructure;
using BorgStudio.Server.Infrastructure.Identity;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace BorgStudio.Server.Web.Authentication;

/// <summary>Names of the authorization policies.</summary>
public static class Policies
{
    /// <summary>Signed in, even while account tasks (new password, recovery codes …) are still due.</summary>
    public const string AccountTasks = "AccountTasks";

    /// <summary>Signed in with nothing left to do, and an administrator.</summary>
    public const string Administrator = "Administrator";

    /// <summary>The interface: a valid token of a paired app or an API token (no cookies).</summary>
    public const string Api = "Api";
}

/// <summary>How long a sign-in lasts.</summary>
public static class SessionLifetime
{
    /// <summary>Without "stay signed in": ends after this long without activity.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(60);

    /// <summary>With "stay signed in".</summary>
    public static readonly TimeSpan Persistent = TimeSpan.FromDays(14);
}

public static class AuthenticationSetup
{
    public const string CookieName = "borgstudio.auth";

    public static IServiceCollection AddWebAuthentication(this IServiceCollection services, ServerPaths paths)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, Api.ApiTokenAuthenticationHandler>(Api.ApiTokenAuthenticationHandler.SchemeName, null);

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/account/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/account/access-denied";
            options.ReturnUrlParameter = "returnUrl";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = SessionLifetime.Persistent;
            // Only this one event: replacing the events object would drop Identity's security stamp
            // check (OnValidatePrincipal), and with it sign-outs after password or account changes.
            options.Events.OnSigningIn = context =>
            {
                // Sliding expiration renews with the same span, so this sets the idle limit per session kind.
                var lifetime = context.Properties.IsPersistent ? SessionLifetime.Persistent : SessionLifetime.Idle;
                var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                context.Properties.IssuedUtc = now;
                context.Properties.ExpiresUtc = now + lifetime;
                context.Properties.AllowRefresh = true;
                return Task.CompletedTask;
            };
        });

        // Password changes, resets and two-factor changes sign out other sessions within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthorization(options =>
        {
            var signedIn = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !context.User.HasClaim(c => c.Type == AccountClaimTypes.PendingTasks))
                .Build();

            // Every page needs a complete sign-in unless it says otherwise ([AllowAnonymous] or a policy).
            options.DefaultPolicy = signedIn;
            options.FallbackPolicy = signedIn;
            options.AddPolicy(Policies.AccountTasks, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(Policies.Administrator, policy => policy.Combine(signedIn).RequireRole(Roles.Administrator));
            options.AddPolicy(Policies.Api, policy => policy
                .AddAuthenticationSchemes(Api.ApiTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());
        });

        // Keys of the sign-in cookies live in the data folder, so restarts and updates keep everyone signed in.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDirectory))
            .SetApplicationName("BorgStudio Server");

        return services;
    }
}
