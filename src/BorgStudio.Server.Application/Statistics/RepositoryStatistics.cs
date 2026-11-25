using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Statistics;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Statistics;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Statistics;

/// <summary>One day of a storage history; datastores also have size and free space.</summary>
public sealed record DailyPoint(DateOnly Day, long UsedBytes, long? TotalBytes = null, long? FreeBytes = null);

/// <summary>One day of the activity calendar: how many connections or runs, and the worst outcome.</summary>
public sealed record DayActivity(DateOnly Day, int Count, Health Health);

public sealed record ConnectionItem(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string UserName,
    string By,
    long BytesReceived,
    long BytesSent,
    long Growth,
    Health Health);

public sealed record BackupItem(
    DateTimeOffset StartedAt,
    double DurationSeconds,
    AppReportOutcome Outcome,
    string? ArchiveName,
    long? OriginalBytes,
    long? DeduplicatedBytes,
    string? By);

/// <summary>What the BorgStudio app reported about the repository (it alone can read it).</summary>
/// <param name="SavingsPercent">How much less the repository takes than the files it holds.</param>
public sealed record ReportedStatistics(
    DateTimeOffset At,
    int? Archives,
    long? TotalOriginalBytes,
    long? UniqueCompressedBytes,
    double? SavingsPercent,
    double? DeduplicationRatio,
    double? CompressionRatio,
    IReadOnlyList<BackupItem> Backups,
    double? SuccessRate30,
    double? AverageDuration30,
    BackupItem? LastCheck);

/// <summary>Key figures of the last 30 days.</summary>
public sealed record ActivitySummary(
    int Connections,
    double? SuccessRate,
    long BytesReceived,
    long BytesSent,
    long Growth,
    TimeSpan? AverageDuration,
    DateTimeOffset? LastConnection);

public sealed record RepositoryStatistics(
    IReadOnlyList<DailyPoint> History,
    long? Limit,
    DateOnly? FullAt,
    IReadOnlyList<ConnectionItem> Connections,
    IReadOnlyList<DayActivity> Calendar,
    ActivitySummary Last30Days,
    ReportedStatistics? Reported);

/// <summary>Statistics of one repository; <c>null</c> if the user may not see it.</summary>
public sealed record GetRepositoryStatisticsQuery([property: Required] string ActingUserId, Guid RepositoryId) : IQuery<RepositoryStatistics?>;

internal sealed class GetRepositoryStatisticsQueryHandler(StorageContext storage, IStatisticsStore statistics, TimeProvider timeProvider)
    : IQueryHandler<GetRepositoryStatisticsQuery, RepositoryStatistics?>
{
    public async Task<RepositoryStatistics?> HandleAsync(GetRepositoryStatisticsQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(query.RepositoryId, cancellationToken);
        if (repository is null || repository.IsDeleted || !StorageContext.CanSee(acting, repository))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var users = await storage.UsersByIdAsync(cancellationToken);
        var history = await HistoryAsync(statistics, StorageSubject.Repository, repository.Id, now, cancellationToken);
        var connections = await statistics.GetConnectionsAsync(repository.Id, now.AddDays(-StatisticsViews.CalendarDays), cancellationToken);
        var reports = await statistics.GetReportsAsync(repository.Id, now.AddDays(-365), cancellationToken);

        return new RepositoryStatistics(
            history,
            repository.QuotaBytes,
            StorageForecast.FullAt([.. history.Select(p => (p.Day, p.UsedBytes))], repository.QuotaBytes),
            [.. connections.TakeLast(30).Select(c => StatisticsViews.Item(c, users))],
            StatisticsViews.Calendar(connections, reports, now),
            StatisticsViews.Summary(connections, now),
            StatisticsViews.Reported(reports, now));
    }

    internal static async Task<IReadOnlyList<DailyPoint>> HistoryAsync(
        IStatisticsStore statistics, StorageSubject subject, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        [.. (await statistics.GetStorageSamplesAsync(subject, id, DateOnly.FromDateTime(now.UtcDateTime.AddDays(-730)), cancellationToken))
            .Select(s => new DailyPoint(s.Day, s.UsedBytes, s.TotalBytes, s.FreeBytes))];
}

internal static class StatisticsViews
{
    /// <summary>Twelve weeks, like the app's calendar.</summary>
    public const int CalendarDays = 84;

    public static ConnectionItem Item(ConnectionRecord record, IReadOnlyDictionary<string, AccountInfo> users) => new(
        record.StartedAt,
        record.Duration,
        users.TryGetValue(record.UserId, out var user) ? user.UserName : "?",
        record.By,
        record.BytesReceived,
        record.BytesSent,
        record.Growth,
        StorageForecast.HealthOf(record.ExitCode));

    public static IReadOnlyList<DayActivity> Calendar(
        IEnumerable<ConnectionRecord> connections, IEnumerable<AppReport> reports, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var first = today.AddDays(-CalendarDays + 1);
        var byDay = new Dictionary<DateOnly, (int Count, Health Health)>();

        void Add(DateTimeOffset at, Health health)
        {
            var day = DateOnly.FromDateTime(at.UtcDateTime);
            if (day < first || day > today)
            {
                return;
            }

            var (count, worst) = byDay.GetValueOrDefault(day);
            byDay[day] = (count + 1, (Health)Math.Max((int)worst, (int)health));
        }

        // The app's reports say more than borg's exit code; connections only count where no report covers the day.
        var reported = reports.Where(r => r.Kind == AppReportKind.Backup).ToList();
        foreach (var report in reported)
        {
            Add(report.StartedAt, HealthOf(report.Outcome));
        }

        var reportedDays = reported.Select(r => DateOnly.FromDateTime(r.StartedAt.UtcDateTime)).ToHashSet();
        foreach (var connection in connections.Where(c => !reportedDays.Contains(DateOnly.FromDateTime(c.StartedAt.UtcDateTime))))
        {
            Add(connection.StartedAt, StorageForecast.HealthOf(connection.ExitCode));
        }

        return [.. Enumerable.Range(0, CalendarDays)
            .Select(i => first.AddDays(i))
            .Select(day => byDay.TryGetValue(day, out var value) ? new DayActivity(day, value.Count, value.Health) : new DayActivity(day, 0, Health.Unknown))];
    }

    public static ActivitySummary Summary(IReadOnlyList<ConnectionRecord> connections, DateTimeOffset now)
    {
        var recent = connections.Where(c => c.StartedAt >= now.AddDays(-30)).ToList();
        var known = recent.Where(c => c.ExitCode is not null).ToList();
        return new ActivitySummary(
            recent.Count,
            known.Count == 0 ? null : known.Count(c => c.ExitCode is 0 or 1) * 100.0 / known.Count,
            recent.Sum(c => c.BytesReceived),
            recent.Sum(c => c.BytesSent),
            recent.Sum(c => c.Growth),
            recent.Count == 0 ? null : TimeSpan.FromSeconds(recent.Average(c => c.Duration.TotalSeconds)),
            connections.Count == 0 ? null : connections[^1].StartedAt);
    }

    public static ReportedStatistics? Reported(IReadOnlyList<AppReport> reports, DateTimeOffset now)
    {
        if (reports.Count == 0)
        {
            return null;
        }

        var backups = reports.Where(r => r.Kind == AppReportKind.Backup).ToList();
        var latest = reports.LastOrDefault(r => r.Archives is not null || r.TotalOriginalBytes is not null) ?? reports[^1];
        double? Ratio(long? a, long? b) => a is { } x && b is { } y && y > 0 ? (double)x / y : null;
        var recent = backups.Where(r => r.StartedAt >= now.AddDays(-30) && r.Outcome != AppReportOutcome.Skipped).ToList();

        return new ReportedStatistics(
            latest.StartedAt,
            latest.Archives,
            latest.TotalOriginalBytes,
            latest.UniqueCompressedBytes,
            latest.TotalOriginalBytes is { } original && original > 0 && latest.UniqueCompressedBytes is { } stored
                ? (1 - (double)stored / original) * 100
                : null,
            Ratio(latest.TotalCompressedBytes, latest.UniqueCompressedBytes),
            Ratio(latest.TotalOriginalBytes, latest.TotalCompressedBytes),
            [.. backups.TakeLast(30).Select(Item)],
            recent.Count == 0 ? null : recent.Count(r => r.Outcome is AppReportOutcome.Succeeded or AppReportOutcome.Warnings) * 100.0 / recent.Count,
            recent.Count == 0 ? null : recent.Average(r => r.DurationSeconds),
            reports.LastOrDefault(r => r.Kind == AppReportKind.Check) is { } check ? Item(check) : null);
    }

    public static BackupItem Item(AppReport report) => new(
        report.StartedAt, report.DurationSeconds, report.Outcome, report.ArchiveName, report.OriginalBytes, report.DeduplicatedBytes, report.By);

    public static Health HealthOf(AppReportOutcome outcome) => outcome switch
    {
        AppReportOutcome.Succeeded => Health.Ok,
        AppReportOutcome.Warnings => Health.Warning,
        AppReportOutcome.Failed => Health.Failed,
        _ => Health.Unknown,
    };
}
