using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Application.Settings;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Application.Users;
using BorgStudio.Server.Domain.Storage;
using BorgStudio.Server.Domain.Users;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Whether apps see who else uses a repository: switchable on the server and per repository.</summary>
public sealed class AppVisibilityTests
{
    [Fact]
    public async Task Participants_CanBeHiddenServerWideAndPerRepository()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        var adminId = await SignInTests.AdminIdAsync(server);
        var aliceId = (await Run.SendAsync(server, new CreateUserCommand(
            adminId, "alice", null, Roles.User, NewUserAccess.TemporaryPassword, 7, "http://localhost/"))).Value!.UserId;
        var id = await Run.CreateRepositoryAsync(server, adminId, "laptop");
        await Run.SendAsync(server, new ShareRepositoryCommand(adminId, id, "alice", RepositoryAccess.AppendOnly));

        async Task<RepositoryStats> StatsAsync() => Assert.Single(await Run.QueryAsync(server, new GetRepositoryStatsQuery(aliceId, "localhost", id)));

        var shown = await StatsAsync();
        Assert.False(shown.ParticipantsHidden);
        Assert.Equal(2, shown.Participants.Count);

        // The owner switches it off for the repository; users with a share cannot.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run.SendAsync(server, new SetRepositoryAppVisibilityCommand(aliceId, id, false)));
        await Run.SendAsync(server, new SetRepositoryAppVisibilityCommand(adminId, id, false));
        var hidden = await StatsAsync();
        Assert.True(hidden.ParticipantsHidden);
        Assert.Empty(hidden.Participants);
        Assert.Null(hidden.LastAccess);
        Assert.Equal(RepositoryAccess.AppendOnly, hidden.Access);

        // Server-wide off wins over the repository's own setting.
        await Run.SendAsync(server, new SetRepositoryAppVisibilityCommand(adminId, id, true));
        Assert.False((await StatsAsync()).ParticipantsHidden);
        await Run.SendAsync(server, new UpdateServerSettingsCommand(adminId, "Test Server", null, true, ShowParticipantsInApps: false));
        Assert.True((await StatsAsync()).ParticipantsHidden);
        Assert.False((await Run.QueryAsync(server, new GetServerSettingsQuery())).ShowParticipantsInApps);

        var german = new Browser(admin.Client, "de-DE");
        Assert.Contains("Apps zeigen, wer ein Repository noch nutzt", (await german.GetAsync("/settings")).Text);
        Assert.Contains("Apps dürfen zeigen, wer dieses Repository nutzt", (await german.GetAsync($"/repositories/{id}")).Text);
    }
}
