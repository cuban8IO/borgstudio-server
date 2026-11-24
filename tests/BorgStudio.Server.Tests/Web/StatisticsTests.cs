using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using BorgStudio.Server.Application.Abstractions.Statistics;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Application.Statistics;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Statistics;
using BorgStudio.Server.Tests.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

/// <summary>What the server measures, what apps report, and the pages that show it.</summary>
public sealed class StatisticsTests
{
    [Fact]
    public async Task Connections_AreRecordedWithTrafficAndGrowth()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var id = await Run.CreateRepositoryAsync(server, adminId, "laptop");
        await Run.SendAsync(server, new AddSshKeyCommand(adminId, id, "borgmatic", AccessRulesTests.PublicKey));
        var key = Assert.Single(await Run.QueryAsync(server, new GetSshKeysQuery(adminId, id)));

        var plan = await Run.SendAsync(server, new OpenBorgSessionCommand(key.Id, Environment.ProcessId));
        File.WriteAllBytes(Path.Combine(server.DataDirectory, "repositories", "admin", "laptop", "segment"), new byte[4000]);
        server.Clock.Advance(TimeSpan.FromMinutes(3));
        await Run.SendAsync(server, new CloseBorgSessionCommand(plan.SessionId, 5000, 300, 1));

        var statistics = (await Run.QueryAsync(server, new GetRepositoryStatisticsQuery(adminId, id)))!;
        var connection = Assert.Single(statistics.Connections);
        Assert.Equal(4000, connection.Growth);
        Assert.Equal(5000, connection.BytesReceived);
        Assert.Equal(300, connection.BytesSent);
        Assert.Equal(TimeSpan.FromMinutes(3), connection.Duration);
        Assert.Equal("borgmatic", connection.By);
        Assert.Equal(Health.Warning, connection.Health);

        Assert.Equal(1, statistics.Last30Days.Connections);
        Assert.Equal(100, statistics.Last30Days.SuccessRate);
        Assert.Equal(4000, statistics.Last30Days.Growth);
        Assert.Equal(84, statistics.Calendar.Count);
        Assert.Equal(new DayActivity(DateOnly.FromDateTime(server.Clock.GetUtcNow().UtcDateTime), 1, Health.Warning), statistics.Calendar[^1]);
        Assert.Null(statistics.Reported);
    }

    [Fact]
    public async Task AppReports_GiveArchivesSavingsAndResults()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var id = await Run.CreateRepositoryAsync(server, adminId, "laptop");
        var started = await Run.SendAsync(server, new StartPairingCommand("laptop", "Linux", "0.6.0"));
        await Run.SendAsync(server, new DecidePairingCommand(adminId, started.UserCode, true));
        var token = (await Run.SendAsync(server, new CompletePairingCommand(started.PairingCode))).Token;
        using var app = server.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var now = server.Clock.GetUtcNow();
        using (var backup = await app.PostAsJsonAsync($"/api/v1/repositories/{id}/reports", new
        {
            kind = "backup",
            outcome = "succeeded",
            startedAt = now.AddMinutes(-5),
            durationSeconds = 240,
            archiveName = "laptop-2026-11-25",
            originalSize = 10_000_000_000L,
            compressedSize = 6_000_000_000L,
            deduplicatedSize = 120_000_000L,
            files = 123456,
            archives = 42,
            totalSize = 400_000_000_000L,
            totalCompressedSize = 240_000_000_000L,
            uniqueCompressedSize = 20_000_000_000L,
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, backup.StatusCode);
        }

        using (var check = await app.PostAsJsonAsync($"/api/v1/repositories/{id}/reports", new
        {
            kind = "check", outcome = "failed", startedAt = now.AddMinutes(-1), durationSeconds = 30, message = "segment 12 damaged",
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, check.StatusCode);
        }

        using (var bad = await app.PostAsJsonAsync($"/api/v1/repositories/{id}/reports", new { kind = "dance", outcome = "succeeded", startedAt = now }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        using (var foreign = await app.PostAsJsonAsync($"/api/v1/repositories/{Guid.NewGuid()}/reports", new { kind = "backup", outcome = "succeeded", startedAt = now }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        var reported = (await Run.QueryAsync(server, new GetRepositoryStatisticsQuery(adminId, id)))!.Reported!;
        Assert.Equal(42, reported.Archives);
        Assert.Equal(95, reported.SavingsPercent!.Value, 3);
        Assert.Equal(12, reported.DeduplicationRatio!.Value, 3);
        Assert.Equal(400.0 / 240, reported.CompressionRatio!.Value, 3);
        Assert.Equal(100, reported.SuccessRate30);
        var backupItem = Assert.Single(reported.Backups);
        Assert.Equal("laptop", backupItem.By);
        Assert.Equal(AppReportOutcome.Failed, reported.LastCheck!.Outcome);

        var overview = await Run.QueryAsync(server, new GetOverviewStatisticsQuery(adminId));
        Assert.Equal(95, overview.SavingsPercent!.Value, 3);
        Assert.Equal(100, overview.SuccessRate);
        Assert.Equal(1, overview.RunsCounted);
        Assert.Equal(AppReportOutcome.Succeeded, Assert.Single(overview.Rows).LastBackup);
    }

    [Fact]
    public async Task StorageHistory_GivesAForecast_AndTheOverviewWarns()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var id = (await Run.SendAsync(server, new CreateRepositoryCommand(adminId, "laptop", null, 10_000))).Value;

        // 1000 bytes a day for 10 days: with a quota of 10 000 it is full in about two weeks.
        var today = DateOnly.FromDateTime(server.Clock.GetUtcNow().UtcDateTime);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var statistics = scope.ServiceProvider.GetRequiredService<IStatisticsStore>();
            for (var day = 0; day < 10; day++)
            {
                await statistics.SetStorageSampleAsync(StorageSubject.Repository, id, today.AddDays(day - 9), (day + 1) * 1000L, null, null, CancellationToken.None);
            }

            await statistics.SaveChangesAsync(CancellationToken.None);
        }

        var repository = (await Run.QueryAsync(server, new GetRepositoryStatisticsQuery(adminId, id)))!;
        Assert.Equal(10, repository.History.Count);
        Assert.Equal(today, repository.FullAt);

        File.WriteAllBytes(Path.Combine(server.DataDirectory, "repositories", "admin", "laptop", "data"), new byte[5000]);
        await Run.SendAsync(server, new RunStorageMaintenanceCommand());
        repository = (await Run.QueryAsync(server, new GetRepositoryStatisticsQuery(adminId, id)))!;
        Assert.Equal(5000, repository.History[^1].UsedBytes);
        Assert.InRange(repository.FullAt!.Value.DayNumber - today.DayNumber, 1, 30);

        var overview = await Run.QueryAsync(server, new GetOverviewStatisticsQuery(adminId));
        Assert.Equal("admin/laptop", overview.FullSoonest!.Subject);
        var row = Assert.Single(overview.Rows);
        Assert.Equal(Health.Warning, row.Health);
        Assert.Equal(OverviewReason.FullSoon, row.Reason);

        // The datastore got its daily point too.
        var datastore = (await Run.QueryAsync(server, new GetDatastoresQuery(adminId))).Single();
        var datastoreStatistics = (await Run.QueryAsync(server, new GetDatastoreStatisticsQuery(adminId, datastore.Id)))!;
        Assert.Equal(5000, Assert.Single(datastoreStatistics.History).UsedBytes);
        Assert.NotNull(datastoreStatistics.History[0].TotalBytes);
    }

    [Fact]
    public async Task SystemSamples_AreSummedUpPerHourAndDay()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var start = new DateTimeOffset(2026, 11, 25, 9, 58, 0, TimeSpan.Zero);

        for (var minute = 0; minute < 4; minute++)
        {
            await Run.SendAsync(server, new RecordSystemSampleCommand(
                start.AddMinutes(minute), minute * 10, 1000 + minute, 8000, 600, 60, 6000 * (minute + 1), 60));
        }

        server.Clock.SetUtcNow(start.AddMinutes(4));
        var day = await Run.QueryAsync(server, new GetSystemStatisticsQuery(adminId, SystemRange.Day));
        Assert.Equal(4, day.Samples.Count);
        Assert.Equal(30, day.Latest!.CpuPercent);

        // The hour from 9:00 to 10:00 is complete once a sample of 10:00 or later arrived.
        var month = await Run.QueryAsync(server, new GetSystemStatisticsQuery(adminId, SystemRange.Month));
        var hour = Assert.Single(month.Samples);
        Assert.Equal(start.AddMinutes(-58), hour.At);
        Assert.Equal(5, hour.CpuPercent);
        Assert.Equal(10, hour.CpuPercentPeak);
        Assert.Equal(6000 + 12000, hour.NetworkReceivedBytes);
        Assert.Equal(12000, hour.NetworkReceivedPeak);
        Assert.Equal(1001, hour.MemoryUsedPeak);
    }

    [Fact]
    public async Task Pages_ShowTheStatistics()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        var adminId = await SignInTests.AdminIdAsync(server);
        var id = await Run.CreateRepositoryAsync(server, adminId, "laptop");
        var box = await ExternalDatastoreTests.AddBoxAsync(server, adminId);
        await Run.SendAsync(server, new RecordSystemSampleCommand(server.Clock.GetUtcNow(), 12.5, 1_000_000, 4_000_000, 0, 0, 60_000, 6_000));

        var german = new Browser(admin.Client, "de-DE");
        var home = await german.GetAsync("/");
        Assert.Contains("Gespeichert", home.Text);
        Assert.Contains("admin/laptop", home.Text);
        Assert.Contains("Noch nicht benutzt", home.Text);
        Assert.Contains("Arbeitsspeicher", home.Text);

        var repository = await german.GetAsync($"/repositories/{id}");
        Assert.Contains("Speicher im Zeitverlauf", repository.Text);
        Assert.Contains("Von der BorgStudio-App gemeldet", repository.Text);

        var system = await german.GetAsync("/system");
        Assert.Contains("Datenträger-I/O", system.Text);
        Assert.Contains("Datenspeicher box", system.Text);

        var datastores = await german.GetAsync("/datastores");
        Assert.Contains("Hetzner Storage Box", datastores.Text);
        Assert.Contains("Storage Box hinzufügen", datastores.Text);
        Assert.Contains("Datenverkehr je Tag", (await german.GetAsync($"/datastores/{box}")).Text);
    }
}
