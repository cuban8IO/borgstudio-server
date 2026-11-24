using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Application.Users;

/// <summary>The invitation behind a link, if it is still valid.</summary>
public sealed record GetInvitationQuery([property: Required] string Token) : IQuery<InvitationInfo?>;

public sealed record InvitationInfo(string UserName, DateTimeOffset ExpiresAt);

internal sealed class GetInvitationQueryHandler(IInvitationStore invitations, IAccountService accounts, TimeProvider timeProvider)
    : IQueryHandler<GetInvitationQuery, InvitationInfo?>
{
    public async Task<InvitationInfo?> HandleAsync(GetInvitationQuery query, CancellationToken cancellationToken)
    {
        var (invitation, account) = await InvitationLookup.FindValidAsync(
            invitations, accounts, timeProvider, query.Token, cancellationToken);
        return invitation is null ? null : new InvitationInfo(account!.UserName, invitation.ExpiresAt);
    }
}

/// <summary>
/// The invited user sets their password. The link is used up; next come two-factor authentication
/// (offered) and the recovery codes.
/// </summary>
public sealed record AcceptInvitationCommand(
    [property: Required] string Token,
    [property: Required] string Password,
    [property: Required] string ConfirmPassword) : ICommand<AccountResult<string>>;

internal sealed class AcceptInvitationCommandHandler(
    IInvitationStore invitations,
    IAccountService accounts,
    IAccountTexts texts,
    TimeProvider timeProvider) : ICommandHandler<AcceptInvitationCommand, AccountResult<string>>
{
    public async Task<AccountResult<string>> HandleAsync(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        var (invitation, account) = await InvitationLookup.FindValidAsync(
            invitations, accounts, timeProvider, command.Token, cancellationToken);
        if (invitation is null)
        {
            return AccountResult<string>.Failed(texts.InvitationInvalid);
        }

        if (command.Password != command.ConfirmPassword)
        {
            return AccountResult<string>.Failed(texts.PasswordsDoNotMatch);
        }

        var result = await accounts.SetPasswordAsync(account!.Id, command.Password, cancellationToken);
        if (!result.Succeeded)
        {
            return AccountResult<string>.Failed([.. result.Errors]);
        }

        var tasks = (account.Tasks & ~AccountTasks.ChangePassword) | AccountTasks.ShowRecoveryCodes;
        if (!account.TwoFactorEnabled)
        {
            tasks |= AccountTasks.OfferTwoFactor;
        }

        await accounts.SetTasksAsync(account.Id, tasks, cancellationToken);
        await invitations.RemoveForUserAsync(account.Id, cancellationToken);
        return AccountResult<string>.Ok(account.Id);
    }
}

internal static class InvitationLookup
{
    public static async Task<(Invitation? Invitation, AccountInfo? Account)> FindValidAsync(
        IInvitationStore invitations,
        IAccountService accounts,
        TimeProvider timeProvider,
        string token,
        CancellationToken cancellationToken)
    {
        var invitation = await invitations.FindByTokenAsync(token, cancellationToken);
        if (invitation is null || invitation.IsExpired(timeProvider.GetUtcNow()))
        {
            return (null, null);
        }

        var account = await accounts.GetAsync(invitation.UserId, cancellationToken);
        return account is null || account.IsDisabled || account.HasPassword ? (null, null) : (invitation, account);
    }
}
