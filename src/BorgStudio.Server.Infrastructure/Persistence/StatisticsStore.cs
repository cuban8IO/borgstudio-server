using BorgStudio.Server.Application.Abstractions.Statistics;
using BorgStudio.Server.Domain.Statistics;

using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Persistence;

/// <summary>Times are stored as Unix milliseconds (see AppDbContext), so SQLite can compare them in queries.</summary>
internal sealed class StatisticsStore(AppDbContext db) : IStatisticsStore
{
    public void Add(ConnectionRecord record) => db.ConnectionRecords.Add(record);

    public void Add(AppReport report) => db.AppReports.Add(report);

    public void Add(SystemSample sample) => db.SystemSamples.Add(sample);

    public async Task<IReadOnlyList<ConnectionRecord>> GetConnectionsAsync(Guid? repositoryId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.ConnectionRecords.AsNoTracking()
            .Where(c => (repositoryId == null || c.RepositoryId == repositoryId) && c.StartedAt >= since)
            .OrderBy(c => c.StartedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AppReport>> GetReportsAsync(Guid? repositoryId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.AppReports.AsNoTracking()
            .Where(r => (repositoryId == null || r.RepositoryId == repositoryId) && r.StartedAt >= since)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StorageSample>> GetStorageSamplesAsync(
        StorageSubject subject, Guid? subjectId, DateOnly since, CancellationToken cancellationToken) =>
        await db.StorageSamples.AsNoTracking()
            .Where(s => s.Subject == subject && (subjectId == null || s.SubjectId == subjectId) && s.Day >= since)
            .OrderBy(s => s.Day)
            .ToListAsync(cancellationToken);

    public async Task SetStorageSampleAsync(
        StorageSubject subject, Guid subjectId, DateOnly day, long usedBytes, long? totalBytes, long? freeBytes, CancellationToken cancellationToken)
    {
        var existing = db.StorageSamples.Local.FirstOrDefault(s => s.Subject == subject && s.SubjectId == subjectId && s.Day == day)
            ?? await db.StorageSamples.FirstOrDefaultAsync(s => s.Subject == subject && s.SubjectId == subjectId && s.Day == day, cancellationToken);
        if (existing is null)
        {
            db.StorageSamples.Add(StorageSample.Create(subject, subjectId, day, usedBytes, totalBytes, freeBytes));
        }
        else
        {
            existing.Update(usedBytes, totalBytes, freeBytes);
        }
    }

    public async Task<IReadOnlyList<SystemSample>> GetSystemSamplesAsync(SampleResolution resolution, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.SystemSamples.AsNoTracking()
            .Where(s => s.Resolution == resolution && s.At >= since)
            .OrderBy(s => s.At)
            .ToListAsync(cancellationToken);

    public Task<SystemSample?> GetLatestSystemSampleAsync(SampleResolution resolution, CancellationToken cancellationToken) =>
        db.SystemSamples.AsNoTracking()
            .Where(s => s.Resolution == resolution)
            .OrderByDescending(s => s.At)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var minutes = now - Retention.MinuteSamples;
        var hours = now - Retention.HourSamples;
        var days = now - Retention.DaySamples;
        await db.SystemSamples.Where(s => s.Resolution == SampleResolution.Minute && s.At < minutes).ExecuteDeleteAsync(cancellationToken);
        await db.SystemSamples.Where(s => s.Resolution == SampleResolution.Hour && s.At < hours).ExecuteDeleteAsync(cancellationToken);
        await db.SystemSamples.Where(s => s.Resolution == SampleResolution.Day && s.At < days).ExecuteDeleteAsync(cancellationToken);

        var connections = now - Retention.Connections;
        await db.ConnectionRecords.Where(c => c.StartedAt < connections).ExecuteDeleteAsync(cancellationToken);
        var reports = now - Retention.AppReports;
        await db.AppReports.Where(r => r.StartedAt < reports).ExecuteDeleteAsync(cancellationToken);
        var history = DateOnly.FromDateTime((now - Retention.StorageHistory).UtcDateTime);
        await db.StorageSamples.Where(s => s.Day < history).ExecuteDeleteAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
