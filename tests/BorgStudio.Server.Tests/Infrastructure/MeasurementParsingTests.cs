using BorgStudio.Server.Application.Statistics;
using BorgStudio.Server.Infrastructure.Ssh;
using BorgStudio.Server.Infrastructure.Statistics;

namespace BorgStudio.Server.Tests.Infrastructure;

public sealed class MeasurementParsingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "borgstudio-metrics-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProcStat_CountsIdleAndIowaitAsIdle()
    {
        var (total, idle) = SystemMetrics.ParseProcStat("cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 1 2 3 4\n");
        Assert.Equal(1000, total);
        Assert.Equal(850, idle);
    }

    [Fact]
    public void Diskstats_CountWholeDisksOnly()
    {
        const string text = """
               8       0 sda 100 0 2000 0 50 0 1000 0 0 0 0
               8       1 sda1 100 0 2000 0 50 0 1000 0 0 0 0
             259       0 nvme0n1 10 0 100 0 5 0 50 0 0 0 0
             259       1 nvme0n1p1 10 0 100 0 5 0 50 0 0 0 0
               7       0 loop0 99 0 9999 0 0 0 0 0 0 0 0
             253       0 dm-0 1 0 500 0 1 0 500 0 0 0 0
            """;
        Assert.Equal(((2000 + 100) * 512L, (1000 + 50) * 512L), SystemMetrics.ParseDiskstats(text));
    }

    [Fact]
    public void NetDev_LeavesOutLoopback()
    {
        const string text = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo: 999999 10 0 0 0 0 0 0 999999 10 0 0 0 0 0 0
              eth0: 1000 10 0 0 0 0 0 0 2000 20 0 0 0 0 0 0
              eth1: 500 1 0 0 0 0 0 0 50 1 0 0 0 0 0 0
            """;
        Assert.Equal((1500L, 2050L), SystemMetrics.ParseNetDev(text));
    }

    [Fact]
    public void CgroupFiles()
    {
        Assert.Equal((300L, 70L), SystemMetrics.ParseIoStat("8:0 rbytes=100 wbytes=20 rios=1\n8:16 rbytes=200 wbytes=50 dbytes=0\n"));
        Assert.Equal(123456L, SystemMetrics.ParseCpuUsage("usage_usec 123456\nuser_usec 100\n"));
        Assert.Equal((8192L * 1024, 2048L * 1024), SystemMetrics.ParseMeminfo("MemTotal:  8192 kB\nMemFree: 100 kB\nMemAvailable: 2048 kB\n"));
    }

    [Fact]
    public void Readings_AreDifferencesBetweenTwoReads()
    {
        Write("proc/stat", "cpu  100 0 0 900 0 0 0 0\n");
        Write("proc/meminfo", "MemTotal: 1000 kB\nMemAvailable: 400 kB\n");
        Write("proc/diskstats", "   8 0 sda 1 0 10 0 1 0 20 0 0 0 0\n");
        Write("proc/net/dev", "h\nh\n  eth0: 1000 0 0 0 0 0 0 0 100 0 0 0 0 0 0 0\n");
        var metrics = new SystemMetrics(root);
        var start = DateTimeOffset.UtcNow;
        Assert.Null(metrics.Read(start));

        Write("proc/stat", "cpu  150 0 50 1000 0 0 0 0\n");
        Write("proc/diskstats", "   8 0 sda 1 0 30 0 1 0 60 0 0 0 0\n");
        Write("proc/net/dev", "h\nh\n  eth0: 4000 0 0 0 0 0 0 0 600 0 0 0 0 0 0 0\n");
        var reading = metrics.Read(start.AddMinutes(1))!;

        Assert.Equal(50, reading.CpuPercent, 3);
        Assert.Equal(600 * 1024, reading.MemoryUsedBytes);
        Assert.Equal(1000 * 1024, reading.MemoryTotalBytes);
        Assert.Equal(20 * 512, reading.DiskReadBytes);
        Assert.Equal(40 * 512, reading.DiskWrittenBytes);
        Assert.Equal(3000, reading.NetworkReceivedBytes);
        Assert.Equal(500, reading.NetworkSentBytes);
    }

    [Fact]
    public void RemoteOutput_DuAndDf()
    {
        Assert.Equal(
            new Dictionary<string, long> { ["abc"] = 12 * 1024, ["def"] = 0 },
            RemoteStorage.ParseDu("12\tabc\n0\tdef/\ndu: cannot access 'x': No such file\n"));
        var space = RemoteStorage.ParseDf("Filesystem     1K-blocks      Used Available Use% Mounted on\n/dev/sdb1     1000000000 400000000 600000000  40% /home\n");
        Assert.Equal(1000000000L * 1024, space!.TotalBytes);
        Assert.Equal(600000000L * 1024, space.FreeBytes);
        Assert.Null(RemoteStorage.ParseDf("nothing"));
    }

    [Fact]
    public void Forecast_IsAStraightLineThroughTheLast90Days()
    {
        var today = new DateOnly(2026, 11, 25);
        var growing = Enumerable.Range(0, 30).Select(i => (today.AddDays(i - 29), 1000L + i * 100)).ToList();

        Assert.Equal(today.AddDays(61), StorageForecast.FullAt(growing, 10_000));
        Assert.Null(StorageForecast.FullAt(growing, null));
        Assert.Null(StorageForecast.FullAt([.. growing.Select(p => (p.Item1, 5000L))], 10_000));
        Assert.Null(StorageForecast.FullAt(growing, long.MaxValue / 2));
        Assert.Equal(today, StorageForecast.FullAt(growing, 3000));
        Assert.Null(StorageForecast.FullAt(growing.Take(2).ToList(), 10_000));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
