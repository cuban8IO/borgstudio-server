using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Validation;
using BorgStudio.Server.Application.Accounts;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Users;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Users;

/// <summary>All users with their state, for administrators.</summary>
public sealed record GetUsersQuery([property: Required] string ActingUserId) : IQuery<IReadOnlyList<UserSummary>>;

/// <param name="InvitationExpiresAt">When the open invitation link expires, if there is one.</param>
public sealed record UserSummary(AccountInfo Account, DateTimeOffset? InvitationExpiresAt, bool InvitationExpired);

internal sealed class GetUsersQueryHandler(UserAdministration admin) : IQueryHandler<GetUsersQuery, IReadOnlyList<UserSummary>>
{
    public async Task<IReadOnlyList<UserSummary>> HandleAsync(GetUsersQuery query, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(query.ActingUserId, cancellationToken);
        var expiries = await admin.GetInvitationExpiriesAsync(cancellationToken);
        var users = await admin.Accounts.ListAsync(cancellationToken);
        return users
            .OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(u => expiries.TryGetValue(u.Id, out var expires)
                ? new UserSummary(u, expires, admin.Now >= expires)
                : new UserSummary(u, null, false))
            .ToList();
    }
}

/// <summary>How a new user gets in for the first time.</summary>
public enum NewUserAccess
{
    /// <summary>A generated password, shown once; it has to be changed at the first sign-in.</summary>
    TemporaryPassword,

    /// <summary>A one-time link with which the user sets their own password.</summary>
    Invitation,
}

/// <summary>What the administrator passes on to the user: a temporary password or an invitation link.</summary>
public sealed record UserAccessGrant(
    string UserId,
    string UserName,
    string? TemporaryPassword,
    string? InvitationLink,
    DateTimeOffset? InvitationExpiresAt);

public sealed record CreateUserCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserName,
    [property: OptionalEmail] string? Email,
    [property: Required] string Role,
    NewUserAccess Access,
    int InvitationDays,
    [property: Required] string CurrentBaseUrl) : ICommand<AccountResult<UserAccessGrant>>;

internal sealed class CreateUserCommandHandler(UserAdministration admin, ILogger<CreateUserCommandHandler> logger)
    : ICommandHandler<CreateUserCommand, AccountResult<UserAccessGrant>>
{
    public async Task<AccountResult<UserAccessGrant>> HandleAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (!Roles.All.Contains(command.Role))
        {
            throw new ArgumentException($"Unknown role {command.Role}.", nameof(command));
        }

        var userName = command.UserName.Trim();
        var email = string.IsNullOrWhiteSpace(command.Email) ? null : command.Email.Trim();
        var byPassword = command.Access == NewUserAccess.TemporaryPassword;
        var password = byPassword ? TemporaryPassword.Generate() : null;
        var tasks = AccountTasks.OfferTwoFactor | AccountTasks.ShowRecoveryCodes;
        if (byPassword)
        {
            tasks |= AccountTasks.ChangePassword;
        }

        var created = await admin.Accounts.CreateAsync(
            new NewAccount(userName, email, password, command.Role, tasks), cancellationToken);
        if (!created.Succeeded)
        {
            return AccountResult<UserAccessGrant>.Failed([.. created.Errors]);
        }

        var userId = created.Value!;
        logger.LogInformation("User {UserName} ({Role}) was created.", userName, command.Role);
        if (byPassword)
        {
            return AccountResult<UserAccessGrant>.Ok(new UserAccessGrant(userId, userName, password, null, null));
        }

        var (link, expiresAt) = await admin.InviteAsync(userId, command.InvitationDays, command.CurrentBaseUrl, cancellationToken);
        return AccountResult<UserAccessGrant>.Ok(new UserAccessGrant(userId, userName, null, link, expiresAt));
    }
}

public sealed record UpdateUserCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId,
    [property: Required] string UserName,
    [property: OptionalEmail] string? Email,
    [property: Required] string Role) : ICommand<AccountResult>;

internal sealed class UpdateUserCommandHandler(UserAdministration admin, RepositoryOwners repositories)
    : ICommandHandler<UpdateUserCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(UpdateUserCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (!Roles.All.Contains(command.Role))
        {
            throw new ArgumentException($"Unknown role {command.Role}.", nameof(command));
        }

        var target = await admin.RequireTargetAsync(command.UserId, cancellationToken);
        var demotes = target.IsAdministrator && command.Role != Roles.Administrator;
        if (demotes && command.UserId == command.ActingUserId)
        {
            return AccountResult.Failed(admin.Texts.NotOnYourOwnAccount);
        }

        if (demotes && !await admin.OtherAdministratorRemainsAsync(target, cancellationToken))
        {
            return AccountResult.Failed(admin.Texts.LastAdministrator);
        }

        var userName = command.UserName.Trim();
        if (!string.Equals(userName, target.UserName, StringComparison.Ordinal))
        {
            var renamed = await admin.Accounts.SetUserNameAsync(command.UserId, userName, cancellationToken);
            if (!renamed.Succeeded)
            {
                return renamed;
            }

            await repositories.FollowRenameAsync(command.UserId, userName, cancellationToken);
        }

        var email = string.IsNullOrWhiteSpace(command.Email) ? null : command.Email.Trim();
        if (email != target.Email)
        {
            var changed = await admin.Accounts.SetEmailAsync(command.UserId, email, cancellationToken);
            if (!changed.Succeeded)
            {
                return changed;
            }
        }

        if (!target.Roles.SequenceEqual([command.Role]))
        {
            await admin.Accounts.SetRoleAsync(command.UserId, command.Role, cancellationToken);
            // A changed role must reach the user's cookie: their sessions renew at the next check.
            await admin.Accounts.InvalidateSessionsAsync(command.UserId, cancellationToken);
        }

        return AccountResult.Success;
    }
}

/// <summary>Gives the user a new temporary password; they set their own at the next sign-in.</summary>
public sealed record ResetUserPasswordCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId) : ICommand<AccountResult<UserAccessGrant>>;

internal sealed class ResetUserPasswordCommandHandler(UserAdministration admin, ILogger<ResetUserPasswordCommandHandler> logger)
    : ICommandHandler<ResetUserPasswordCommand, AccountResult<UserAccessGrant>>
{
    public async Task<AccountResult<UserAccessGrant>> HandleAsync(ResetUserPasswordCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (command.UserId == command.ActingUserId)
        {
            return AccountResult<UserAccessGrant>.Failed(admin.Texts.NotOnYourOwnAccount);
        }

        var target = await admin.RequireTargetAsync(command.UserId, cancellationToken);
        var password = TemporaryPassword.Generate();
        var result = await admin.Accounts.SetPasswordAsync(command.UserId, password, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The temporary password was rejected: {string.Join(" ", result.Errors)}");
        }

        var tasks = target.Tasks | AccountTasks.ChangePassword | AccountTasks.ShowRecoveryCodes;
        if (!target.TwoFactorEnabled)
        {
            tasks |= AccountTasks.OfferTwoFactor;
        }

        await admin.Accounts.SetTasksAsync(command.UserId, tasks, cancellationToken);
        await admin.Accounts.UnlockAsync(command.UserId, cancellationToken);
        await admin.RemoveInvitationAsync(command.UserId, cancellationToken);
        await admin.Accounts.InvalidateSessionsAsync(command.UserId, cancellationToken);

        logger.LogInformation("The password of {UserName} was reset by an administrator.", target.UserName);
        return AccountResult<UserAccessGrant>.Ok(new UserAccessGrant(command.UserId, target.UserName, password, null, null));
    }
}

/// <summary>A new invitation link, e.g. when the old one expired; only for users without a password yet.</summary>
public sealed record CreateInvitationCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId,
    int InvitationDays,
    [property: Required] string CurrentBaseUrl) : ICommand<AccountResult<UserAccessGrant>>;

internal sealed class CreateInvitationCommandHandler(UserAdministration admin)
    : ICommandHandler<CreateInvitationCommand, AccountResult<UserAccessGrant>>
{
    public async Task<AccountResult<UserAccessGrant>> HandleAsync(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var target = await admin.RequireTargetAsync(command.UserId, cancellationToken);
        if (target.HasPassword)
        {
            throw new InvalidOperationException("Invitations are only for users who have not set a password yet.");
        }

        var (link, expiresAt) = await admin.InviteAsync(command.UserId, command.InvitationDays, command.CurrentBaseUrl, cancellationToken);
        return AccountResult<UserAccessGrant>.Ok(new UserAccessGrant(command.UserId, target.UserName, null, link, expiresAt));
    }
}

public sealed record RevokeInvitationCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId) : ICommand;

internal sealed class RevokeInvitationCommandHandler(UserAdministration admin) : ICommandHandler<RevokeInvitationCommand>
{
    public async Task HandleAsync(RevokeInvitationCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        await admin.RemoveInvitationAsync(command.UserId, cancellationToken);
    }
}

/// <summary>For a lost authenticator: two-factor authentication off, offered again at the next sign-in.</summary>
public sealed record DisableUserTwoFactorCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId) : ICommand<AccountResult>;

internal sealed class DisableUserTwoFactorCommandHandler(UserAdministration admin)
    : ICommandHandler<DisableUserTwoFactorCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(DisableUserTwoFactorCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (command.UserId == command.ActingUserId)
        {
            return AccountResult.Failed(admin.Texts.NotOnYourOwnAccount);
        }

        var target = await admin.RequireTargetAsync(command.UserId, cancellationToken);
        await admin.Accounts.SetTwoFactorEnabledAsync(command.UserId, false, cancellationToken);
        await admin.Accounts.ResetAuthenticatorKeyAsync(command.UserId, cancellationToken);
        await admin.Accounts.SetTasksAsync(
            command.UserId, (target.Tasks | AccountTasks.OfferTwoFactor) & ~AccountTasks.VerifyAuthenticator, cancellationToken);
        return AccountResult.Success;
    }
}

public sealed record UnlockUserCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId) : ICommand;

internal sealed class UnlockUserCommandHandler(UserAdministration admin) : ICommandHandler<UnlockUserCommand>
{
    public async Task HandleAsync(UnlockUserCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        await admin.Accounts.UnlockAsync(command.UserId, cancellationToken);
    }
}

/// <summary>Ends all sessions of the user (within a minute).</summary>
public sealed record SignOutUserCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId) : ICommand;

internal sealed class SignOutUserCommandHandler(UserAdministration admin) : ICommandHandler<SignOutUserCommand>
{
    public async Task HandleAsync(SignOutUserCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        await admin.Accounts.InvalidateSessionsAsync(command.UserId, cancellationToken);
    }
}

public sealed record SetUserDisabledCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId,
    bool Disabled) : ICommand<AccountResult>;

internal sealed class SetUserDisabledCommandHandler(UserAdministration admin, ILogger<SetUserDisabledCommandHandler> logger)
    : ICommandHandler<SetUserDisabledCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(SetUserDisabledCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var target = await admin.RequireTargetAsync(command.UserId, cancellationToken);
        if (command.Disabled)
        {
            if (command.UserId == command.ActingUserId)
            {
                return AccountResult.Failed(admin.Texts.NotOnYourOwnAccount);
            }

            if (!await admin.OtherAdministratorRemainsAsync(target, cancellationToken))
            {
                return AccountResult.Failed(admin.Texts.LastAdministrator);
            }
        }

        await admin.Accounts.SetDisabledAsync(command.UserId, command.Disabled, cancellationToken);
        logger.LogInformation("User {UserName} was {State}.", target.UserName, command.Disabled ? "disabled" : "enabled");
        return AccountResult.Success;
    }
}

public sealed record DeleteUserCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId) : ICommand<AccountResult>;

internal sealed class DeleteUserCommandHandler(
    UserAdministration admin, RepositoryOwners repositories, ILogger<DeleteUserCommandHandler> logger)
    : ICommandHandler<DeleteUserCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(DeleteUserCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (command.UserId == command.ActingUserId)
        {
            return AccountResult.Failed(admin.Texts.NotOnYourOwnAccount);
        }

        var target = await admin.RequireTargetAsync(command.UserId, cancellationToken);
        if (!await admin.OtherAdministratorRemainsAsync(target, cancellationToken))
        {
            return AccountResult.Failed(admin.Texts.LastAdministrator);
        }

        if (await repositories.OrphanAsync(command.UserId, target.UserName, cancellationToken) is { } problem)
        {
            return AccountResult.Failed(problem);
        }

        await admin.RemoveInvitationAsync(command.UserId, cancellationToken);
        var result = await admin.Accounts.DeleteAsync(command.UserId, cancellationToken);
        if (result.Succeeded)
        {
            logger.LogInformation("User {UserName} was deleted.", target.UserName);
        }

        return result;
    }
}
