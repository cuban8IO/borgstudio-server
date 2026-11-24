using BorgStudio.Server.Domain.Users;

using Microsoft.AspNetCore.Identity;

namespace BorgStudio.Server.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    /// <summary>Steps due right after the next sign-in, see <see cref="AccountTasks"/>.</summary>
    public AccountTasks PendingTasks { get; set; }

    /// <summary>New accounts start dark, like the BorgStudio app.</summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;

    /// <summary>The newest version whose changes the user has been told about (administrators).</summary>
    public string? ChangelogSeenVersion { get; set; }

    /// <summary>Disabled by an administrator: cannot sign in until enabled again.</summary>
    public bool IsDisabled { get; set; }
}

/// <summary>Disabled accounts count as not confirmed, so the sign-in manager refuses them.</summary>
internal sealed class AccountConfirmation : IUserConfirmation<ApplicationUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(!user.IsDisabled);
}
