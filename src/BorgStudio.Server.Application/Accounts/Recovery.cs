using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain.Users;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Accounts;

/// <summary>
/// Signs in with user name and recovery code, without password or authenticator. The user then has
/// to set a new password, prove or set up the authenticator again (if two-factor authentication is on)
/// and gets new recovery codes.
/// </summary>
public sealed record SignInWithRecoveryCodeCommand(
    [property: Required] string UserName,
    [property: Required] string Code) : ICommand<RecoverySignInResult>;

public sealed record RecoverySignInResult(CodeCheck Status, string? UserId);

internal sealed class SignInWithRecoveryCodeCommandHandler(
    IAccountService accounts,
    ILogger<SignInWithRecoveryCodeCommandHandler> logger) : ICommandHandler<SignInWithRecoveryCodeCommand, RecoverySignInResult>
{
    public async Task<RecoverySignInResult> HandleAsync(SignInWithRecoveryCodeCommand command, CancellationToken cancellationToken)
    {
        // A disabled account cannot get in, not even with a valid code (which stays unused).
        if (await accounts.FindByNameAsync(command.UserName.Trim(), cancellationToken) is { IsDisabled: true })
        {
            return new RecoverySignInResult(CodeCheck.Invalid, null);
        }

        var status = await accounts.RedeemRecoveryCodeAsync(command.UserName.Trim(), command.Code, cancellationToken);
        if (status != CodeCheck.Valid)
        {
            return new RecoverySignInResult(status, null);
        }

        var account = await accounts.FindByNameAsync(command.UserName.Trim(), cancellationToken)
            ?? throw new InvalidOperationException("The account disappeared while its recovery code was used.");

        var tasks = account.Tasks | AccountTasks.ChangePassword | AccountTasks.ShowRecoveryCodes;
        if (account.TwoFactorEnabled)
        {
            tasks |= AccountTasks.VerifyAuthenticator;
        }

        await accounts.SetTasksAsync(account.Id, tasks, cancellationToken);
        logger.LogInformation("{UserName} signed in with a recovery code.", account.UserName);
        return new RecoverySignInResult(CodeCheck.Valid, account.Id);
    }
}

/// <summary>Sets the new password that is due after a recovery or a temporary password.</summary>
public sealed record SetNewPasswordCommand(
    [property: Required] string UserId,
    [property: Required] string NewPassword,
    [property: Required] string ConfirmPassword) : ICommand<AccountResult>;

internal sealed class SetNewPasswordCommandHandler(IAccountService accounts, IAccountTexts texts)
    : ICommandHandler<SetNewPasswordCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(SetNewPasswordCommand command, CancellationToken cancellationToken)
    {
        var account = await AccountGuard.RequireTaskAsync(accounts, command.UserId, AccountTasks.ChangePassword, cancellationToken);
        if (command.NewPassword != command.ConfirmPassword)
        {
            return AccountResult.Failed(texts.PasswordsDoNotMatch);
        }

        var result = await accounts.SetPasswordAsync(command.UserId, command.NewPassword, cancellationToken);
        if (result.Succeeded)
        {
            await accounts.SetTasksAsync(command.UserId, account.Tasks & ~AccountTasks.ChangePassword, cancellationToken);
        }

        return result;
    }
}

/// <summary>After a recovery: the user shows a current code, so the authenticator stays as it is.</summary>
public sealed record VerifyAuthenticatorCommand(
    [property: Required] string UserId,
    [property: Required] string Code) : ICommand<CodeCheck>;

internal sealed class VerifyAuthenticatorCommandHandler(IAccountService accounts)
    : ICommandHandler<VerifyAuthenticatorCommand, CodeCheck>
{
    public async Task<CodeCheck> HandleAsync(VerifyAuthenticatorCommand command, CancellationToken cancellationToken)
    {
        var account = await AccountGuard.RequireTaskAsync(accounts, command.UserId, AccountTasks.VerifyAuthenticator, cancellationToken);
        var check = await accounts.VerifyAuthenticatorCodeAsync(command.UserId, command.Code, cancellationToken);
        if (check == CodeCheck.Valid)
        {
            await accounts.SetTasksAsync(command.UserId, account.Tasks & ~AccountTasks.VerifyAuthenticator, cancellationToken);
        }

        return check;
    }
}

/// <summary>Creates the recovery codes that are due and returns them; they are shown this one time.</summary>
public sealed record RevealRecoveryCodesCommand([property: Required] string UserId) : ICommand<IReadOnlyList<string>>;

internal sealed class RevealRecoveryCodesCommandHandler(IAccountService accounts)
    : ICommandHandler<RevealRecoveryCodesCommand, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> HandleAsync(RevealRecoveryCodesCommand command, CancellationToken cancellationToken)
    {
        var account = await AccountGuard.RequireTaskAsync(accounts, command.UserId, AccountTasks.ShowRecoveryCodes, cancellationToken);
        var codes = await accounts.ReplaceRecoveryCodesAsync(command.UserId, cancellationToken);
        await accounts.SetTasksAsync(command.UserId, account.Tasks & ~AccountTasks.ShowRecoveryCodes, cancellationToken);
        return codes;
    }
}

/// <summary>Creates new recovery codes on request; the password confirms it is really the user.</summary>
public sealed record RegenerateRecoveryCodesCommand(
    [property: Required] string UserId,
    [property: Required] string Password) : ICommand<AccountResult<IReadOnlyList<string>>>;

internal sealed class RegenerateRecoveryCodesCommandHandler(IAccountService accounts, IAccountTexts texts)
    : ICommandHandler<RegenerateRecoveryCodesCommand, AccountResult<IReadOnlyList<string>>>
{
    public async Task<AccountResult<IReadOnlyList<string>>> HandleAsync(
        RegenerateRecoveryCodesCommand command, CancellationToken cancellationToken)
    {
        if (!await accounts.CheckPasswordAsync(command.UserId, command.Password, cancellationToken))
        {
            return AccountResult<IReadOnlyList<string>>.Failed(texts.WrongPassword);
        }

        var codes = await accounts.ReplaceRecoveryCodesAsync(command.UserId, cancellationToken);
        return AccountResult<IReadOnlyList<string>>.Ok(codes);
    }
}

internal static class AccountGuard
{
    /// <summary>The account, after making sure <paramref name="task"/> is really due for it.</summary>
    public static async Task<AccountInfo> RequireTaskAsync(
        IAccountService accounts, string userId, AccountTasks task, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("The account does not exist.");
        if (!account.Tasks.HasFlag(task))
        {
            throw new InvalidOperationException($"{task} is not due for this account.");
        }

        return account;
    }
}
