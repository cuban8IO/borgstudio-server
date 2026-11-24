using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Statistics;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Statistics;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Statistics;

/// <summary>One repository in the overview, worst state first.</summary>
/// <param name="Reason">Why it is not fine, as a code the page turns into text.</param>
public sealed record OverviewRow(
    RepositorySummary Repository,
    Health Health,
    OverviewReason Reason,
    DateTimeOffset? LastConnection,
    AppReportOutcome? LastBackup,
    DateTimeOffset? LastBackupAt,
    DateOnly? FullAt);

public enum OverviewReason
{
    None,
    NeverUsed,
    QuotaFull,
    QuotaAlmostFull,
    FullSoon,
    BackupFailed,
    BackupWarnings,
    Idle,
    ConnectionFailed,
}

/// <summary>The earliest "full" among repositories, users and datastores the viewer may see.</summary>
public sealed record FullSoonest(string Subject, DateOnly Day);

public sealed record OverviewStatistics(
    long StoredBytes,
    long? OriginalBytes,
    double? SavingsPercent,
    double? SuccessRate,
    int RunsCounted,
    FullSoonest? FullSoonest,
    long TrafficReceived30,
    long TrafficSent30,
    IReadOnlyList<OverviewRow> Rows,
    IReadOnlyList<DayActivity> Calendar,
    SystemNow? System);

/// <summary>The system's load right now, for administrators.</summary>
public sealed record SystemNow(
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskBytesPerSecond,
    long NetworkBytesPerSecond,
    StorageSpace? DataDisk);

/// <summary>The overview: the user's (an administrator's: all) repositories and how they are doing.</summary>
public sealed record GetOverviewStatisticsQuery([property: Required] string ActingUserId) : IQuery<OverviewStatistics>;

internal sealed class GetOverviewStatisticsQueryHandler(
    StorageContext storage, IStatisticsStore statistics, IQueryHandler<GetRepositoriesQuery, RepositoryListView> repositories, TimeProvider timeProvider)
    : IQueryHandler<GetOverviewStatisticsQuery, OverviewStatistics>
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromDays(7);

    public async Task<OverviewStatistics> HandleAsync(GetOverviewStatisticsQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var view = await repositories.HandleAsync(new GetRepositoriesQuery(query.ActingUserId), cancellationToken);
        var visible = view.Repositories.Select(r => r.Id).ToHashSet();

        var connections = (await statistics.GetConnectionsAsync(null, now.AddDays(-StatisticsViews.CalendarDays), cancellationToken))
            .Where(c => visible.Contains(c.RepositoryId)).ToList();
        var reports = (await statistics.GetReportsAsync(null, now.AddDays(-365), cancellationToken))
            .Where(r => visible.Contains(r.RepositoryId)).ToList();
        var samples = (await statistics.GetStorageSamplesAsync(
                StorageSubject.Repository, null, DateOnly.FromDateTime(now.UtcDateTime.AddDays(-100)), cancellationToken))
            .Where(s => visible.Contains(s.SubjectId))
            .GroupBy(s => s.SubjectId)
            .ToDictionary(g => g.Key, g => g.Select(s => (s.Day, s.UsedBytes)).ToList());

        var rows = view.Repositories.Select(r => Row(r, connections, reports, samples, now)).ToList();

        // Savings: the latest report of every repository that has one.
        var latestTotals = reports
            .Where(r => r.TotalOriginalBytes is not null && r.UniqueCompressedBytes is not null)
            .GroupBy(r => r.RepositoryId)
            .Select(g => g.Last())
            .ToList();
        long? original = latestTotals.Count == 0 ? null : latestTotals.Sum(r => r.TotalOriginalBytes!.Value);
        long? stored = latestTotals.Count == 0 ? null : latestTotals.Sum(r => r.UniqueCompressedBytes!.Value);

        var recentBackups = reports.Where(r => r.Kind == AppReportKind.Backup && r.StartedAt >= now.AddDays(-30) && r.Outcome != AppReportOutcome.Skipped).ToList();
        var recentConnections = connections.Where(c => c.StartedAt >= now.AddDays(-30)).ToList();
        double? success;
        int counted;
        if (recentBackups.Count > 0)
        {
            counted = recentBackups.Count;
            success = recentBackups.Count(r => r.Outcome is AppReportOutcome.Succeeded or AppReportOutcome.Warnings) * 100.0 / counted;
        }
        else
        {
            var known = recentConnections.Where(c => c.ExitCode is not null).ToList();
            counted = known.Count;
            success = counted == 0 ? null : known.Count(c => c.ExitCode is 0 or 1) * 100.0 / counted;
        }

        return new OverviewStatistics(
            view.Repositories.Sum(r => r.UsedBytes),
            original,
            original is > 0 && stored is { } s ? (1 - (double)s / original.Value) * 100 : null,
            success,
            counted,
            await FullSoonestAsync(acting, view, rows, now, cancellationToken),
            recentConnections.Sum(c => c.BytesReceived),
            recentConnections.Sum(c => c.BytesSent),
            [.. rows.OrderByDescending(r => r.Health).ThenBy(r => r.Repository.FullName, StringComparer.OrdinalIgnoreCase)],
            StatisticsViews.Calendar(connections, reports, now),
            acting.IsAdministrator ? await SystemNowAsync(cancellationToken) : null);
    }

    private static OverviewRow Row(
        RepositorySummary repository,
        IReadOnlyList<ConnectionRecord> connections,
        IReadOnlyList<AppReport> reports,
        IReadOnlyDictionary<Guid, List<(DateOnly Day, long UsedBytes)>> samples,
        DateTimeOffset now)
    {
        var last = connections.LastOrDefault(c => c.RepositoryId == repository.Id);
        var backup = reports.LastOrDefault(r => r.RepositoryId == repository.Id && r.Kind == AppReportKind.Backup);
        var fullAt = StorageForecast.FullAt(samples.GetValueOrDefault(repository.Id) ?? [], repository.QuotaBytes);

        var (health, reason) = (Health.Ok, OverviewReason.None);
        void Worse(Health h, OverviewReason r)
        {
            if (h > health)
            {
                (health, reason) = (h, r);
            }
        }

        if (repository.QuotaBytes is { } quota && repository.UsedBytes >= quota)
        {
            Worse(Health.Failed, OverviewReason.QuotaFull);
        }
        else if (repository.QuotaBytes is { } q && repository.UsedBytes >= q * 0.9)
        {
            Worse(Health.Warning, OverviewReason.QuotaAlmostFull);
        }

        if (fullAt is { } day && day <= DateOnly.FromDateTime(now.UtcDateTime.AddDays(90)))
        {
            Worse(Health.Warning, OverviewReason.FullSoon);
        }

        if (backup is { Outcome: AppReportOutcome.Failed })
        {
            Worse(Health.Failed, OverviewReason.BackupFailed);
        }
        else if (backup is { Outcome: AppReportOutcome.Warnings })
        {
            Worse(Health.Warning, OverviewReason.BackupWarnings);
        }
        else if (backup is null && last is { ExitCode: >= 2 })
        {
            Worse(Health.Failed, OverviewReason.ConnectionFailed);
        }

        if (last is null && backup is null)
        {
            if (health == Health.Ok)
            {
                (health, reason) = (Health.Unknown, OverviewReason.NeverUsed);
            }
        }
        else if ((backup?.StartedAt ?? last!.StartedAt) < now - IdleAfter)
        {
            Worse(Health.Warning, OverviewReason.Idle);
        }

        return new OverviewRow(repository, health, reason, last?.StartedAt, backup?.Outcome, backup?.StartedAt, fullAt);
    }

    private async Task<FullSoonest?> FullSoonestAsync(
        AccountInfo acting, RepositoryListView view, IReadOnlyList<OverviewRow> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = rows.Where(r => r.FullAt is not null).Select(r => new FullSoonest(r.Repository.FullName, r.FullAt!.Value)).ToList();

        // The user's own quota, from the daily history of their repositories.
        if (view.Allowance.QuotaBytes is { } userQuota)
        {
            var own = view.Repositories.Where(r => r.IsOwner).Select(r => r.Id).ToHashSet();
            var history = (await statistics.GetStorageSamplesAsync(
                    StorageSubject.Repository, null, DateOnly.FromDateTime(now.UtcDateTime.AddDays(-100)), cancellationToken))
                .Where(s => own.Contains(s.SubjectId))
                .GroupBy(s => s.Day)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Sum(s => s.UsedBytes)))
                .ToList();
            if (StorageForecast.FullAt(history, userQuota) is { } day)
            {
                candidates.Add(new FullSoonest(acting.UserName, day));
            }
        }

        if (acting.IsAdministrator)
        {
            foreach (var datastore in await storage.Store.GetDatastoresAsync(cancellationToken))
            {
                var points = await GetRepositoryStatisticsQueryHandler.HistoryAsync(statistics, StorageSubject.Datastore, datastore.Id, now, cancellationToken);
                var withSpace = points.Where(p => p.TotalBytes is not null && p.FreeBytes is not null).ToList();
                if (withSpace.Count > 0
                    && StorageForecast.FullAt([.. withSpace.Select(p => (p.Day, p.TotalBytes!.Value - p.FreeBytes!.Value))], withSpace[^1].TotalBytes) is { } day)
                {
                    candidates.Add(new FullSoonest(datastore.Name, day));
                }
            }
        }

        return candidates.OrderBy(c => c.Day).FirstOrDefault();
    }

    private async Task<SystemNow?> SystemNowAsync(CancellationToken cancellationToken)
    {
        var latest = await statistics.GetLatestSystemSampleAsync(SampleResolution.Minute, cancellationToken);
        if (latest is null)
        {
            return null;
        }

        return new SystemNow(
            latest.CpuPercent,
            latest.MemoryUsedBytes,
            latest.MemoryTotalBytes,
            (latest.DiskReadBytes + latest.DiskWrittenBytes) / 60,
            (latest.NetworkReceivedBytes + latest.NetworkSentBytes) / 60,
            storage.Folders.DataSpace());
    }
}
