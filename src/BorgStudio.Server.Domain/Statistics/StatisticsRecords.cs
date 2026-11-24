namespace BorgStudio.Server.Domain.Statistics;

/// <summary>How long the server keeps what it measures.</summary>
public static class Retention
{
    public static readonly TimeSpan MinuteSamples = TimeSpan.FromHours(24);

    public static readonly TimeSpan HourSamples = TimeSpan.FromDays(30);

    public static readonly TimeSpan DaySamples = TimeSpan.FromDays(365);

    /// <summary>One point per day for repositories and datastores, like the BorgStudio app.</summary>
    public static readonly TimeSpan StorageHistory = TimeSpan.FromDays(730);

    public static readonly TimeSpan Connections = TimeSpan.FromDays(365);

    public static readonly TimeSpan AppReports = TimeSpan.FromDays(730);
}

/// <summary>One finished borg connection: who, how long, how much went over the wire, how much the repository grew.</summary>
public sealed class ConnectionRecord
{
    private ConnectionRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid RepositoryId { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    /// <summary>The paired app or key name that connected.</summary>
    public string By { get; private set; } = string.Empty;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset EndedAt { get; private set; }

    /// <summary>Bytes the client sent (the backup data, mostly).</summary>
    public long BytesReceived { get; private set; }

    /// <summary>Bytes sent back to the client (e.g. for restores).</summary>
    public long BytesSent { get; private set; }

    /// <summary>borg's exit code; 0 = fine, 1 = warnings, 2 and more = errors; <c>null</c> if unknown.</summary>
    public int? ExitCode { get; private set; }

    public long UsedBefore { get; private set; }

    public long UsedAfter { get; private set; }

    public long Growth => UsedAfter - UsedBefore;

    public TimeSpan Duration => EndedAt - StartedAt;

    public static ConnectionRecord Create(
        Guid repositoryId,
        string userId,
        string by,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        long bytesReceived,
        long bytesSent,
        int? exitCode,
        long usedBefore,
        long usedAfter) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repositoryId,
        UserId = userId,
        By = by.Length > 100 ? by[..100] : by,
        StartedAt = startedAt,
        EndedAt = endedAt,
        BytesReceived = bytesReceived,
        BytesSent = bytesSent,
        ExitCode = exitCode,
        UsedBefore = usedBefore,
        UsedAfter = usedAfter,
    };
}

public enum StorageSubject
{
    Repository = 0,
    Datastore = 1,
}

/// <summary>Space a repository or datastore took on one day (the last measurement of that day).</summary>
public sealed class StorageSample
{
    private StorageSample()
    {
    }

    public long Id { get; private set; }

    public StorageSubject Subject { get; private set; }

    public Guid SubjectId { get; private set; }

    public DateOnly Day { get; private set; }

    public long UsedBytes { get; private set; }

    /// <summary>Datastores: the size of their disk or account.</summary>
    public long? TotalBytes { get; private set; }

    public long? FreeBytes { get; private set; }

    public static StorageSample Create(StorageSubject subject, Guid subjectId, DateOnly day, long usedBytes, long? totalBytes, long? freeBytes) => new()
    {
        Subject = subject,
        SubjectId = subjectId,
        Day = day,
        UsedBytes = usedBytes,
        TotalBytes = totalBytes,
        FreeBytes = freeBytes,
    };

    public void Update(long usedBytes, long? totalBytes, long? freeBytes)
    {
        UsedBytes = usedBytes;
        TotalBytes = totalBytes;
        FreeBytes = freeBytes;
    }
}

public enum SampleResolution
{
    Minute = 0,
    Hour = 1,
    Day = 2,
}

/// <summary>
/// The load of the container or computer over one minute, hour or day: averages and peaks. Byte
/// counts are totals for the period; the peaks are the highest per-minute values in it.
/// </summary>
public sealed class SystemSample
{
    private SystemSample()
    {
    }

    public long Id { get; private set; }

    public SampleResolution Resolution { get; private set; }

    /// <summary>Start of the period.</summary>
    public DateTimeOffset At { get; private set; }

    public double CpuPercent { get; private set; }

    public double CpuPercentPeak { get; private set; }

    public long MemoryUsedBytes { get; private set; }

    public long MemoryUsedPeak { get; private set; }

    public long MemoryTotalBytes { get; private set; }

    public long DiskReadBytes { get; private set; }

    public long DiskWrittenBytes { get; private set; }

    public long NetworkReceivedBytes { get; private set; }

    public long NetworkSentBytes { get; private set; }

    public long DiskReadPeak { get; private set; }

    public long DiskWrittenPeak { get; private set; }

    public long NetworkReceivedPeak { get; private set; }

    public long NetworkSentPeak { get; private set; }

    public static SystemSample Minute(
        DateTimeOffset at,
        double cpuPercent,
        long memoryUsed,
        long memoryTotal,
        long diskRead,
        long diskWritten,
        long networkReceived,
        long networkSent) => new()
    {
        Resolution = SampleResolution.Minute,
        At = at,
        CpuPercent = cpuPercent,
        CpuPercentPeak = cpuPercent,
        MemoryUsedBytes = memoryUsed,
        MemoryUsedPeak = memoryUsed,
        MemoryTotalBytes = memoryTotal,
        DiskReadBytes = diskRead,
        DiskWrittenBytes = diskWritten,
        NetworkReceivedBytes = networkReceived,
        NetworkSentBytes = networkSent,
        DiskReadPeak = diskRead,
        DiskWrittenPeak = diskWritten,
        NetworkReceivedPeak = networkReceived,
        NetworkSentPeak = networkSent,
    };

    /// <summary>Sums finer samples up into one hour or day: averages of averages, maxima of peaks, sums of bytes.</summary>
    public static SystemSample Aggregate(SampleResolution resolution, DateTimeOffset at, IReadOnlyCollection<SystemSample> parts)
    {
        if (parts.Count == 0)
        {
            throw new ArgumentException("Nothing to aggregate.", nameof(parts));
        }

        return new SystemSample
        {
            Resolution = resolution,
            At = at,
            CpuPercent = parts.Average(p => p.CpuPercent),
            CpuPercentPeak = parts.Max(p => p.CpuPercentPeak),
            MemoryUsedBytes = (long)parts.Average(p => p.MemoryUsedBytes),
            MemoryUsedPeak = parts.Max(p => p.MemoryUsedPeak),
            MemoryTotalBytes = parts.Max(p => p.MemoryTotalBytes),
            DiskReadBytes = parts.Sum(p => p.DiskReadBytes),
            DiskWrittenBytes = parts.Sum(p => p.DiskWrittenBytes),
            NetworkReceivedBytes = parts.Sum(p => p.NetworkReceivedBytes),
            NetworkSentBytes = parts.Sum(p => p.NetworkSentBytes),
            DiskReadPeak = parts.Max(p => p.DiskReadPeak),
            DiskWrittenPeak = parts.Max(p => p.DiskWrittenPeak),
            NetworkReceivedPeak = parts.Max(p => p.NetworkReceivedPeak),
            NetworkSentPeak = parts.Max(p => p.NetworkSentPeak),
        };
    }
}

public enum AppReportKind
{
    Backup = 0,
    Check = 1,
    Prune = 2,
    Compact = 3,
}

public enum AppReportOutcome
{
    Succeeded = 0,
    Warnings = 1,
    Failed = 2,
    Cancelled = 3,
    Skipped = 4,
}

/// <summary>
/// What a BorgStudio app reported after a run: the server cannot read the encrypted repository,
/// so archive counts, savings and results come from the app.
/// </summary>
public sealed class AppReport
{
    private AppReport()
    {
    }

    public Guid Id { get; private set; }

    public Guid RepositoryId { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    /// <summary>The paired app that reported, if it was one.</summary>
    public string? By { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public AppReportKind Kind { get; private set; }

    public AppReportOutcome Outcome { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public double DurationSeconds { get; private set; }

    public string? ArchiveName { get; private set; }

    /// <summary>This run's archive: size of the files, compressed, and what was new (deduplicated).</summary>
    public long? OriginalBytes { get; private set; }

    public long? CompressedBytes { get; private set; }

    public long? DeduplicatedBytes { get; private set; }

    public long? Files { get; private set; }

    /// <summary>The whole repository after the run (borg info): archives and sizes.</summary>
    public int? Archives { get; private set; }

    public long? TotalOriginalBytes { get; private set; }

    public long? TotalCompressedBytes { get; private set; }

    public long? UniqueCompressedBytes { get; private set; }

    public string? Message { get; private set; }

    public static AppReport Create(
        Guid repositoryId,
        string userId,
        string? by,
        DateTimeOffset receivedAt,
        AppReportKind kind,
        AppReportOutcome outcome,
        DateTimeOffset startedAt,
        double durationSeconds,
        string? archiveName,
        long? originalBytes,
        long? compressedBytes,
        long? deduplicatedBytes,
        long? files,
        int? archives,
        long? totalOriginalBytes,
        long? totalCompressedBytes,
        long? uniqueCompressedBytes,
        string? message) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repositoryId,
        UserId = userId,
        By = Cut(by, 100),
        ReceivedAt = receivedAt,
        Kind = kind,
        Outcome = outcome,
        StartedAt = startedAt,
        DurationSeconds = Math.Max(0, durationSeconds),
        ArchiveName = Cut(archiveName, 200),
        OriginalBytes = originalBytes,
        CompressedBytes = compressedBytes,
        DeduplicatedBytes = deduplicatedBytes,
        Files = files,
        Archives = archives,
        TotalOriginalBytes = totalOriginalBytes,
        TotalCompressedBytes = totalCompressedBytes,
        UniqueCompressedBytes = uniqueCompressedBytes,
        Message = Cut(message, 2000),
    };

    private static string? Cut(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
}
