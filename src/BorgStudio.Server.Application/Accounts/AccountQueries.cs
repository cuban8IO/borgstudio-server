using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Application.Accounts;

public sealed record GetAccountQuery([property: Required] string UserId) : IQuery<AccountInfo?>;

internal sealed class GetAccountQueryHandler(IAccountService accounts) : IQueryHandler<GetAccountQuery, AccountInfo?>
{
    public Task<AccountInfo?> HandleAsync(GetAccountQuery query, CancellationToken cancellationToken) =>
        accounts.GetAsync(query.UserId, cancellationToken);
}

public sealed record SetThemeModeCommand([property: Required] string UserId, ThemeMode Mode) : ICommand;

internal sealed class SetThemeModeCommandHandler(IAccountService accounts) : ICommandHandler<SetThemeModeCommand>
{
    public Task HandleAsync(SetThemeModeCommand command, CancellationToken cancellationToken) =>
        accounts.SetThemeModeAsync(command.UserId, command.Mode, cancellationToken);
}

public sealed record ChangePasswordCommand(
    [property: Required] string UserId,
    [property: Required] string CurrentPassword,
    [property: Required] string NewPassword,
    [property: Required] string ConfirmPassword) : ICommand<AccountResult>;

internal sealed class ChangePasswordCommandHandler(IAccountService accounts, IAccountTexts texts)
    : ICommandHandler<ChangePasswordCommand, AccountResult>
{
    public Task<AccountResult> HandleAsync(ChangePasswordCommand command, CancellationToken cancellationToken) =>
        command.NewPassword != command.ConfirmPassword
            ? Task.FromResult(AccountResult.Failed(texts.PasswordsDoNotMatch))
            : accounts.ChangePasswordAsync(command.UserId, command.CurrentPassword, command.NewPassword, cancellationToken);
}
