using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Storage;

/// <summary>Creates an empty repository of the acting user; the client initializes it with borg.</summary>
/// <param name="DatastoreId">Where it goes; <c>null</c> picks the writable datastore with the most free space.</param>
public sealed record CreateRepositoryCommand(
    [property: Required] string ActingUserId,
    [property: Required] string Name,
    Guid? DatastoreId,
    long? QuotaBytes) : ICommand<AccountResult<Guid>>;

internal sealed class CreateRepositoryCommandHandler(StorageContext storage, ILogger<CreateRepositoryCommandHandler> logger)
    : ICommandHandler<CreateRepositoryCommand, AccountResult<Guid>>
{
    public async Task<AccountResult<Guid>> HandleAsync(CreateRepositoryCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var texts = storage.Texts;
        var policy = await storage.Store.GetPolicyAsync(acting.Id, cancellationToken);
        if (!policy.MayCreateRepositories)
        {
            return AccountResult<Guid>.Failed(texts.MayNotCreateRepositories);
        }

        var all = await storage.Store.GetRepositoriesAsync(cancellationToken);
        if (policy.QuotaBytes is { } userQuota && StorageContext.UsedBy(all, acting.Id) >= userQuota)
        {
            return AccountResult<Guid>.Failed(texts.UserQuotaExceeded);
        }

        var name = command.Name.Trim();
        if (!RepositoryNames.IsValid(name))
        {
            return AccountResult<Guid>.Failed(texts.RepositoryNameInvalid);
        }

        if (command.QuotaBytes is <= 0)
        {
            return AccountResult<Guid>.Failed(texts.QuotaInvalid);
        }

        if (await storage.NameTakenAsync(acting.Id, name, null, cancellationToken))
        {
            return AccountResult<Guid>.Failed(string.Format(texts.RepositoryNameTaken, name));
        }

        var datastore = await PickDatastoreAsync(command.DatastoreId, cancellationToken);
        if (datastore is null)
        {
            return AccountResult<Guid>.Failed(texts.DatastoreUnknown);
        }

        if (datastore.IsReadOnly)
        {
            return AccountResult<Guid>.Failed(texts.DatastoreReadOnly);
        }

        var repository = BackupRepository.Create(acting.Id, acting.UserName, name, datastore.Id, command.QuotaBytes, storage.Now);
        if (storage.Folders.Exists(datastore.Path, repository.RelativePath))
        {
            // A leftover folder (e.g. restored by hand) is never taken over silently.
            return AccountResult<Guid>.Failed(string.Format(texts.RepositoryNameTaken, name));
        }

        if (storage.TryFolder(datastore, repository.RelativePath, () => storage.Folders.Create(datastore.Path, repository.RelativePath)) is { } problem)
        {
            return AccountResult<Guid>.Failed(problem);
        }

        storage.Store.Add(repository);
        await storage.Store.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Repository {Owner}/{Name} was created on datastore {Datastore}.", acting.UserName, name, datastore.Name);
        return AccountResult<Guid>.Ok(repository.Id);
    }

    private async Task<Datastore?> PickDatastoreAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is { } chosen)
        {
            return await storage.Store.FindDatastoreAsync(chosen, cancellationToken);
        }

        var writable = (await storage.Store.GetDatastoresAsync(cancellationToken)).Where(d => !d.IsReadOnly).ToList();
        return writable.MaxBy(d => storage.Folders.GetSpace(d.Path)?.FreeBytes ?? -1);
    }
}

/// <summary>Renames a repository or changes its quota (owner or administrator).</summary>
public sealed record UpdateRepositoryCommand(
    [property: Required] string ActingUserId,
    Guid RepositoryId,
    [property: Required] string Name,
    long? QuotaBytes) : ICommand<AccountResult>;

internal sealed class UpdateRepositoryCommandHandler(StorageContext storage) : ICommandHandler<UpdateRepositoryCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(UpdateRepositoryCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.RequireManagedAsync(acting, command.RepositoryId, cancellationToken);
        var texts = storage.Texts;
        var name = command.Name.Trim();
        if (!RepositoryNames.IsValid(name))
        {
            return AccountResult.Failed(texts.RepositoryNameInvalid);
        }

        if (command.QuotaBytes is <= 0)
        {
            return AccountResult.Failed(texts.QuotaInvalid);
        }

        if (!string.Equals(name, repository.Name, StringComparison.Ordinal))
        {
            if (repository.IsMoving)
            {
                return AccountResult.Failed(texts.RepositoryMoving);
            }

            if (await storage.NameTakenAsync(repository.OwnerId!, name, repository.Id, cancellationToken))
            {
                return AccountResult.Failed(string.Format(texts.RepositoryNameTaken, name));
            }

            var owner = await storage.Accounts.GetAsync(repository.OwnerId!, cancellationToken)
                ?? throw new InvalidOperationException("The owner does not exist.");
            var datastore = (await storage.Store.FindDatastoreAsync(repository.DatastoreId, cancellationToken))!;
            var from = repository.RelativePath;
            repository.Rename(name, owner.UserName);
            if (!RepositoryNames.AreSame(from, repository.RelativePath) && storage.Folders.Exists(datastore.Path, repository.RelativePath))
            {
                return AccountResult.Failed(string.Format(texts.RepositoryNameTaken, name));
            }

            if (storage.TryFolder(datastore, from, () => storage.Folders.Move(datastore.Path, from, repository.RelativePath)) is { } problem)
            {
                return AccountResult.Failed(problem);
            }
        }

        repository.SetQuota(command.QuotaBytes);
        await storage.Store.SaveChangesAsync(cancellationToken);
        return AccountResult.Success;
    }
}

/// <summary>Gives a repository to another user (administrators only).</summary>
public sealed record SetRepositoryOwnerCommand(
    [property: Required] string ActingUserId,
    Guid RepositoryId,
    [property: Required] string OwnerUserName) : ICommand<AccountResult>;

internal sealed class SetRepositoryOwnerCommandHandler(
    StorageContext storage, IAccessStore access, ILogger<SetRepositoryOwnerCommandHandler> logger)
    : ICommandHandler<SetRepositoryOwnerCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(SetRepositoryOwnerCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.RequireManagedAsync(acting, command.RepositoryId, cancellationToken);
        var texts = storage.Texts;
        var owner = await storage.Accounts.FindByNameAsync(command.OwnerUserName.Trim(), cancellationToken);
        if (owner is null)
        {
            return AccountResult.Failed(string.Format(texts.UserUnknown, command.OwnerUserName.Trim()));
        }

        if (owner.Id == repository.OwnerId)
        {
            return AccountResult.Success;
        }

        if (repository.IsMoving)
        {
            return AccountResult.Failed(texts.RepositoryMoving);
        }

        if (await storage.NameTakenAsync(owner.Id, repository.Name, repository.Id, cancellationToken))
        {
            return AccountResult.Failed(string.Format(texts.RepositoryNameTaken, $"{owner.UserName}/{repository.Name}"));
        }

        var datastore = (await storage.Store.FindDatastoreAsync(repository.DatastoreId, cancellationToken))!;
        var from = repository.RelativePath;
        repository.SetOwner(owner.Id, owner.UserName);
        if (storage.Folders.Exists(datastore.Path, repository.RelativePath))
        {
            return AccountResult.Failed(string.Format(texts.RepositoryNameTaken, $"{owner.UserName}/{repository.Name}"));
        }

        if (storage.TryFolder(datastore, from, () => storage.Folders.Move(datastore.Path, from, repository.RelativePath)) is { } problem)
        {
            return AccountResult.Failed(problem);
        }

        await SshKeyRules.PruneAsync(access, repository, cancellationToken);
        await storage.Store.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Repository {Name} now belongs to {Owner}.", repository.Name, owner.UserName);
        return AccountResult.Success;
    }
}

/// <summary>Shares a repository with a user or changes their access (owner or administrator).</summary>
public sealed record ShareRepositoryCommand(
    [property: Required] string ActingUserId,
    Guid RepositoryId,
    [property: Required] string UserName,
    RepositoryAccess Access) : ICommand<AccountResult>;

internal sealed class ShareRepositoryCommandHandler(StorageContext storage) : ICommandHandler<ShareRepositoryCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(ShareRepositoryCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.RequireManagedAsync(acting, command.RepositoryId, cancellationToken);
        if (!Enum.IsDefined(command.Access))
        {
            throw new ArgumentException($"Unknown access {command.Access}.", nameof(command));
        }

        var user = await storage.Accounts.FindByNameAsync(command.UserName.Trim(), cancellationToken);
        if (user is null)
        {
            return AccountResult.Failed(string.Format(storage.Texts.UserUnknown, command.UserName.Trim()));
        }

        if (user.Id == repository.OwnerId)
        {
            return AccountResult.Failed(storage.Texts.ShareWithOwner);
        }

        repository.Share(user.Id, command.Access);
        await storage.Store.SaveChangesAsync(cancellationToken);
        return AccountResult.Success;
    }
}

/// <summary>Ends a share; owners and administrators can end any, users their own ("leave").</summary>
public sealed record RemoveShareCommand(
    [property: Required] string ActingUserId,
    Guid RepositoryId,
    [property: Required] string UserId) : ICommand;

internal sealed class RemoveShareCommandHandler(StorageContext storage, IAccessStore access) : ICommandHandler<RemoveShareCommand>
{
    public async Task HandleAsync(RemoveShareCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(command.RepositoryId, cancellationToken);
        if (repository is null || !(StorageContext.CanManage(acting, repository) || command.UserId == acting.Id))
        {
            throw new UnauthorizedAccessException("This share may not be removed by this user.");
        }

        if (repository.Unshare(command.UserId))
        {
            await SshKeyRules.PruneAsync(access, repository, cancellationToken);
            await storage.Store.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>Moves a repository to the trash: nobody can use it, and it is removed after 7 days.</summary>
public sealed record DeleteRepositoryCommand([property: Required] string ActingUserId, Guid RepositoryId) : ICommand<AccountResult>;

internal sealed class DeleteRepositoryCommandHandler(StorageContext storage, ILogger<DeleteRepositoryCommandHandler> logger)
    : ICommandHandler<DeleteRepositoryCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(DeleteRepositoryCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.RequireManagedAsync(acting, command.RepositoryId, cancellationToken);
        if (repository.IsMoving)
        {
            return AccountResult.Failed(storage.Texts.RepositoryMoving);
        }

        var datastore = (await storage.Store.FindDatastoreAsync(repository.DatastoreId, cancellationToken))!;
        var from = repository.RelativePath;
        repository.MoveToTrash(storage.Now);
        if (storage.TryFolder(datastore, from, () => storage.Folders.Move(datastore.Path, from, repository.RelativePath)) is { } problem)
        {
            return AccountResult.Failed(problem);
        }

        await storage.Store.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Repository {Name} was moved to the trash by {User}.", repository.Name, acting.UserName);
        return AccountResult.Success;
    }
}

/// <summary>Measures the space a repository takes now instead of waiting for the next regular measurement.</summary>
public sealed record MeasureRepositoryCommand([property: Required] string ActingUserId, Guid RepositoryId) : ICommand;

internal sealed class MeasureRepositoryCommandHandler(StorageContext storage) : ICommandHandler<MeasureRepositoryCommand>
{
    public async Task HandleAsync(MeasureRepositoryCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(command.RepositoryId, cancellationToken);
        if (repository is null || repository.IsDeleted || !StorageContext.CanSee(acting, repository))
        {
            throw new UnauthorizedAccessException("The repository does not exist or may not be seen by this user.");
        }

        var datastore = (await storage.Store.FindDatastoreAsync(repository.DatastoreId, cancellationToken))!;
        repository.RecordUsage(StorageMaintenance.MeasureSafely(storage, datastore, repository), storage.Now);
        await storage.Store.SaveChangesAsync(cancellationToken);
    }
}
