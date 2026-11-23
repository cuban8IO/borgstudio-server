using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Application.Abstractions.Setup;
using BorgStudio.Server.Application.Abstractions.Validation;
using BorgStudio.Server.Domain.Settings;
using BorgStudio.Server.Domain.Setup;
using BorgStudio.Server.Domain.Users;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Setup;

/// <summary>Whether the server still needs its first administrator.</summary>
public sealed record GetSetupStateQuery : IQuery<SetupState>;

public sealed record SetupState(bool IsRequired);

internal sealed class GetSetupStateQueryHandler(IAccountService accounts) : IQueryHandler<GetSetupStateQuery, SetupState>
{
    public async Task<SetupState> HandleAsync(GetSetupStateQuery query, CancellationToken cancellationToken) =>
        new(!await accounts.HasUsersAsync(cancellationToken));
}

/// <summary>
/// Finishes the setup wizard: names the server and creates the first administrator, who then gets
/// offered two-factor authentication and sees their recovery codes.
/// </summary>
public sealed record CompleteSetupCommand(
    [property: Required] string SetupCode,
    [property: MaxLength(ServerSettings.MaxServerNameLength)] string? ServerName,
    [property: Required] string UserName,
    [property: OptionalEmail] string? Email,
    [property: Required] string Password,
    [property: Required] string ConfirmPassword) : ICommand<AccountResult<string>>;

internal sealed class CompleteSetupCommandHandler(
    IAccountService accounts,
    ISetupCodeStore setupCodes,
    IServerSettingsStore settingsStore,
    IAccountTexts texts,
    ILogger<CompleteSetupCommandHandler> logger) : ICommandHandler<CompleteSetupCommand, AccountResult<string>>
{
    // Two browsers finishing the wizard at the same moment must not both create an administrator.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<AccountResult<string>> HandleAsync(CompleteSetupCommand command, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (await accounts.HasUsersAsync(cancellationToken))
            {
                return AccountResult<string>.Failed(texts.SetupAlreadyDone);
            }

            if (!SetupCode.Matches(setupCodes.Current, command.SetupCode))
            {
                logger.LogWarning("Setup was attempted with a wrong setup code.");
                return AccountResult<string>.Failed(texts.InvalidSetupCode);
            }

            if (command.Password != command.ConfirmPassword)
            {
                return AccountResult<string>.Failed(texts.PasswordsDoNotMatch);
            }

            var created = await accounts.CreateAsync(
                new NewAccount(
                    command.UserName.Trim(),
                    string.IsNullOrWhiteSpace(command.Email) ? null : command.Email.Trim(),
                    command.Password,
                    Roles.Administrator,
                    AccountTasks.OfferTwoFactor | AccountTasks.ShowRecoveryCodes),
                cancellationToken);
            if (!created.Succeeded)
            {
                return created;
            }

            var settings = await settingsStore.GetAsync(cancellationToken);
            settings.Rename(command.ServerName);
            await settingsStore.SaveAsync(settings, cancellationToken);
            await setupCodes.RemoveAsync(cancellationToken);

            logger.LogInformation("Setup finished: administrator {UserName} created.", command.UserName.Trim());
            return created;
        }
        finally
        {
            Gate.Release();
        }
    }
}
