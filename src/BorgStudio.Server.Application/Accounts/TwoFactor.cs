using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Application.Accounts;

/// <summary>
/// Gets the authenticator key to set up an app with. <paramref name="StartOver"/> creates a new key,
/// e.g. when the old authenticator is lost; the old key stops working at once.
/// </summary>
public sealed record PrepareAuthenticatorCommand(
    [property: Required] string UserId,
    bool StartOver = false) : ICommand<AuthenticatorSetup>;

public sealed record AuthenticatorSetup(string SharedKey, string Uri, string QrCodeDataUri);

internal sealed class PrepareAuthenticatorCommandHandler(
    IAccountService accounts,
    IServerSettingsStore settings,
    IAuthenticatorPresenter presenter) : ICommandHandler<PrepareAuthenticatorCommand, AuthenticatorSetup>
{
    public async Task<AuthenticatorSetup> HandleAsync(PrepareAuthenticatorCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.UserId, cancellationToken)
            ?? throw new InvalidOperationException("The account does not exist.");

        // An account with working two-factor authentication keeps its key, unless the user lost it.
        string? key;
        if (command.StartOver)
        {
            if (account.TwoFactorEnabled && !account.Tasks.HasFlag(AccountTasks.VerifyAuthenticator))
            {
                throw new InvalidOperationException(
                    "The authenticator of an account with working two-factor authentication cannot be replaced.");
            }

            key = await accounts.ResetAuthenticatorKeyAsync(command.UserId, cancellationToken);
        }
        else
        {
            // The key of an active authenticator is never shown again: whoever holds only a session
            // (or a recovery code) must not be able to copy it.
            if (account.TwoFactorEnabled)
            {
                throw new InvalidOperationException("The key of an active authenticator is not shown again.");
            }

            key = await accounts.GetAuthenticatorKeyAsync(command.UserId, cancellationToken);
            if (string.IsNullOrEmpty(key))
            {
                key = await accounts.ResetAuthenticatorKeyAsync(command.UserId, cancellationToken);
            }
        }

        var issuer = (await settings.GetAsync(cancellationToken)).ServerName;
        var uri = presenter.CreateUri(issuer, account.UserName, key);
        return new AuthenticatorSetup(presenter.FormatKey(key), uri, presenter.CreateQrCodeDataUri(uri));
    }
}

/// <summary>
/// Turns on two-factor authentication (or confirms a newly set up authenticator) with a code from the
/// app and returns new recovery codes to show once.
/// </summary>
public sealed record EnableTwoFactorCommand(
    [property: Required] string UserId,
    [property: Required] string Code) : ICommand<EnableTwoFactorResult>;

public sealed record EnableTwoFactorResult(CodeCheck Status, IReadOnlyList<string> RecoveryCodes);

internal sealed class EnableTwoFactorCommandHandler(IAccountService accounts)
    : ICommandHandler<EnableTwoFactorCommand, EnableTwoFactorResult>
{
    private const AccountTasks Done =
        AccountTasks.VerifyAuthenticator | AccountTasks.OfferTwoFactor | AccountTasks.ShowRecoveryCodes;

    public async Task<EnableTwoFactorResult> HandleAsync(EnableTwoFactorCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.UserId, cancellationToken)
            ?? throw new InvalidOperationException("The account does not exist.");

        var check = await accounts.VerifyAuthenticatorCodeAsync(command.UserId, command.Code, cancellationToken);
        if (check != CodeCheck.Valid)
        {
            return new EnableTwoFactorResult(check, []);
        }

        await accounts.SetTwoFactorEnabledAsync(command.UserId, true, cancellationToken);
        var codes = await accounts.ReplaceRecoveryCodesAsync(command.UserId, cancellationToken);
        await accounts.SetTasksAsync(command.UserId, account.Tasks & ~Done, cancellationToken);
        return new EnableTwoFactorResult(CodeCheck.Valid, codes);
    }
}

/// <summary>The user does not want two-factor authentication now.</summary>
public sealed record SkipTwoFactorCommand([property: Required] string UserId) : ICommand;

internal sealed class SkipTwoFactorCommandHandler(IAccountService accounts) : ICommandHandler<SkipTwoFactorCommand>
{
    public async Task HandleAsync(SkipTwoFactorCommand command, CancellationToken cancellationToken)
    {
        var account = await AccountGuard.RequireTaskAsync(accounts, command.UserId, AccountTasks.OfferTwoFactor, cancellationToken);
        await accounts.SetTasksAsync(command.UserId, account.Tasks & ~AccountTasks.OfferTwoFactor, cancellationToken);
    }
}

/// <summary>Turns two-factor authentication off; the password confirms it is really the user.</summary>
public sealed record DisableTwoFactorCommand(
    [property: Required] string UserId,
    [property: Required] string Password) : ICommand<AccountResult>;

internal sealed class DisableTwoFactorCommandHandler(IAccountService accounts, IAccountTexts texts)
    : ICommandHandler<DisableTwoFactorCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(DisableTwoFactorCommand command, CancellationToken cancellationToken)
    {
        if (!await accounts.CheckPasswordAsync(command.UserId, command.Password, cancellationToken))
        {
            return AccountResult.Failed(texts.WrongPassword);
        }

        await accounts.SetTwoFactorEnabledAsync(command.UserId, false, cancellationToken);
        // A later setup starts with a fresh key, so an old copy of the key is worthless.
        await accounts.ResetAuthenticatorKeyAsync(command.UserId, cancellationToken);
        return AccountResult.Success;
    }
}
