using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Statistics;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Statistics;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Statistics;

/// <summary>Traffic of all connections on one day.</summary>
public sealed record DailyTraffic(DateOnly Day, long BytesReceived, long BytesSent, int Connections);

public sealed record DatastoreRepositoryShare(Guid Id, string FullName, long UsedBytes);

public sealed record DatastoreStatistics(
    Guid Id,
    string Name,
    DatastoreKind Kind,
    string Path,
    StorageSpace? Space,
    IReadOnlyList<DailyPoint> History,
    DateOnly? FullAt,
    IReadOnlyList<DailyTraffic> Traffic,
    IReadOnlyList<DatastoreRepositoryShare> Repositories,
    string? Error);

/// <summary>One datastore over time (administrators); <c>null</c> if it does not exist.</summary>
public sealed record GetDatastoreStatisticsQuery([property: Required] string ActingUserId, Guid DatastoreId) : IQuery<DatastoreStatistics?>;

internal sealed class GetDatastoreStatisticsQueryHandler(StorageContext storage, IStatisticsStore statistics, TimeProvider timeProvider)
    : IQueryHandler<GetDatastoreStatisticsQuery, DatastoreStatistics?>
{
    public async Task<DatastoreStatistics?> HandleAsync(GetDatastoreStatisticsQuery query, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(query.ActingUserId, cancellationToken);
        var datastore = await storage.Store.FindDatastoreAsync(query.DatastoreId, cancellationToken);
        if (datastore is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var history = await GetRepositoryStatisticsQueryHandler.HistoryAsync(statistics, StorageSubject.Datastore, datastore.Id, now, cancellationToken);
        var withSpace = history.Where(p => p.TotalBytes is not null && p.FreeBytes is not null).ToList();
        var fullAt = withSpace.Count == 0
            ? null
            : StorageForecast.FullAt([.. withSpace.Select(p => (p.Day, p.TotalBytes!.Value - p.FreeBytes!.Value))], withSpace[^1].TotalBytes);

        var repositories = (await storage.Store.GetRepositoriesAsync(cancellationToken)).Where(r => r.DatastoreId == datastore.Id).ToList();
        var ids = repositories.Select(r => r.Id).ToHashSet();
        var connections = (await statistics.GetConnectionsAsync(null, now.AddDays(-90), cancellationToken)).Where(c => ids.Contains(c.RepositoryId));
        var traffic = connections
            .GroupBy(c => DateOnly.FromDateTime(c.StartedAt.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DailyTraffic(g.Key, g.Sum(c => c.BytesReceived), g.Sum(c => c.BytesSent), g.Count()))
            .ToList();
        var users = await storage.UsersByIdAsync(cancellationToken);

        return new DatastoreStatistics(
            datastore.Id,
            datastore.Name,
            datastore.Kind,
            datastore.Path,
            storage.SpaceOf(datastore),
            history,
            fullAt,
            traffic,
            [.. repositories.Where(r => !r.IsDeleted).OrderByDescending(r => r.UsedBytes)
                .Select(r => new DatastoreRepositoryShare(r.Id, $"{RepositoryViews.OwnerName(r, users)}/{r.Name}", r.UsedBytes))],
            datastore.LastError);
    }
}

public enum SystemRange
{
    /// <summary>The last 24 hours, per minute.</summary>
    Day,

    /// <summary>The last 30 days, per hour.</summary>
    Month,

    /// <summary>The last year, per day.</summary>
    Year,
}

/// <param name="Supported">Whether the server can read the load here (Linux).</param>
/// <param name="Samples">Oldest first; byte counts are per sample period (minute, hour, day).</param>
public sealed record SystemStatistics(
    bool Supported,
    SystemRange Range,
    TimeSpan Period,
    IReadOnlyList<SystemSample> Samples,
    SystemSample? Latest,
    StorageSpace? DataDisk,
    IReadOnlyList<(string Name, StorageSpace? Space)> Datastores);

/// <summary>The load of the container or computer (administrators).</summary>
public sealed record GetSystemStatisticsQuery([property: Required] string ActingUserId, SystemRange Range) : IQuery<SystemStatistics>;

internal sealed class GetSystemStatisticsQueryHandler(StorageContext storage, IStatisticsStore statistics, TimeProvider timeProvider)
    : IQueryHandler<GetSystemStatisticsQuery, SystemStatistics>
{
    public async Task<SystemStatistics> HandleAsync(GetSystemStatisticsQuery query, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(query.ActingUserId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var (resolution, period, since) = query.Range switch
        {
            SystemRange.Month => (SampleResolution.Hour, TimeSpan.FromHours(1), now.AddDays(-30)),
            SystemRange.Year => (SampleResolution.Day, TimeSpan.FromDays(1), now.AddDays(-365)),
            _ => (SampleResolution.Minute, TimeSpan.FromMinutes(1), now.AddHours(-24)),
        };

        var datastores = await storage.Store.GetDatastoresAsync(cancellationToken);
        return new SystemStatistics(
            OperatingSystem.IsLinux(),
            query.Range,
            period,
            await statistics.GetSystemSamplesAsync(resolution, since, cancellationToken),
            await statistics.GetLatestSystemSampleAsync(SampleResolution.Minute, cancellationToken),
            storage.Folders.DataSpace(),
            [.. datastores.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => (d.Name, storage.SpaceOf(d)))]);
    }
}
