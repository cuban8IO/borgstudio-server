using System.Collections.Concurrent;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Storage;

/// <summary>
/// Regular work of the server: measures every repository and external datastore, keeps the daily
/// history for the statistics, and empties expired trash (run by a background worker).
/// </summary>
public sealed record RunStorageMaintenanceCommand : ICommand;

internal sealed class RunStorageMaintenanceCommandHandler(
    StorageContext storage,
    Statistics.StatisticsRecorder statistics,
    ILogger<RunStorageMaintenanceCommandHandler> logger) : ICommandHandler<RunStorageMaintenanceCommand>
{
    public async Task HandleAsync(RunStorageMaintenanceCommand command, CancellationToken cancellationToken)
    {
        var datastores = await storage.DatastoresByIdAsync(cancellationToken);
        var repositories = await storage.Store.GetRepositoriesAsync(cancellationToken);
        foreach (var group in repositories.Where(r => !r.IsMoving).GroupBy(r => r.DatastoreId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var datastore = datastores[group.Key];
            if (!datastore.IsExternal)
            {
                foreach (var repository in group)
                {
                    repository.RecordUsage(await StorageMaintenance.MeasureSafelyAsync(storage, datastore, repository, cancellationToken), storage.Now);
                }

                continue;
            }

            // External datastores in one go: one connection measures all of their repositories.
            try
            {
                var sizes = await storage.Remote.MeasureAsync(RemoteTarget.Of(datastore), [.. group.Select(r => r.RelativePath)], cancellationToken);
                foreach (var repository in group)
                {
                    repository.RecordUsage(sizes.GetValueOrDefault(repository.RelativePath, repository.UsedBytes), storage.Now);
                }
            }
            catch (RemoteStorageException e)
            {
                logger.LogWarning("Datastore {Name} could not be measured: {Message}", datastore.Name, e.Message);
            }
        }

        foreach (var datastore in datastores.Values.Where(d => d.IsExternal))
        {
            try
            {
                if (await storage.Remote.GetSpaceAsync(RemoteTarget.Of(datastore), cancellationToken) is { } space)
                {
                    datastore.RecordSpace(space.TotalBytes, space.FreeBytes, storage.Now);
                }
            }
            catch (RemoteStorageException e)
            {
                datastore.RecordError(e.Message);
                logger.LogWarning("Datastore {Name} could not be measured: {Message}", datastore.Name, e.Message);
            }
        }

        await storage.Store.SaveChangesAsync(cancellationToken);
        await statistics.RecordStorageAsync(datastores.Values, repositories, cancellationToken);

        var expired = repositories.Where(r => r.PurgeAt is { } at && at <= storage.Now).ToList();
        await StorageMaintenance.PurgeAsync(storage, expired, logger, cancellationToken);
    }
}

internal static class StorageMaintenance
{
    /// <summary>Measures a repository; a disk or connection that fails keeps the last known value.</summary>
    public static async Task<long> MeasureSafelyAsync(
        StorageContext storage, Datastore datastore, BackupRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            return await storage.MeasureAsync(datastore, repository.RelativePath, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RemoteStorageException)
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
            if (await storage.TryFolderAsync(datastore, repository.RelativePath, () => storage.DeleteAsync(datastore, repository.RelativePath, cancellationToken)) is { } problem)
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
        var total = await StorageMaintenance.MeasureSafelyAsync(storage, source, repository, cancellationToken);
        moves.Report(repository.Id, 0, total);
        logger.LogInformation("Moving repository {Name} from {Source} to {Target}.", repository.Name, source.Name, target.Name);
        try
        {
            var owner = repository.OwnerId is { } ownerId ? await storage.Accounts.GetAsync(ownerId, cancellationToken) : null;
            var targetPath = target.IsExternal
                ? RepositoryNames.IdFolder(repository.Id)
                : RepositoryNames.RelativePath(owner?.UserName ?? repository.FormerOwnerName ?? "_", repository.Name);
            await CopyAcrossAsync(source, repository.RelativePath, target, targetPath, new Progress(moves, repository.Id, total), cancellationToken);
            repository.CompleteMove(targetPath, target.IsExternal);
            repository.RecordUsage(total, storage.Now);
            logger.LogInformation("Repository {Name} is now on {Target}.", repository.Name, target.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RemoteStorageException)
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

    /// <summary>
    /// Moves the folder to the other datastore: on this server by renaming or copying, to and from
    /// external ones with rsync over SSH (between two external ones through a local staging folder).
    /// The source is deleted only after the copy is complete.
    /// </summary>
    private async Task CopyAcrossAsync(
        Datastore source, string sourcePath, Datastore target, string targetPath, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var folders = storage.Folders;
        if (!source.IsExternal && !target.IsExternal)
        {
            await folders.MoveToDatastoreAsync(source.Path, sourcePath, target.Path, targetPath, progress, cancellationToken);
            return;
        }

        if (await storage.ExistsAsync(target, targetPath, cancellationToken))
        {
            throw new IOException($"{target.Path}/{targetPath} already exists.");
        }

        string local;
        string? staging = null;
        if (source.IsExternal)
        {
            staging = folders.NewStagingFolder(target.IsExternal ? null : target.Path);
            await storage.Remote.DownloadAsync(RemoteTarget.Of(source), sourcePath, staging, cancellationToken);
            local = staging;
        }
        else
        {
            local = folders.FullPath(source.Path, sourcePath);
        }

        try
        {
            if (target.IsExternal)
            {
                await storage.Remote.UploadAsync(RemoteTarget.Of(target), local, targetPath, cancellationToken);
            }
            else
            {
                folders.Adopt(staging!, target.Path, targetPath);
                staging = null;
            }
        }
        finally
        {
            if (staging is not null)
            {
                folders.DeleteStaging(staging);
            }
        }

        if (source.IsExternal)
        {
            await storage.Remote.DeleteAsync(RemoteTarget.Of(source), sourcePath, cancellationToken);
        }
        else
        {
            folders.Delete(source.Path, sourcePath);
        }
    }

    /// <summary>Reports synchronously; <see cref="Progress{T}"/> would post to a thread pool callback.</summary>
    private sealed class Progress(RepositoryMoves moves, Guid repositoryId, long total) : IProgress<long>
    {
        public void Report(long value) => moves.Report(repositoryId, value, total);
    }
}
