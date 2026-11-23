using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Domain.Users;

using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Persistence;

internal sealed class InvitationStore(AppDbContext db) : IInvitationStore
{
    public async Task<Invitation?> FindByTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = Invitation.HashToken(token.Trim());
        return await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
    }

    public async Task ReplaceAsync(Invitation invitation, CancellationToken cancellationToken)
    {
        await db.Invitations.Where(i => i.UserId == invitation.UserId).ExecuteDeleteAsync(cancellationToken);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task RemoveForUserAsync(string userId, CancellationToken cancellationToken) =>
        db.Invitations.Where(i => i.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetExpiriesAsync(CancellationToken cancellationToken)
    {
        // SQLite cannot compare DateTimeOffset values in queries; the table is small, so this runs in memory.
        var all = await db.Invitations.AsNoTracking().ToListAsync(cancellationToken);
        return all.ToDictionary(i => i.UserId, i => i.ExpiresAt);
    }
}
