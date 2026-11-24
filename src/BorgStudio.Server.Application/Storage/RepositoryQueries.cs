using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Storage;

/// <param name="Access">What the acting user may do with the data; <c>null</c> for administrators who only manage it.</param>
public sealed record RepositorySummary(
    Guid Id,
    string? OwnerId,
    string OwnerName,
    string Name,
    Guid DatastoreId,
    string DatastoreName,
    long UsedBytes,
    long? QuotaBytes,
    DateTimeOffset? UsageMeasuredAt,
    RepositoryAccess? Access,
    bool IsOwner,
    bool CanManage,
    int ShareCount,
    bool IsMoving,
    DateTimeOffset CreatedAt)
{
    public string FullName => $"{OwnerName}/{Name}";
}

/// <summary>How much a user may store, and whether they may create repositories.</summary>
public sealed record StorageAllowance(bool MayCreate, long UsedBytes, long? QuotaBytes)
{
    public bool Exceeded => QuotaBytes is { } quota && UsedBytes >= quota;
}

/// <summary>A datastore new repositories can go to.</summary>
public sealed record DatastoreChoice(Guid Id, string Name, long? FreeBytes);

public sealed record RepositoryListView(
    IReadOnlyList<RepositorySummary> Repositories,
    StorageAllowance Allowance,
    IReadOnlyList<DatastoreChoice> Datastores,
    int TrashCount,
    bool HostingSupported);

/// <summary>The repositories the acting user owns or was given; administrators see all of them.</summary>
public sealed record GetRepositoriesQuery([property: Required] string ActingUserId) : IQuery<RepositoryListView>;

internal sealed class GetRepositoriesQueryHandler(StorageContext storage) : IQueryHandler<GetRepositoriesQuery, RepositoryListView>
{
    public async Task<RepositoryListView> HandleAsync(GetRepositoriesQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var all = await storage.Store.GetRepositoriesAsync(cancellationToken);
        var users = await storage.UsersByIdAsync(cancellationToken);
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);

        var visible = all
            .Where(r => !r.IsDeleted && StorageContext.CanSee(acting, r))
            .Select(r => RepositoryViews.Summary(r, acting, users, datastores))
            .OrderBy(r => r.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var policy = await storage.Store.GetPolicyAsync(acting.Id, cancellationToken);
        var allowance = new StorageAllowance(policy.MayCreateRepositories, StorageContext.UsedBy(all, acting.Id), policy.QuotaBytes);
        var choices = datastores.Values
            .Where(d => !d.IsReadOnly)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DatastoreChoice(d.Id, d.Name, storage.Folders.GetSpace(d.Path)?.FreeBytes))
            .ToList();
        var trash = all.Count(r => r.IsDeleted && (acting.IsAdministrator || r.OwnerId == acting.Id));
        return new RepositoryListView(visible, allowance, choices, trash, storage.Folders.HostingSupported);
    }
}

public sealed record ShareInfo(string UserId, string UserName, RepositoryAccess Access);

/// <param name="Location">The folder on the server; only administrators see it.</param>
/// <param name="MoveTarget">The datastore it is being moved to, if it is.</param>
public sealed record RepositoryDetails(
    RepositorySummary Summary,
    string? Location,
    IReadOnlyList<ShareInfo> Shares,
    StorageAllowance? OwnerAllowance,
    string? MoveTarget,
    RepositoryMoveProgress? MoveProgress,
    string? MoveError,
    bool HostingSupported);

/// <summary>One repository; <c>null</c> if it does not exist, is in the trash or the user may not see it.</summary>
public sealed record GetRepositoryQuery([property: Required] string ActingUserId, Guid RepositoryId) : IQuery<RepositoryDetails?>;

internal sealed class GetRepositoryQueryHandler(StorageContext storage, RepositoryMoves moves)
    : IQueryHandler<GetRepositoryQuery, RepositoryDetails?>
{
    public async Task<RepositoryDetails?> HandleAsync(GetRepositoryQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(query.RepositoryId, cancellationToken);
        if (repository is null || repository.IsDeleted || !StorageContext.CanSee(acting, repository))
        {
            return null;
        }

        var users = await storage.UsersByIdAsync(cancellationToken);
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        var summary = RepositoryViews.Summary(repository, acting, users, datastores);
        var manage = summary.CanManage;

        // Users with a share see who else has access too, but only managers can change it.
        var shares = repository.Shares
            .Select(s => new ShareInfo(s.UserId, users.TryGetValue(s.UserId, out var u) ? u.UserName : s.UserId, s.Access))
            .OrderBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        StorageAllowance? ownerAllowance = null;
        if (manage && repository.OwnerId is { } ownerId)
        {
            var policy = await storage.Store.GetPolicyAsync(ownerId, cancellationToken);
            var all = await storage.Store.GetRepositoriesAsync(cancellationToken);
            ownerAllowance = new StorageAllowance(policy.MayCreateRepositories, StorageContext.UsedBy(all, ownerId), policy.QuotaBytes);
        }

        var datastore = datastores[repository.DatastoreId];
        var target = repository.MoveTargetDatastoreId is { } targetId && datastores.TryGetValue(targetId, out var t) ? t.Name : null;
        return new RepositoryDetails(
            summary,
            acting.IsAdministrator ? Path.Combine(datastore.Path, repository.RelativePath.Replace('/', Path.DirectorySeparatorChar)) : null,
            shares,
            ownerAllowance,
            target,
            moves.Progress(repository.Id),
            manage ? repository.MoveError : null,
            storage.Folders.HostingSupported);
    }
}

/// <summary>Users a repository can be shared with: enabled, not the owner and not yet sharing it.</summary>
public sealed record GetShareCandidatesQuery([property: Required] string ActingUserId, Guid RepositoryId) : IQuery<IReadOnlyList<string>>;

internal sealed class GetShareCandidatesQueryHandler(StorageContext storage) : IQueryHandler<GetShareCandidatesQuery, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> HandleAsync(GetShareCandidatesQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var repository = await storage.RequireManagedAsync(acting, query.RepositoryId, cancellationToken);
        var users = await storage.Accounts.ListAsync(cancellationToken);
        return users
            .Where(u => !u.IsDisabled && u.Id != repository.OwnerId && repository.AccessFor(u.Id) is null)
            .Select(u => u.UserName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal static class RepositoryViews
{
    public static RepositorySummary Summary(
        BackupRepository repository,
        AccountInfo acting,
        IReadOnlyDictionary<string, AccountInfo> users,
        IReadOnlyDictionary<Guid, Datastore> datastores) => new(
            repository.Id,
            repository.OwnerId,
            OwnerName(repository, users),
            repository.Name,
            repository.DatastoreId,
            datastores.TryGetValue(repository.DatastoreId, out var d) ? d.Name : "?",
            repository.UsedBytes,
            repository.QuotaBytes,
            repository.UsageMeasuredAt,
            repository.AccessFor(acting.Id),
            repository.OwnerId == acting.Id,
            StorageContext.CanManage(acting, repository),
            repository.Shares.Count,
            repository.IsMoving,
            repository.CreatedAt);

    public static string OwnerName(BackupRepository repository, IReadOnlyDictionary<string, AccountInfo> users) =>
        repository.OwnerId is { } id && users.TryGetValue(id, out var owner)
            ? owner.UserName
            : repository.FormerOwnerName ?? "?";
}
