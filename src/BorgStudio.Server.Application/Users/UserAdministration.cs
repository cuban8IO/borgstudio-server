using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Application.Users;

/// <summary>Rules every user administration command checks before it changes anything.</summary>
internal sealed class UserAdministration(
    IAccountService accounts,
    IInvitationStore invitations,
    IServerSettingsStore settings,
    IAccountTexts texts,
    TimeProvider timeProvider)
{
    public IAccountService Accounts => accounts;

    public IAccountTexts Texts => texts;

    /// <summary>The acting user must be an enabled administrator; anything else is a programming or forging error.</summary>
    public async Task RequireAdministratorAsync(string actingUserId, CancellationToken cancellationToken)
    {
        var acting = await accounts.GetAsync(actingUserId, cancellationToken);
        if (acting is null || acting.IsDisabled || !acting.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Only administrators may manage users.");
        }
    }

    public async Task<AccountInfo> RequireTargetAsync(string userId, CancellationToken cancellationToken) =>
        await accounts.GetAsync(userId, cancellationToken) ?? throw new InvalidOperationException("The user does not exist.");

    /// <summary>
    /// Changes that could lock everyone out: the server must keep at least one enabled administrator
    /// besides <paramref name="target"/>, if the change takes that role away from it.
    /// </summary>
    public async Task<bool> OtherAdministratorRemainsAsync(AccountInfo target, CancellationToken cancellationToken)
    {
        if (!target.IsAdministrator || target.IsDisabled)
        {
            return true;
        }

        var administrators = await accounts.GetInRoleAsync(Roles.Administrator, cancellationToken);
        return administrators.Any(a => a.Id != target.Id && !a.IsDisabled);
    }

    /// <summary>Creates a one-time invitation link for <paramref name="userId"/>; an older link stops working.</summary>
    public async Task<(string Link, DateTimeOffset ExpiresAt)> InviteAsync(
        string userId, int validityDays, string currentBaseUrl, CancellationToken cancellationToken)
    {
        var (invitation, token) = Invitation.Create(userId, validityDays, timeProvider.GetUtcNow());
        await invitations.ReplaceAsync(invitation, cancellationToken);

        var baseUrl = (await settings.GetAsync(cancellationToken)).PublicUrl ?? currentBaseUrl;
        var link = $"{baseUrl.TrimEnd('/')}/account/invitation?token={token}";
        return (link, invitation.ExpiresAt);
    }

    public Task RemoveInvitationAsync(string userId, CancellationToken cancellationToken) =>
        invitations.RemoveForUserAsync(userId, cancellationToken);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetInvitationExpiriesAsync(CancellationToken cancellationToken) =>
        invitations.GetExpiriesAsync(cancellationToken);

    public DateTimeOffset Now => timeProvider.GetUtcNow();
}
