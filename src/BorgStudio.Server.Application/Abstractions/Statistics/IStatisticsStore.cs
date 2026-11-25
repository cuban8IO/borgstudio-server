using BorgStudio.Server.Domain.Statistics;

namespace BorgStudio.Server.Application.Abstractions.Statistics;

/// <summary>
/// What the server measures and what apps report, in the database. It shares the unit of work with
/// the other stores: changes are written with their SaveChanges (or <see cref="SaveChangesAsync"/>).
/// </summary>
public interface IStatisticsStore
{
    void Add(ConnectionRecord record);

    void Add(AppReport report);

    void Add(SystemSample sample);

    /// <param name="repositoryId">Only this repository's; <c>null</c> for all.</param>
    Task<IReadOnlyList<ConnectionRecord>> GetConnectionsAsync(Guid? repositoryId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <param name="repositoryId">Only this repository's; <c>null</c> for all.</param>
    Task<IReadOnlyList<AppReport>> GetReportsAsync(Guid? repositoryId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <param name="subjectId">Only this repository's or datastore's; <c>null</c> for all of the kind.</param>
    Task<IReadOnlyList<StorageSample>> GetStorageSamplesAsync(StorageSubject subject, Guid? subjectId, DateOnly since, CancellationToken cancellationToken);

    /// <summary>Sets the day's value (the last measurement of a day wins).</summary>
    Task SetStorageSampleAsync(
        StorageSubject subject, Guid subjectId, DateOnly day, long usedBytes, long? totalBytes, long? freeBytes, CancellationToken cancellationToken);

    Task<IReadOnlyList<SystemSample>> GetSystemSamplesAsync(SampleResolution resolution, DateTimeOffset since, CancellationToken cancellationToken);

    Task<SystemSample?> GetLatestSystemSampleAsync(SampleResolution resolution, CancellationToken cancellationToken);

    /// <summary>Removes what is older than <see cref="Retention"/> allows.</summary>
    Task PruneAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
