using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Storage;

/// <param name="OwnerName">The owner, or the former owner's name if that user was deleted (<see cref="OwnerExists"/> false).</param>
public sealed record TrashItem(
    Guid Id,
    string OwnerName,
    bool OwnerExists,
    string Name,
    string DatastoreName,
    long UsedBytes,
    DateTimeOffset DeletedAt,
    DateTimeOffset PurgeAt);

/// <summary>Deleted repositories: owners see theirs, administrators all of them.</summary>
public sealed record GetTrashQuery([property: Required] string ActingUserId) : IQuery<IReadOnlyList<TrashItem>>;

internal sealed class GetTrashQueryHandler(StorageContext storage) : IQueryHandler<GetTrashQuery, IReadOnlyList<TrashItem>>
{
    public async Task<IReadOnlyList<TrashItem>> HandleAsync(GetTrashQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var users = await storage.UsersByIdAsync(cancellationToken);
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        return (await storage.Store.GetRepositoriesAsync(cancellationToken))
            .Where(r => r.IsDeleted && (acting.IsAdministrator || r.OwnerId == acting.Id))
            .OrderByDescending(r => r.DeletedAt!.Value.UtcTicks)
            .Select(r => new TrashItem(
                r.Id,
                RepositoryViews.OwnerName(r, users),
                r.OwnerId is not null && users.ContainsKey(r.OwnerId),
                r.Name,
                datastores.TryGetValue(r.DatastoreId, out var d) ? d.Name : "?",
                r.UsedBytes,
                r.DeletedAt!.Value,
                r.PurgeAt!.Value))
            .ToList();
    }
}

/// <summary>
/// Brings a repository back from the trash. A repository whose owner was deleted needs a new owner
/// (administrators only); <paramref name="Name"/> renames it, e.g. when the old name is taken by now.
/// </summary>
public sealed record RestoreRepositoryCommand(
    [property: Required] string ActingUserId,
    Guid RepositoryId,
    string? OwnerUserName = null,
    string? Name = null) : ICommand<AccountResult>;

internal sealed class RestoreRepositoryCommandHandler(StorageContext storage, ILogger<RestoreRepositoryCommandHandler> logger)
    : ICommandHandler<RestoreRepositoryCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(RestoreRepositoryCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(command.RepositoryId, cancellationToken);
        if (repository is not { IsDeleted: true } || !(acting.IsAdministrator || repository.OwnerId == acting.Id))
        {
            throw new UnauthorizedAccessException("The repository is not in the trash or may not be restored by this user.");
        }

        var texts = storage.Texts;
        AccountInfo? owner;
        if (acting.IsAdministrator && !string.IsNullOrWhiteSpace(command.OwnerUserName))
        {
            owner = await storage.Accounts.FindByNameAsync(command.OwnerUserName.Trim(), cancellationToken);
            if (owner is null)
            {
                return AccountResult.Failed(string.Format(texts.UserUnknown, command.OwnerUserName.Trim()));
            }
        }
        else
        {
            owner = repository.OwnerId is { } ownerId ? await storage.Accounts.GetAsync(ownerId, cancellationToken) : null;
            if (owner is null)
            {
                return AccountResult.Failed(texts.RestoreNeedsOwner);
            }
        }

        var name = string.IsNullOrWhiteSpace(command.Name) ? repository.Name : command.Name.Trim();
        if (!RepositoryNames.IsValid(name))
        {
            return AccountResult.Failed(texts.RepositoryNameInvalid);
        }

        if (await storage.NameTakenAsync(owner.Id, name, repository.Id, cancellationToken))
        {
            return AccountResult.Failed(string.Format(texts.RepositoryNameTaken, $"{owner.UserName}/{name}"));
        }

        var datastore = (await storage.Store.FindDatastoreAsync(repository.DatastoreId, cancellationToken))!;
        var from = repository.RelativePath;
        repository.Restore(owner.Id, owner.UserName, name);
        if (storage.Folders.Exists(datastore.Path, repository.RelativePath))
        {
            return AccountResult.Failed(string.Format(texts.RepositoryNameTaken, $"{owner.UserName}/{name}"));
        }

        if (storage.TryFolder(datastore, from, () => storage.Folders.Move(datastore.Path, from, repository.RelativePath)) is { } problem)
        {
            return AccountResult.Failed(problem);
        }

        await storage.Store.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Repository {Owner}/{Name} was restored from the trash.", owner.UserName, name);
        return AccountResult.Success;
    }
}

/// <summary>Deletes repositories in the trash for good (administrators); without an id, the whole trash.</summary>
public sealed record PurgeRepositoriesCommand([property: Required] string ActingUserId, Guid? RepositoryId = null) : ICommand<AccountResult>;

internal sealed class PurgeRepositoriesCommandHandler(StorageContext storage, ILogger<PurgeRepositoriesCommandHandler> logger)
    : ICommandHandler<PurgeRepositoriesCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(PurgeRepositoriesCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var doomed = (await storage.Store.GetRepositoriesAsync(cancellationToken))
            .Where(r => r.IsDeleted && (command.RepositoryId is null || r.Id == command.RepositoryId))
            .ToList();
        var errors = await StorageMaintenance.PurgeAsync(storage, doomed, logger, cancellationToken);
        return errors.Count == 0 ? AccountResult.Success : AccountResult.Failed([.. errors]);
    }
}
