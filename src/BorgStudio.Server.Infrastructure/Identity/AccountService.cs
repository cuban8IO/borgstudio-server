using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Domain.Users;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Identity;

internal sealed class AccountService(UserManager<ApplicationUser> users) : IAccountService
{
    private const int RecoveryCodeCount = 10;

    public Task<bool> HasUsersAsync(CancellationToken cancellationToken) => users.Users.AnyAsync(cancellationToken);

    public async Task<AccountResult<string>> CreateAsync(NewAccount account, CancellationToken cancellationToken)
    {
        var user = new ApplicationUser
        {
            UserName = account.UserName,
            Email = account.Email,
            PendingTasks = account.Tasks,
        };

        var created = await users.CreateAsync(user, account.Password);
        if (!created.Succeeded)
        {
            return AccountResult<string>.Failed(Describe(created));
        }

        var added = await users.AddToRoleAsync(user, account.Role);
        if (!added.Succeeded)
        {
            await users.DeleteAsync(user);
            return AccountResult<string>.Failed(Describe(added));
        }

        return AccountResult<string>.Ok(user.Id);
    }

    public async Task<AccountInfo?> GetAsync(string userId, CancellationToken cancellationToken) =>
        await users.FindByIdAsync(userId) is { } user ? await ToInfoAsync(user) : null;

    public async Task<AccountInfo?> FindByNameAsync(string userName, CancellationToken cancellationToken) =>
        await users.FindByNameAsync(userName) is { } user ? await ToInfoAsync(user) : null;

    public async Task<IReadOnlyList<AccountInfo>> GetInRoleAsync(string role, CancellationToken cancellationToken)
    {
        var members = await users.GetUsersInRoleAsync(role);
        var result = new List<AccountInfo>(members.Count);
        foreach (var user in members)
        {
            result.Add(await ToInfoAsync(user));
        }

        return result;
    }

    public async Task<bool> CheckPasswordAsync(string userId, string password, CancellationToken cancellationToken) =>
        await users.FindByIdAsync(userId) is { } user && await users.CheckPasswordAsync(user, password);

    public async Task<AccountResult> ChangePasswordAsync(
        string userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);
        return ToResult(await users.ChangePasswordAsync(user, currentPassword, newPassword));
    }

    public async Task<AccountResult> SetPasswordAsync(string userId, string newPassword, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);

        // Check the new password first, so a rejected one never leaves the account without a password.
        // (A reset token would do the same, but needs the data protection keys, which the console
        // command may not be able to read.)
        var errors = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(users, user, newPassword);
            errors.AddRange(validation.Errors.Select(e => e.Description));
        }

        if (errors.Count > 0)
        {
            return AccountResult.Failed([.. errors]);
        }

        if (await users.HasPasswordAsync(user))
        {
            EnsureSucceeded(await users.RemovePasswordAsync(user));
        }

        return ToResult(await users.AddPasswordAsync(user, newPassword));
    }

    public async Task<CodeCheck> RedeemRecoveryCodeAsync(string userName, string code, CancellationToken cancellationToken)
    {
        var user = await users.FindByNameAsync(userName);
        if (user is null)
        {
            return CodeCheck.Invalid;
        }

        if (await users.IsLockedOutAsync(user))
        {
            return CodeCheck.LockedOut;
        }

        var normalized = code.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var redeemed = await users.RedeemTwoFactorRecoveryCodeAsync(user, normalized);
        return await CountAttemptAsync(user, redeemed.Succeeded);
    }

    public async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return codes?.ToList() ?? throw new InvalidOperationException("No recovery codes were created.");
    }

    public async Task<string?> GetAuthenticatorKeyAsync(string userId, CancellationToken cancellationToken) =>
        await users.GetAuthenticatorKeyAsync(await RequireAsync(userId));

    public async Task<string> ResetAuthenticatorKeyAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);
        await users.ResetAuthenticatorKeyAsync(user);
        return await users.GetAuthenticatorKeyAsync(user)
            ?? throw new InvalidOperationException("No authenticator key was created.");
    }

    public async Task<CodeCheck> VerifyAuthenticatorCodeAsync(string userId, string code, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);
        if (await users.IsLockedOutAsync(user))
        {
            return CodeCheck.LockedOut;
        }

        var normalized = code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var valid = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, normalized);
        return await CountAttemptAsync(user, valid);
    }

    public async Task SetTwoFactorEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken) =>
        EnsureSucceeded(await users.SetTwoFactorEnabledAsync(await RequireAsync(userId), enabled));

    public async Task SetTasksAsync(string userId, AccountTasks tasks, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);
        user.PendingTasks = tasks;
        EnsureSucceeded(await users.UpdateAsync(user));
    }

    public async Task SetThemeModeAsync(string userId, ThemeMode mode, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);
        user.ThemeMode = mode;
        EnsureSucceeded(await users.UpdateAsync(user));
    }

    public async Task UnlockAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId);
        EnsureSucceeded(await users.SetLockoutEndDateAsync(user, null));
        EnsureSucceeded(await users.ResetAccessFailedCountAsync(user));
    }

    public async Task InvalidateSessionsAsync(string userId, CancellationToken cancellationToken) =>
        EnsureSucceeded(await users.UpdateSecurityStampAsync(await RequireAsync(userId)));

    private async Task<CodeCheck> CountAttemptAsync(ApplicationUser user, bool succeeded)
    {
        if (succeeded)
        {
            await users.ResetAccessFailedCountAsync(user);
            return CodeCheck.Valid;
        }

        await users.AccessFailedAsync(user);
        return await users.IsLockedOutAsync(user) ? CodeCheck.LockedOut : CodeCheck.Invalid;
    }

    private async Task<AccountInfo> ToInfoAsync(ApplicationUser user) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.Email,
        (await users.GetRolesAsync(user)).ToList(),
        user.TwoFactorEnabled,
        await users.CountRecoveryCodesAsync(user),
        user.PendingTasks,
        user.ThemeMode,
        user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow ? end : null);

    private async Task<ApplicationUser> RequireAsync(string userId) =>
        await users.FindByIdAsync(userId) ?? throw new InvalidOperationException("The account does not exist.");

    private static AccountResult ToResult(IdentityResult result) =>
        result.Succeeded ? AccountResult.Success : AccountResult.Failed(Describe(result));

    private static string[] Describe(IdentityResult result) => result.Errors.Select(e => e.Description).ToArray();

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", Describe(result)));
        }
    }
}
