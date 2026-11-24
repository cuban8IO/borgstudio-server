using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Access;

/// <summary>What the app shows about a repository it can use.</summary>
/// <param name="DatastoreFreeBytes">Free space on the disk of its datastore.</param>
/// <param name="OwnerQuota">The owner's storage, if the owner has a quota.</param>
public sealed record RepositoryStats(
    Guid Id,
    string OwnerName,
    string Name,
    RepositoryAccess Access,
    bool IsOwner,
    string Url,
    long UsedBytes,
    long? QuotaBytes,
    DateTimeOffset? UsageMeasuredAt,
    string DatastoreName,
    long? DatastoreFreeBytes,
    StorageAllowance? OwnerQuota,
    RepositoryLastAccess? LastAccess,
    IReadOnlyList<RepositoryParticipant> Participants,
    bool IsMoving)
{
    public string FullName => $"{OwnerName}/{Name}";
}

/// <param name="By">The paired app or key name that connected.</param>
public sealed record RepositoryLastAccess(DateTimeOffset At, string UserName, string By);

public sealed record RepositoryParticipant(string UserName, RepositoryAccess Access, bool IsOwner);

/// <summary>
/// The repositories a user can back up to (own and shared; administrators only see theirs too),
/// for the interface. <paramref name="RepositoryId"/> narrows it to one.
/// </summary>
public sealed record GetRepositoryStatsQuery(
    [property: Required] string UserId,
    string? RequestHost,
    Guid? RepositoryId = null) : IQuery<IReadOnlyList<RepositoryStats>>;

internal sealed class GetRepositoryStatsQueryHandler(StorageContext storage, SshEndpoints endpoints)
    : IQueryHandler<GetRepositoryStatsQuery, IReadOnlyList<RepositoryStats>>
{
    public async Task<IReadOnlyList<RepositoryStats>> HandleAsync(GetRepositoryStatsQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.UserId, cancellationToken);
        var all = await storage.Store.GetRepositoriesAsync(cancellationToken);
        var users = await storage.UsersByIdAsync(cancellationToken);
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        var policies = (await storage.Store.GetPoliciesAsync(cancellationToken)).ToDictionary(p => p.UserId);
        var endpoint = await endpoints.GetAsync(query.RequestHost, cancellationToken);
        var free = new Dictionary<Guid, long?>();

        string NameOf(string id) => users.TryGetValue(id, out var user) ? user.UserName : "?";

        return all
            .Where(r => !r.IsDeleted && r.AccessFor(acting.Id) is not null && (query.RepositoryId is null || r.Id == query.RepositoryId))
            .OrderBy(r => RepositoryViews.OwnerName(r, users), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r =>
            {
                var datastore = datastores[r.DatastoreId];
                if (!free.TryGetValue(datastore.Id, out var freeBytes))
                {
                    free[datastore.Id] = freeBytes = storage.Folders.GetSpace(datastore.Path)?.FreeBytes;
                }

                StorageAllowance? ownerQuota = null;
                if (r.OwnerId is { } ownerId && policies.TryGetValue(ownerId, out var policy) && policy.QuotaBytes is not null)
                {
                    ownerQuota = new StorageAllowance(policy.MayCreateRepositories, StorageContext.UsedBy(all, ownerId), policy.QuotaBytes);
                }

                var participants = new List<RepositoryParticipant> { new(RepositoryViews.OwnerName(r, users), RepositoryAccess.Full, true) };
                participants.AddRange(r.Shares
                    .Select(s => new RepositoryParticipant(NameOf(s.UserId), s.Access, false))
                    .OrderBy(p => p.UserName, StringComparer.OrdinalIgnoreCase));

                return new RepositoryStats(
                    r.Id,
                    RepositoryViews.OwnerName(r, users),
                    r.Name,
                    r.AccessFor(acting.Id)!.Value,
                    r.OwnerId == acting.Id,
                    endpoint.UrlFor(r.Id),
                    r.UsedBytes,
                    r.QuotaBytes,
                    r.UsageMeasuredAt,
                    datastore.Name,
                    freeBytes,
                    ownerQuota,
                    r.LastAccessAt is { } at ? new RepositoryLastAccess(at, NameOf(r.LastAccessUserId ?? string.Empty), r.LastAccessBy ?? string.Empty) : null,
                    participants,
                    r.IsMoving);
            })
            .ToList();
    }
}
