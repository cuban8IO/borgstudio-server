using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Application.Abstractions.Accounts;

public interface IInvitationStore
{
    /// <summary>The invitation the token belongs to, or <c>null</c>.</summary>
    Task<Invitation?> FindByTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>Stores the invitation; an earlier invitation of the same user stops working.</summary>
    Task ReplaceAsync(Invitation invitation, CancellationToken cancellationToken);

    Task RemoveForUserAsync(string userId, CancellationToken cancellationToken);

    /// <summary>When the open invitations expire, by user id.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetExpiriesAsync(CancellationToken cancellationToken);
}
