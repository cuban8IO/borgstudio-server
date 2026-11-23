using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Application.Abstractions.Accounts;

/// <summary>User accounts, passwords, two-factor authentication and recovery codes.</summary>
public interface IAccountService
{
    Task<bool> HasUsersAsync(CancellationToken cancellationToken);

    Task<AccountResult<string>> CreateAsync(NewAccount account, CancellationToken cancellationToken);

    Task<AccountInfo?> GetAsync(string userId, CancellationToken cancellationToken);

    Task<AccountInfo?> FindByNameAsync(string userName, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountInfo>> GetInRoleAsync(string role, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountInfo>> ListAsync(CancellationToken cancellationToken);

    Task<AccountResult> SetUserNameAsync(string userId, string userName, CancellationToken cancellationToken);

    Task<AccountResult> SetEmailAsync(string userId, string? email, CancellationToken cancellationToken);

    /// <summary>Gives the account exactly this role.</summary>
    Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken);

    /// <summary>A disabled account cannot sign in; its sessions end at their next check.</summary>
    Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken);

    Task<AccountResult> DeleteAsync(string userId, CancellationToken cancellationToken);

    Task<bool> CheckPasswordAsync(string userId, string password, CancellationToken cancellationToken);

    Task<AccountResult> ChangePasswordAsync(
        string userId, string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>Replaces the password without asking for the old one.</summary>
    Task<AccountResult> SetPasswordAsync(string userId, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Uses up one recovery code of <paramref name="userName"/>. Wrong codes count as failed sign-ins
    /// and can lock the account.
    /// </summary>
    Task<CodeCheck> RedeemRecoveryCodeAsync(string userName, string code, CancellationToken cancellationToken);

    /// <summary>Creates a new set of recovery codes; all earlier codes stop working.</summary>
    Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(string userId, CancellationToken cancellationToken);

    /// <summary>The current authenticator key, or <c>null</c> if there is none.</summary>
    Task<string?> GetAuthenticatorKeyAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the first authenticator key of an account without one. Unlike a reset this keeps the
    /// security stamp, so open sessions are not signed out just because a QR code was shown.
    /// </summary>
    Task<string> CreateAuthenticatorKeyAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Creates a new authenticator key; the old one stops working at once.</summary>
    Task<string> ResetAuthenticatorKeyAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Checks a code from the authenticator app. Wrong codes count as failed sign-ins.</summary>
    Task<CodeCheck> VerifyAuthenticatorCodeAsync(string userId, string code, CancellationToken cancellationToken);

    Task SetTwoFactorEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken);

    Task SetTasksAsync(string userId, AccountTasks tasks, CancellationToken cancellationToken);

    Task SetThemeModeAsync(string userId, ThemeMode mode, CancellationToken cancellationToken);

    /// <summary>Lifts a lockout and resets the count of failed sign-ins.</summary>
    Task UnlockAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Signs the user out everywhere: existing sessions stop working at their next check.</summary>
    Task InvalidateSessionsAsync(string userId, CancellationToken cancellationToken);
}

/// <param name="Password">The first password; <c>null</c> for an invited user who sets it later.</param>
public sealed record NewAccount(
    string UserName,
    string? Email,
    string? Password,
    string Role,
    AccountTasks Tasks);

public sealed record AccountInfo(
    string Id,
    string UserName,
    string? Email,
    IReadOnlyList<string> Roles,
    bool TwoFactorEnabled,
    int RecoveryCodesLeft,
    AccountTasks Tasks,
    ThemeMode ThemeMode,
    DateTimeOffset? LockedOutUntil,
    bool IsDisabled,
    bool HasPassword)
{
    public bool IsAdministrator => Roles.Contains(Domain.Users.Roles.Administrator);
}

public enum CodeCheck
{
    Valid,
    Invalid,
    LockedOut,
}

/// <summary>Outcome of an account change; <see cref="Errors"/> are ready to show to the user.</summary>
public record AccountResult(IReadOnlyList<string> Errors)
{
    public static readonly AccountResult Success = new([]);

    public bool Succeeded => Errors.Count == 0;

    public static AccountResult Failed(params string[] errors) => new(errors);
}

public sealed record AccountResult<T>(T? Value, IReadOnlyList<string> Errors) : AccountResult(Errors)
{
    public static AccountResult<T> Ok(T value) => new(value, []);

    public static new AccountResult<T> Failed(params string[] errors) => new(default, errors);
}
