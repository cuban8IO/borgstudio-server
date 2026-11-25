using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Storage;

/// <summary>Space and rights of every user, by user id (administrators).</summary>
public sealed record GetUserStorageQuery([property: Required] string ActingUserId)
    : IQuery<IReadOnlyDictionary<string, StorageAllowance>>;

internal sealed class GetUserStorageQueryHandler(StorageContext storage)
    : IQueryHandler<GetUserStorageQuery, IReadOnlyDictionary<string, StorageAllowance>>
{
    public async Task<IReadOnlyDictionary<string, StorageAllowance>> HandleAsync(GetUserStorageQuery query, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(query.ActingUserId, cancellationToken);
        var repositories = await storage.Store.GetRepositoriesAsync(cancellationToken);
        var policies = (await storage.Store.GetPoliciesAsync(cancellationToken)).ToDictionary(p => p.UserId);
        return (await storage.Accounts.ListAsync(cancellationToken)).ToDictionary(
            u => u.Id,
            u =>
            {
                var policy = policies.GetValueOrDefault(u.Id) ?? new UserStoragePolicy(u.Id);
                return new StorageAllowance(policy.MayCreateRepositories, StorageContext.UsedBy(repositories, u.Id), policy.QuotaBytes);
            });
    }
}

/// <summary>Whether a user may create repositories, and how much all of theirs may take (administrators).</summary>
public sealed record SetUserStorageCommand(
    [property: Required] string ActingUserId,
    [property: Required] string UserId,
    bool MayCreateRepositories,
    long? QuotaBytes) : ICommand;

internal sealed class SetUserStorageCommandHandler(StorageContext storage) : ICommandHandler<SetUserStorageCommand>
{
    public async Task HandleAsync(SetUserStorageCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (command.QuotaBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A quota must be positive.");
        }

        var policy = await storage.Store.GetPolicyAsync(command.UserId, cancellationToken);
        policy.MayCreateRepositories = command.MayCreateRepositories;
        policy.QuotaBytes = command.QuotaBytes;
        await storage.Store.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>What happens to repositories when their owner is renamed or deleted (used by the user commands).</summary>
internal sealed class RepositoryOwners(StorageContext storage, ILogger<RepositoryOwners> logger)
{
    /// <summary>The folders follow the new user name: &lt;datastore&gt;/&lt;new name&gt;/&lt;repository&gt;.</summary>
    public async Task FollowRenameAsync(string userId, string newUserName, CancellationToken cancellationToken)
    {
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        var owned = (await storage.Store.GetRepositoriesAsync(cancellationToken)).Where(r => r.OwnerId == userId && !r.IsDeleted);
        foreach (var repository in owned)
        {
            var datastore = datastores[repository.DatastoreId];
            var from = repository.RelativePath;
            var to = repository.FolderFor(newUserName, repository.Name);
            if (await storage.TryFolderAsync(datastore, from, () => storage.MoveAsync(datastore, from, to)) is { } problem)
            {
                // The database keeps the old folder, so the repository stays usable; it moves at the next rename.
                logger.LogWarning("Repository {Name} keeps its folder {Folder}: {Problem}", repository.Name, from, problem);
                continue;
            }

            repository.FollowOwnerName(newUserName);

            // Saved one by one, so the database always matches the folders that were moved.
            await storage.Store.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A deleted user's repositories go to the trash, where an administrator can give them to someone
    /// else. Returns a message if a folder could not be moved; the user must not be deleted then.
    /// </summary>
    public async Task<string?> OrphanAsync(string userId, string userName, CancellationToken cancellationToken)
    {
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        var owned = (await storage.Store.GetRepositoriesAsync(cancellationToken)).Where(r => r.OwnerId == userId).ToList();
        foreach (var repository in owned)
        {
            var datastore = datastores[repository.DatastoreId];
            var from = repository.RelativePath;
            repository.Orphan(userName, storage.Now);
            if (from != repository.RelativePath
                && await storage.TryFolderAsync(datastore, from, () => storage.MoveAsync(datastore, from, repository.RelativePath)) is { } problem)
            {
                return problem;
            }

            await storage.Store.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Repository {Owner}/{Name} went to the trash with its owner.", userName, repository.Name);
        }

        return null;
    }
}
