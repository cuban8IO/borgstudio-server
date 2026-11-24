using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Statistics;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Statistics;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Statistics;

/// <summary>Writes the daily storage history (repositories and datastores) from the regular measurement.</summary>
internal sealed class StatisticsRecorder(IStatisticsStore statistics, StorageContext storage, TimeProvider timeProvider)
{
    public async Task RecordStorageAsync(
        IEnumerable<Datastore> datastores, IEnumerable<BackupRepository> repositories, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var all = repositories.ToList();
        foreach (var repository in all.Where(r => !r.IsDeleted))
        {
            await statistics.SetStorageSampleAsync(StorageSubject.Repository, repository.Id, today, repository.UsedBytes, null, null, cancellationToken);
        }

        foreach (var datastore in datastores)
        {
            var used = all.Where(r => r.DatastoreId == datastore.Id).Sum(r => r.UsedBytes);
            var space = storage.SpaceOf(datastore);
            await statistics.SetStorageSampleAsync(
                StorageSubject.Datastore, datastore.Id, today, used, space?.TotalBytes, space?.FreeBytes, cancellationToken);
        }

        await statistics.PruneAsync(timeProvider.GetUtcNow(), cancellationToken);
        await statistics.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// One minute of the system's load, from the background worker that reads it. Whole hours and days
/// are summed up as they end; old samples go when their retention is over.
/// </summary>
public sealed record RecordSystemSampleCommand(
    DateTimeOffset At,
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskReadBytes,
    long DiskWrittenBytes,
    long NetworkReceivedBytes,
    long NetworkSentBytes) : ICommand;

internal sealed class RecordSystemSampleCommandHandler(IStatisticsStore statistics) : ICommandHandler<RecordSystemSampleCommand>
{
    public async Task HandleAsync(RecordSystemSampleCommand command, CancellationToken cancellationToken)
    {
        var minute = Truncate(command.At, TimeSpan.FromMinutes(1));
        statistics.Add(SystemSample.Minute(
            minute,
            Math.Clamp(command.CpuPercent, 0, 100),
            command.MemoryUsedBytes,
            command.MemoryTotalBytes,
            command.DiskReadBytes,
            command.DiskWrittenBytes,
            command.NetworkReceivedBytes,
            command.NetworkSentBytes));
        await statistics.SaveChangesAsync(cancellationToken);

        await RollUpAsync(SampleResolution.Minute, SampleResolution.Hour, TimeSpan.FromHours(1), minute, cancellationToken);
        await RollUpAsync(SampleResolution.Hour, SampleResolution.Day, TimeSpan.FromDays(1), minute, cancellationToken);
    }

    /// <summary>Sums up every finished period since the last one that was summed up (also after the server was off).</summary>
    private async Task RollUpAsync(
        SampleResolution from, SampleResolution to, TimeSpan period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = Truncate(now, period);
        var latest = await statistics.GetLatestSystemSampleAsync(to, cancellationToken);
        var start = latest is null ? current - period * 2 : latest.At + period;
        if (start >= current)
        {
            return;
        }

        var parts = await statistics.GetSystemSamplesAsync(from, start, cancellationToken);
        for (var periodStart = start; periodStart < current; periodStart += period)
        {
            var end = periodStart + period;
            var inPeriod = parts.Where(p => p.At >= periodStart && p.At < end).ToList();
            if (inPeriod.Count > 0)
            {
                statistics.Add(SystemSample.Aggregate(to, periodStart, inPeriod));
            }
        }

        await statistics.SaveChangesAsync(cancellationToken);
    }

    internal static DateTimeOffset Truncate(DateTimeOffset value, TimeSpan period)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.UtcTicks - (utc.UtcTicks % period.Ticks), TimeSpan.Zero);
    }
}

/// <summary>
/// A BorgStudio app reports a run (backup, check, prune, compact) of a repository it may use. The
/// server cannot read the encrypted repository, so this is where archives and savings come from.
/// </summary>
public sealed record AddAppReportCommand(
    [property: Required] string UserId,
    string? By,
    Guid RepositoryId,
    AppReportKind Kind,
    AppReportOutcome Outcome,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    string? ArchiveName,
    long? OriginalBytes,
    long? CompressedBytes,
    long? DeduplicatedBytes,
    long? Files,
    int? Archives,
    long? TotalOriginalBytes,
    long? TotalCompressedBytes,
    long? UniqueCompressedBytes,
    string? Message) : ICommand<AccountResult>;

internal sealed class AddAppReportCommandHandler(StorageContext storage, IStatisticsStore statistics)
    : ICommandHandler<AddAppReportCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(AddAppReportCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.UserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(command.RepositoryId, cancellationToken);
        if (repository is null || repository.IsDeleted || repository.AccessFor(acting.Id) is null)
        {
            return AccountResult.Failed(storage.Texts.NoAccessToRepository);
        }

        statistics.Add(AppReport.Create(
            repository.Id,
            acting.Id,
            command.By,
            storage.Now,
            command.Kind,
            command.Outcome,
            command.StartedAt,
            command.DurationSeconds,
            command.ArchiveName,
            command.OriginalBytes,
            command.CompressedBytes,
            command.DeduplicatedBytes,
            command.Files,
            command.Archives,
            command.TotalOriginalBytes,
            command.TotalCompressedBytes,
            command.UniqueCompressedBytes,
            command.Message));
        await statistics.SaveChangesAsync(cancellationToken);
        return AccountResult.Success;
    }
}
