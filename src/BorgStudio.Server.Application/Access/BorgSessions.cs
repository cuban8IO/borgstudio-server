using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Statistics;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Access;
using BorgStudio.Server.Domain.Statistics;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Access;

/// <summary>
/// A borg client connected with an SSH key: decides whether it may open the repository and how
/// (append only, storage quota). Runs in the process that serves the connection, once per connection.
/// </summary>
public sealed record OpenBorgSessionCommand(Guid KeyId, int ProcessId) : ICommand<BorgSessionPlan>;

/// <param name="Refusal">Why the connection is refused (shown to the borg client); <c>null</c> if it may go ahead.</param>
/// <param name="RepositoryPath">The repository's folder on this server, or on <paramref name="Remote"/> relative to its home.</param>
/// <param name="StorageQuotaBytes">Limit for the repository, from its own quota and its owner's; <c>null</c> = none.</param>
/// <param name="Remote">The external datastore borg runs on; <c>null</c> for a folder on this server.</param>
public sealed record BorgSessionPlan(
    string? Refusal,
    Guid SessionId = default,
    Guid RepositoryId = default,
    string RepositoryPath = "",
    bool AppendOnly = false,
    long? StorageQuotaBytes = null,
    RemoteTarget? Remote = null)
{
    public static BorgSessionPlan Refuse(string reason) => new(reason);
}

internal sealed class OpenBorgSessionCommandHandler(
    StorageContext storage,
    IAccessStore access,
    IProcessInfo processes,
    ILogger<OpenBorgSessionCommandHandler> logger) : ICommandHandler<OpenBorgSessionCommand, BorgSessionPlan>
{
    // The client sees these through borg ("Remote: …"); the server's language is not known there.
    public async Task<BorgSessionPlan> HandleAsync(OpenBorgSessionCommand command, CancellationToken cancellationToken)
    {
        var key = await access.FindKeyAsync(command.KeyId, cancellationToken);
        if (key is null)
        {
            return BorgSessionPlan.Refuse("This SSH key is no longer known to BorgStudio Server.");
        }

        var repository = await storage.Store.FindRepositoryAsync(key.RepositoryId, cancellationToken);
        if (repository is null || repository.IsDeleted)
        {
            return BorgSessionPlan.Refuse("The repository was deleted.");
        }

        if (repository.IsMoving)
        {
            return BorgSessionPlan.Refuse("The repository is being moved to another datastore; try again later.");
        }

        var user = await storage.Accounts.GetAsync(key.UserId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return BorgSessionPlan.Refuse("The account of this SSH key is disabled.");
        }

        if (repository.AccessFor(user.Id) is not { } level)
        {
            return BorgSessionPlan.Refuse("The account of this SSH key has no access to the repository any more.");
        }

        Device? device = null;
        if (key.DeviceId is { } deviceId)
        {
            device = await access.FindDeviceAsync(deviceId, cancellationToken);
            if (device is null || device.IsExpired(storage.Now))
            {
                return BorgSessionPlan.Refuse("The paired app or token of this SSH key was revoked or has expired.");
            }

            device.Touch(storage.Now);
        }

        var datastore = await storage.Store.FindDatastoreAsync(repository.DatastoreId, cancellationToken)
            ?? throw new InvalidOperationException("The repository's datastore does not exist.");
        var quota = await QuotaAsync(repository, cancellationToken);

        await RemoveDeadSessionsAsync(access, processes, repository.Id, cancellationToken);
        var by = device?.Name ?? key.Name;
        var session = RepositorySession.Start(repository.Id, key.Id, command.ProcessId, storage.Now, user.Id, by, repository.UsedBytes);
        access.Add(session);
        key.MarkUsed(storage.Now);
        repository.RecordAccess(user.Id, by, storage.Now);
        await access.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "borg session for {Repository} by {User} ({Key}), {Access}, quota {Quota}.",
            repository.Name, user.UserName, key.Name, level, quota);
        return new BorgSessionPlan(
            null,
            session.Id,
            repository.Id,
            datastore.IsExternal ? repository.RelativePath : StorageContext.LocationOf(datastore, repository.RelativePath),
            level == RepositoryAccess.AppendOnly,
            quota,
            datastore.IsExternal ? RemoteTarget.Of(datastore) : null);
    }

    /// <summary>
    /// The repository's own quota, and what is left of its owner's: the owner's quota minus what their
    /// other repositories take. Over the owner's quota the repository may not grow (deleting still works).
    /// </summary>
    private async Task<long?> QuotaAsync(BackupRepository repository, CancellationToken cancellationToken)
    {
        long? quota = repository.QuotaBytes;
        if (repository.OwnerId is { } ownerId)
        {
            var policy = await storage.Store.GetPolicyAsync(ownerId, cancellationToken);
            if (policy.QuotaBytes is { } ownerQuota)
            {
                var all = await storage.Store.GetRepositoriesAsync(cancellationToken);
                var others = StorageContext.UsedBy(all, ownerId) - (repository.IsDeleted ? 0 : repository.UsedBytes);
                var allowed = Math.Max(ownerQuota - others, repository.UsedBytes);
                quota = quota is { } own ? Math.Min(own, allowed) : allowed;
            }
        }

        // borg reads 0 as "no quota".
        return quota is { } q ? Math.Max(q, 1) : null;
    }

    internal static async Task RemoveDeadSessionsAsync(IAccessStore access, IProcessInfo processes, Guid? repositoryId, CancellationToken cancellationToken)
    {
        foreach (var session in await access.GetSessionsAsync(repositoryId, cancellationToken))
        {
            if (!processes.IsAlive(session.ProcessId))
            {
                access.Remove(session);
            }
        }
    }
}

/// <summary>
/// The borg connection ended: the session goes away, the repository is measured again, and the
/// connection is recorded for the statistics (traffic, duration, growth, borg's exit code).
/// </summary>
public sealed record CloseBorgSessionCommand(Guid SessionId, long BytesReceived = 0, long BytesSent = 0, int? ExitCode = null) : ICommand;

internal sealed class CloseBorgSessionCommandHandler(StorageContext storage, IAccessStore access, IStatisticsStore statistics)
    : ICommandHandler<CloseBorgSessionCommand>
{
    public async Task HandleAsync(CloseBorgSessionCommand command, CancellationToken cancellationToken)
    {
        var session = await access.FindSessionAsync(command.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        access.Remove(session);
        var repository = await storage.Store.FindRepositoryAsync(session.RepositoryId, cancellationToken);
        var usedAfter = session.UsedBefore;
        if (repository is { IsDeleted: false })
        {
            var datastore = (await storage.Store.FindDatastoreAsync(repository.DatastoreId, cancellationToken))!;
            usedAfter = await StorageMaintenance.MeasureSafelyAsync(storage, datastore, repository, cancellationToken);
            repository.RecordUsage(usedAfter, storage.Now);
        }

        if (repository is not null)
        {
            statistics.Add(ConnectionRecord.Create(
                session.RepositoryId,
                session.UserId,
                session.By,
                session.StartedAt,
                storage.Now,
                command.BytesReceived,
                command.BytesSent,
                command.ExitCode,
                session.UsedBefore,
                usedAfter));
        }

        await access.SaveChangesAsync(cancellationToken);
    }
}
