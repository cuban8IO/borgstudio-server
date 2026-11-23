using BorgStudio.Server.Domain.Users;

using Microsoft.AspNetCore.Identity;

namespace BorgStudio.Server.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    /// <summary>Steps due right after the next sign-in, see <see cref="AccountTasks"/>.</summary>
    public AccountTasks PendingTasks { get; set; }

    public ThemeMode ThemeMode { get; set; }
}
