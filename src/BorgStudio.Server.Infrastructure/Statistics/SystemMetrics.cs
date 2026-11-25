using System.Globalization;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Statistics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Infrastructure.Statistics;

/// <summary>
/// Reads the load of the container or computer the server runs in (Linux). Where the server has its
/// own control group (LXC, Docker) the counters come from it, so they show the container; otherwise
/// from /proc for the whole computer. The network counters come from the server's network namespace.
/// </summary>
public sealed partial class SystemMetrics
{
    private readonly string root;
    private Counters? previous;

    public SystemMetrics()
        : this("/")
    {
    }

    /// <summary>For tests: <paramref name="root"/> stands in for "/".</summary>
    public SystemMetrics(string root)
    {
        this.root = root;
    }

    public static bool Supported => OperatingSystem.IsLinux();

    /// <summary>
    /// Takes a reading and returns the load since the previous one; <c>null</c> for the first reading
    /// (there is nothing to compare it with yet) or when nothing can be read.
    /// </summary>
    public MetricsReading? Read(DateTimeOffset now)
    {
        var current = ReadCounters(now);
        if (current is null)
        {
            return null;
        }

        var last = previous;
        previous = current;
        if (last is null || current.At <= last.At)
        {
            return null;
        }

        var seconds = (current.At - last.At).TotalSeconds;
        double cpu;
        if (current.CpuUsageMicroseconds is { } usage && last.CpuUsageMicroseconds is { } lastUsage)
        {
            cpu = (usage - lastUsage) / (seconds * 1_000_000 * current.CpuCount) * 100;
        }
        else
        {
            var total = current.CpuTotal - last.CpuTotal;
            cpu = total <= 0 ? 0 : (double)(total - (current.CpuIdle - last.CpuIdle)) / total * 100;
        }

        return new MetricsReading(
            Math.Clamp(cpu, 0, 100),
            current.MemoryUsed,
            current.MemoryTotal,
            Math.Max(0, current.DiskRead - last.DiskRead),
            Math.Max(0, current.DiskWritten - last.DiskWritten),
            Math.Max(0, current.NetworkReceived - last.NetworkReceived),
            Math.Max(0, current.NetworkSent - last.NetworkSent));
    }

    private Counters? ReadCounters(DateTimeOffset now)
    {
        try
        {
            var cgroup = Path.Combine(root, "sys", "fs", "cgroup");
            var cpuStat = File.Exists(Path.Combine(cgroup, "cpu.stat")) ? File.ReadAllText(Path.Combine(cgroup, "cpu.stat")) : null;
            var procStat = File.ReadAllText(Path.Combine(root, "proc", "stat"));
            var (total, idle) = ParseProcStat(procStat);

            long memoryUsed, memoryTotal;
            var meminfo = ParseMeminfo(File.ReadAllText(Path.Combine(root, "proc", "meminfo")));
            if (File.Exists(Path.Combine(cgroup, "memory.current")))
            {
                memoryUsed = ParseLong(File.ReadAllText(Path.Combine(cgroup, "memory.current")));
                var max = File.Exists(Path.Combine(cgroup, "memory.max")) ? File.ReadAllText(Path.Combine(cgroup, "memory.max")).Trim() : "max";
                memoryTotal = max == "max" ? meminfo.Total : Math.Min(ParseLong(max), meminfo.Total);
            }
            else
            {
                memoryUsed = meminfo.Total - meminfo.Available;
                memoryTotal = meminfo.Total;
            }

            var (read, written) = File.Exists(Path.Combine(cgroup, "io.stat"))
                ? ParseIoStat(File.ReadAllText(Path.Combine(cgroup, "io.stat")))
                : ParseDiskstats(File.ReadAllText(Path.Combine(root, "proc", "diskstats")));
            var (received, sent) = ParseNetDev(File.ReadAllText(Path.Combine(root, "proc", "net", "dev")));

            return new Counters(
                now,
                total,
                idle,
                cpuStat is null ? null : ParseCpuUsage(cpuStat),
                CpuCount(cgroup),
                memoryUsed,
                memoryTotal,
                read,
                written,
                received,
                sent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static int CpuCount(string cgroup)
    {
        var limit = Path.Combine(cgroup, "cpu.max");
        if (File.Exists(limit))
        {
            var parts = File.ReadAllText(limit).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] != "max" && long.TryParse(parts[0], out var quota) && long.TryParse(parts[1], out var period) && period > 0)
            {
                return Math.Max(1, (int)Math.Ceiling((double)quota / period));
            }
        }

        return Environment.ProcessorCount;
    }

    /// <summary>"cpu  user nice system idle iowait irq softirq steal …": all ticks, and idle plus iowait.</summary>
    internal static (long Total, long Idle) ParseProcStat(string text)
    {
        var line = text.Split('\n').First(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ParseLong).ToArray();
        var total = values.Take(8).Sum();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (total, idle);
    }

    /// <summary>cgroup v2 cpu.stat: "usage_usec 12345".</summary>
    internal static long? ParseCpuUsage(string text) =>
        text.Split('\n').Select(l => l.Split(' ')).Where(p => p.Length == 2 && p[0] == "usage_usec").Select(p => (long?)ParseLong(p[1])).FirstOrDefault();

    /// <summary>/proc/meminfo in kB: MemTotal and MemAvailable.</summary>
    internal static (long Total, long Available) ParseMeminfo(string text)
    {
        long Value(string name) =>
            text.Split('\n').Where(l => l.StartsWith(name + ":", StringComparison.Ordinal))
                .Select(l => ParseLong(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024).FirstOrDefault();
        return (Value("MemTotal"), Value("MemAvailable"));
    }

    /// <summary>cgroup v2 io.stat: "8:0 rbytes=… wbytes=… rios=…", one line per device.</summary>
    internal static (long Read, long Written) ParseIoStat(string text)
    {
        long read = 0, written = 0;
        foreach (var field in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("rbytes=", StringComparison.Ordinal))
            {
                read += ParseLong(field[7..]);
            }
            else if (field.StartsWith("wbytes=", StringComparison.Ordinal))
            {
                written += ParseLong(field[7..]);
            }
        }

        return (read, written);
    }

    /// <summary>
    /// /proc/diskstats: sectors (512 bytes) read and written, for whole disks only – partitions,
    /// loop and RAM devices would count the same bytes twice or not at all.
    /// </summary>
    internal static (long Read, long Written) ParseDiskstats(string text)
    {
        long read = 0, written = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10)
            {
                continue;
            }

            if (!WholeDisk().IsMatch(parts[2]))
            {
                continue;
            }

            read += ParseLong(parts[5]) * 512;
            written += ParseLong(parts[9]) * 512;
        }

        return (read, written);
    }

    /// <summary>/proc/net/dev: bytes received and sent over every interface but loopback.</summary>
    internal static (long Received, long Sent) ParseNetDev(string text)
    {
        long received = 0, sent = 0;
        foreach (var line in text.Split('\n').Skip(2))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || line[..colon].Trim() == "lo")
            {
                continue;
            }

            var values = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 9)
            {
                received += ParseLong(values[0]);
                sent += ParseLong(values[8]);
            }
        }

        return (received, sent);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^((sd|vd|xvd|hd)[a-z]+|nvme[0-9]+n[0-9]+|mmcblk[0-9]+)$")]
    private static partial System.Text.RegularExpressions.Regex WholeDisk();

    private static long ParseLong(string text) => long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private sealed record Counters(
        DateTimeOffset At,
        long CpuTotal,
        long CpuIdle,
        long? CpuUsageMicroseconds,
        int CpuCount,
        long MemoryUsed,
        long MemoryTotal,
        long DiskRead,
        long DiskWritten,
        long NetworkReceived,
        long NetworkSent);
}

/// <summary>The load since the previous reading: CPU in percent, memory now, bytes moved in the meantime.</summary>
public sealed record MetricsReading(
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskReadBytes,
    long DiskWrittenBytes,
    long NetworkReceivedBytes,
    long NetworkSentBytes);

/// <summary>Records the system's load once a minute (Linux only).</summary>
internal sealed class SystemMetricsWorker(
    SystemMetrics metrics,
    IDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<SystemMetricsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!SystemMetrics.Supported)
        {
            return;
        }

        try
        {
            metrics.Read(timeProvider.GetUtcNow());
            using var timer = new PeriodicTimer(Interval, timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = timeProvider.GetUtcNow();
                if (metrics.Read(now) is not { } reading)
                {
                    continue;
                }

                try
                {
                    await dispatcher.SendAsync(
                        new RecordSystemSampleCommand(
                            now - Interval,
                            reading.CpuPercent,
                            reading.MemoryUsedBytes,
                            reading.MemoryTotalBytes,
                            reading.DiskReadBytes,
                            reading.DiskWrittenBytes,
                            reading.NetworkReceivedBytes,
                            reading.NetworkSentBytes),
                        stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "The system's load could not be recorded.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server stops.
        }
    }
}
