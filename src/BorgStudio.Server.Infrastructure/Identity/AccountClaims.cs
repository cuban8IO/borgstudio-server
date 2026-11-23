using System.Security.Claims;

using BorgStudio.Server.Domain.Users;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Infrastructure.Identity;

public static class AccountClaimTypes
{
    /// <summary>Present while <see cref="AccountTasks"/> are due; the value is the flags as a number.</summary>
    public const string PendingTasks = "borgstudio:pending-tasks";

    public static AccountTasks GetPendingTasks(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(PendingTasks), out var value) ? (AccountTasks)value : AccountTasks.None;
}

/// <summary>Adds the due account tasks to the sign-in cookie, so pages can be locked until they are done.</summary>
internal sealed class AccountClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.PendingTasks != AccountTasks.None)
        {
            identity.AddClaim(new Claim(AccountClaimTypes.PendingTasks, ((int)user.PendingTasks).ToString()));
        }

        return identity;
    }
}
