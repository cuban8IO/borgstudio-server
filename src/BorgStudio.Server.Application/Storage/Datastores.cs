using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Storage;

/// <param name="Problem">What is wrong with the folder right now (e.g. a disk that is not mounted).</param>
/// <param name="UsedBytes">Space its repositories take, those in the trash included.</param>
public sealed record DatastoreInfo(
    Guid Id,
    string Name,
    string Path,
    bool IsReadOnly,
    StorageSpace? Space,
    DatastoreProblem? Problem,
    int RepositoryCount,
    int TrashCount,
    long UsedBytes);

/// <summary>All datastores (administrators).</summary>
public sealed record GetDatastoresQuery([property: Required] string ActingUserId) : IQuery<IReadOnlyList<DatastoreInfo>>;

internal sealed class GetDatastoresQueryHandler(StorageContext storage) : IQueryHandler<GetDatastoresQuery, IReadOnlyList<DatastoreInfo>>
{
    public async Task<IReadOnlyList<DatastoreInfo>> HandleAsync(GetDatastoresQuery query, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(query.ActingUserId, cancellationToken);
        var repositories = await storage.Store.GetRepositoriesAsync(cancellationToken);
        return (await storage.Store.GetDatastoresAsync(cancellationToken))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d =>
            {
                var on = repositories.Where(r => r.DatastoreId == d.Id).ToList();
                return new DatastoreInfo(
                    d.Id,
                    d.Name,
                    d.Path,
                    d.IsReadOnly,
                    storage.Folders.GetSpace(d.Path),
                    storage.Folders.Check(d.Path),
                    on.Count(r => !r.IsDeleted),
                    on.Count(r => r.IsDeleted),
                    on.Sum(r => r.UsedBytes));
            })
            .ToList();
    }
}

/// <summary>Adds a datastore: an existing folder the server may write to.</summary>
public sealed record CreateDatastoreCommand(
    [property: Required] string ActingUserId,
    [property: Required] string Name,
    [property: Required] string Path) : ICommand<AccountResult>;

internal sealed class CreateDatastoreCommandHandler(StorageContext storage, ILogger<CreateDatastoreCommandHandler> logger)
    : ICommandHandler<CreateDatastoreCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(CreateDatastoreCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var existing = await storage.Store.GetDatastoresAsync(cancellationToken);
        if (DatastoreRules.CheckName(storage, existing, command.Name, null) is { } nameError)
        {
            return AccountResult.Failed(nameError);
        }

        var path = storage.Folders.Normalize(command.Path);
        if (path is null)
        {
            return AccountResult.Failed(storage.Texts.DatastorePathNotAbsolute);
        }

        if (existing.FirstOrDefault(d => storage.Folders.Overlap(d.Path, path)) is { } other)
        {
            return AccountResult.Failed(string.Format(storage.Texts.DatastorePathOverlaps, other.Name));
        }

        if (storage.Folders.Check(path) is { } problem)
        {
            return AccountResult.Failed(DatastoreRules.Describe(storage.Texts, problem));
        }

        storage.Store.Add(Datastore.Create(command.Name, path, storage.Now));
        await storage.Store.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Datastore {Name} was added at {Path}.", command.Name.Trim(), path);
        return AccountResult.Success;
    }
}

/// <summary>Renames a datastore or makes it read-only (no new repositories, no moves to it).</summary>
public sealed record UpdateDatastoreCommand(
    [property: Required] string ActingUserId,
    Guid DatastoreId,
    [property: Required] string Name,
    bool IsReadOnly) : ICommand<AccountResult>;

internal sealed class UpdateDatastoreCommandHandler(StorageContext storage) : ICommandHandler<UpdateDatastoreCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(UpdateDatastoreCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var existing = await storage.Store.GetDatastoresAsync(cancellationToken);
        var datastore = existing.FirstOrDefault(d => d.Id == command.DatastoreId)
            ?? throw new InvalidOperationException("The datastore does not exist.");
        if (DatastoreRules.CheckName(storage, existing, command.Name, datastore.Id) is { } nameError)
        {
            return AccountResult.Failed(nameError);
        }

        datastore.Rename(command.Name);
        datastore.SetReadOnly(command.IsReadOnly);
        await storage.Store.SaveChangesAsync(cancellationToken);
        return AccountResult.Success;
    }
}

/// <summary>Removes an empty datastore from the list; its folder stays on disk.</summary>
public sealed record DeleteDatastoreCommand([property: Required] string ActingUserId, Guid DatastoreId) : ICommand<AccountResult>;

internal sealed class DeleteDatastoreCommandHandler(StorageContext storage) : ICommandHandler<DeleteDatastoreCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(DeleteDatastoreCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var all = await storage.Store.GetDatastoresAsync(cancellationToken);
        var datastore = all.FirstOrDefault(d => d.Id == command.DatastoreId)
            ?? throw new InvalidOperationException("The datastore does not exist.");
        if (all.Count == 1)
        {
            return AccountResult.Failed(storage.Texts.DatastoreLast);
        }

        // Repositories in the trash and moves to this datastore count too.
        var repositories = await storage.Store.GetRepositoriesAsync(cancellationToken);
        if (repositories.Any(r => r.DatastoreId == datastore.Id || r.MoveTargetDatastoreId == datastore.Id))
        {
            return AccountResult.Failed(storage.Texts.DatastoreNotEmpty);
        }

        storage.Store.Remove(datastore);
        await storage.Store.SaveChangesAsync(cancellationToken);
        return AccountResult.Success;
    }
}

/// <summary>Moves a repository to another datastore in the background (administrators).</summary>
public sealed record MoveRepositoryCommand(
    [property: Required] string ActingUserId,
    Guid RepositoryId,
    Guid TargetDatastoreId) : ICommand<AccountResult>;

internal sealed class MoveRepositoryCommandHandler(StorageContext storage, RepositoryMoves moves)
    : ICommandHandler<MoveRepositoryCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(MoveRepositoryCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.RequireManagedAsync(acting, command.RepositoryId, cancellationToken);
        if (repository.IsMoving)
        {
            return AccountResult.Failed(storage.Texts.RepositoryMoving);
        }

        var target = await storage.Store.FindDatastoreAsync(command.TargetDatastoreId, cancellationToken);
        if (target is null || target.Id == repository.DatastoreId)
        {
            return AccountResult.Failed(storage.Texts.DatastoreUnknown);
        }

        if (target.IsReadOnly)
        {
            return AccountResult.Failed(storage.Texts.DatastoreReadOnly);
        }

        if (storage.Folders.Exists(target.Path, repository.RelativePath))
        {
            return AccountResult.Failed(string.Format(storage.Texts.FolderProblem, $"{target.Path}/{repository.RelativePath}"));
        }

        repository.RequestMove(target.Id);
        await storage.Store.SaveChangesAsync(cancellationToken);
        moves.Wake();
        return AccountResult.Success;
    }
}

internal static class DatastoreRules
{
    public static string? CheckName(StorageContext storage, IEnumerable<Datastore> existing, string name, Guid? except)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > Datastore.MaxNameLength)
        {
            return storage.Texts.DatastoreNameInvalid;
        }

        return existing.Any(d => d.Id != except && string.Equals(d.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ? string.Format(storage.Texts.DatastoreNameTaken, trimmed)
            : null;
    }

    public static string Describe(IStorageTexts texts, DatastoreProblem problem) => problem switch
    {
        DatastoreProblem.Missing => texts.DatastorePathMissing,
        DatastoreProblem.NotWritable => texts.DatastorePathNotWritable,
        _ => texts.DatastorePathContainsServerData,
    };
}
