using System.Collections.Concurrent;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Storage;

/// <summary>Regular work of the server: measures every repository and empties expired trash (run by a background worker).</summary>
public sealed record RunStorageMaintenanceCommand : ICommand;

internal sealed class RunStorageMaintenanceCommandHandler(StorageContext storage, ILogger<RunStorageMaintenanceCommandHandler> logger)
    : ICommandHandler<RunStorageMaintenanceCommand>
{
    public async Task HandleAsync(RunStorageMaintenanceCommand command, CancellationToken cancellationToken)
    {
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        var repositories = await storage.Store.GetRepositoriesAsync(cancellationToken);
        foreach (var repository in repositories.Where(r => !r.IsMoving))
        {
            cancellationToken.ThrowIfCancellationRequested();
            repository.RecordUsage(StorageMaintenance.MeasureSafely(storage, datastores[repository.DatastoreId], repository), storage.Now);
        }

        await storage.Store.SaveChangesAsync(cancellationToken);

        var expired = repositories.Where(r => r.PurgeAt is { } at && at <= storage.Now).ToList();
        await StorageMaintenance.PurgeAsync(storage, expired, logger, cancellationToken);
    }
}

internal static class StorageMaintenance
{
    /// <summary>Measures a repository; a disk that cannot be read keeps the last known value.</summary>
    public static long MeasureSafely(StorageContext storage, Datastore datastore, BackupRepository repository)
    {
        try
        {
            return storage.Folders.Measure(datastore.Path, repository.RelativePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return repository.UsedBytes;
        }
    }

    /// <summary>Deletes repositories and their folders for good; returns what could not be deleted.</summary>
    public static async Task<IReadOnlyList<string>> PurgeAsync(
        StorageContext storage, IEnumerable<BackupRepository> repositories, ILogger logger, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        foreach (var repository in repositories)
        {
            var datastore = datastores[repository.DatastoreId];
            if (storage.TryFolder(datastore, repository.RelativePath, () => storage.Folders.Delete(datastore.Path, repository.RelativePath)) is { } problem)
            {
                logger.LogWarning("Repository {Name} could not be deleted: {Problem}", repository.Name, problem);
                errors.Add(problem);
                continue;
            }

            storage.Store.Remove(repository);
            logger.LogInformation("Repository {Name} was deleted for good.", repository.Name);
        }

        await storage.Store.SaveChangesAsync(cancellationToken);
        return errors;
    }
}

/// <summary>How far a move to another datastore has come.</summary>
public sealed record RepositoryMoveProgress(long CopiedBytes, long TotalBytes);

/// <summary>Wakes the background worker for moves and keeps their progress for the pages (singleton).</summary>
public sealed class RepositoryMoves
{
    private readonly SemaphoreSlim wake = new(0);
    private readonly ConcurrentDictionary<Guid, RepositoryMoveProgress> progress = new();

    /// <summary>Only one run of the moves at a time, also when a test starts one directly.</summary>
    internal SemaphoreSlim RunLock { get; } = new(1, 1);

    public void Wake() => wake.Release();

    /// <summary>Waits for a new move request, or at most <paramref name="timeout"/> (moves waiting for a running backup retry then).</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => wake.WaitAsync(timeout, cancellationToken);

    public RepositoryMoveProgress? Progress(Guid repositoryId) => progress.TryGetValue(repositoryId, out var p) ? p : null;

    internal void Report(Guid repositoryId, long copied, long total) => progress[repositoryId] = new RepositoryMoveProgress(copied, total);

    internal void Clear(Guid repositoryId) => progress.TryRemove(repositoryId, out _);
}

/// <summary>
/// Carries out the requested moves to other datastores, one after the other. A repository someone is
/// backing up to right now waits for the next run (no new connections start while a move is pending).
/// </summary>
public sealed record RunRepositoryMovesCommand : ICommand;

internal sealed class RunRepositoryMovesCommandHandler(
    StorageContext storage,
    RepositoryMoves moves,
    IAccessStore access,
    IProcessInfo processes,
    ILogger<RunRepositoryMovesCommandHandler> logger) : ICommandHandler<RunRepositoryMovesCommand>
{
    public async Task HandleAsync(RunRepositoryMovesCommand command, CancellationToken cancellationToken)
    {
        await moves.RunLock.WaitAsync(cancellationToken);
        try
        {
            var datastores = await storage.DatastoresByIdAsync(cancellationToken);
            var pending = (await storage.Store.GetRepositoriesAsync(cancellationToken)).Where(r => r.IsMoving).ToList();
            foreach (var repository in pending)
            {
                await OpenBorgSessionCommandHandler.RemoveDeadSessionsAsync(access, processes, repository.Id, cancellationToken);
                await access.SaveChangesAsync(cancellationToken);
                if ((await access.GetSessionsAsync(repository.Id, cancellationToken)).Count > 0)
                {
                    logger.LogInformation("Repository {Name} is in use; its move waits.", repository.Name);
                    continue;
                }

                await MoveAsync(repository, datastores, cancellationToken);
            }
        }
        finally
        {
            moves.RunLock.Release();
        }
    }

    private async Task MoveAsync(BackupRepository repository, IReadOnlyDictionary<Guid, Datastore> datastores, CancellationToken cancellationToken)
    {
        var source = datastores[repository.DatastoreId];
        var target = datastores[repository.MoveTargetDatastoreId!.Value];
        var total = StorageMaintenance.MeasureSafely(storage, source, repository);
        moves.Report(repository.Id, 0, total);
        logger.LogInformation("Moving repository {Name} from {Source} to {Target}.", repository.Name, source.Name, target.Name);
        try
        {
            await storage.Folders.MoveToDatastoreAsync(
                source.Path,
                target.Path,
                repository.RelativePath,
                new Progress(moves, repository.Id, total),
                cancellationToken);
            repository.CompleteMove();
            repository.RecordUsage(total, storage.Now);
            logger.LogInformation("Repository {Name} is now on {Target}.", repository.Name, target.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            repository.FailMove(e.Message);
            logger.LogWarning(e, "Repository {Name} could not be moved to {Target}.", repository.Name, target.Name);
        }
        finally
        {
            moves.Clear(repository.Id);
        }

        // Saved without the request's token: a move that went through must be recorded even at shutdown.
        await storage.Store.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Reports synchronously; <see cref="Progress{T}"/> would post to a thread pool callback.</summary>
    private sealed class Progress(RepositoryMoves moves, Guid repositoryId, long total) : IProgress<long>
    {
        public void Report(long value) => moves.Report(repositoryId, value, total);
    }
}
