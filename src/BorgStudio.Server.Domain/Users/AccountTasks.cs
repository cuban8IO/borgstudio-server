namespace BorgStudio.Server.Domain.Users;

/// <summary>
/// Steps a user has to go through right after signing in, before the rest of the server opens up.
/// They run in the order of their values.
/// </summary>
[Flags]
public enum AccountTasks
{
    None = 0,

    /// <summary>Set a new password (after signing in with a recovery code or a temporary password).</summary>
    ChangePassword = 1,

    /// <summary>
    /// Two-factor authentication is on, but the user signed in with a recovery code: prove the
    /// authenticator still works, or set it up again. Cannot be skipped.
    /// </summary>
    VerifyAuthenticator = 2,

    /// <summary>Offer to turn on two-factor authentication. Can be skipped.</summary>
    OfferTwoFactor = 4,

    /// <summary>Create new recovery codes and show them once.</summary>
    ShowRecoveryCodes = 8,
}

public static class AccountTasksExtensions
{
    private static readonly AccountTasks[] Order =
    [
        AccountTasks.ChangePassword,
        AccountTasks.VerifyAuthenticator,
        AccountTasks.OfferTwoFactor,
        AccountTasks.ShowRecoveryCodes,
    ];

    /// <summary>The step to show next, or <see cref="AccountTasks.None"/> when nothing is left.</summary>
    public static AccountTasks Next(this AccountTasks tasks) =>
        Order.FirstOrDefault(task => tasks.HasFlag(task), AccountTasks.None);
}
