using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Storage;
using BorgStudio.Server.Tests.Domain;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Hetzner Storage Boxes and SSH servers as datastores (the SSH side is faked).</summary>
public sealed class ExternalDatastoreTests
{
    [Fact]
    public async Task StorageBox_IsAddedWithHetznersHostKey_AndThePasswordOnce()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);

        Assert.Equal("This is not a valid user name (Storage Box: u123456 or u123456-sub1).",
            Assert.Single((await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.HetznerStorageBox, "bob", null, null))).Errors));

        var prepared = await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.HetznerStorageBox, "U123456-sub1", null, null));
        Assert.True(prepared.Succeeded, string.Join(" ", prepared.Errors));
        var draft = prepared.Value!;
        Assert.Equal("u123456-sub1.your-storagebox.de", draft.Host);
        Assert.Equal(23, draft.Port);
        Assert.Equal(FakeRemoteStorage.HetznerKey, draft.TrustedHostKey);
        Assert.Equal(["borg-1.4", "borg-1.2"], draft.BorgChoices);

        Assert.False((await Run.SendAsync(server, Create(adminId, draft, "box", "borg-9"))).Succeeded);
        Assert.Contains("Permission denied", Assert.Single((await Run.SendAsync(server, Create(adminId, draft, "box", "borg-1.4", "wrong"))).Errors));

        var created = await Run.SendAsync(server, Create(adminId, draft, "box", "borg-1.4", "secret"));
        Assert.True(created.Succeeded, string.Join(" ", created.Errors));
        Assert.Equal("borg 1.4.1", created.Value);
        Assert.Equal((draft.DraftId, DatastoreKind.HetznerStorageBox, "secret"), Assert.Single(server.Remote.InstalledKeys));

        var box = (await Run.QueryAsync(server, new GetDatastoresQuery(adminId))).Single(d => d.Name == "box");
        Assert.Equal(DatastoreKind.HetznerStorageBox, box.Kind);
        Assert.Equal("ssh://u123456-sub1@u123456-sub1.your-storagebox.de:23", box.Path);
        Assert.Equal(server.Remote.Space, box.Space);

        // The same account twice is refused.
        Assert.Contains("already a datastore",
            Assert.Single((await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.HetznerStorageBox, "u123456-sub1", null, null))).Errors));
    }

    [Fact]
    public async Task StorageBox_WithAnUnknownHostKey_IsRefused_SshServersAsk()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        server.Remote.HostKeys.Clear();
        server.Remote.HostKeys.Add(FakeRemoteStorage.OtherKey);

        Assert.Contains("none of those Hetzner publishes",
            Assert.Single((await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.HetznerStorageBox, "u1", null, null))).Errors));

        var ssh = await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.Ssh, "borgstudio", "nas.example.org", 2200));
        Assert.True(ssh.Succeeded);
        Assert.Null(ssh.Value!.TrustedHostKey);
        Assert.Equal(FakeRemoteStorage.OtherKey, Assert.Single(ssh.Value.HostKeys));

        server.Remote.Unreachable = true;
        Assert.Contains("could not be reached",
            Assert.Single((await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.Ssh, "borgstudio", "nas.example.org", 22))).Errors));
        Assert.Equal("Enter a valid host name or address and a port between 1 and 65535.",
            Assert.Single((await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.Ssh, "borgstudio", "bad host", 22))).Errors));
    }

    [Fact]
    public async Task Repositories_OnExternalDatastores_AreFoldersById_AndBorgIsPassedThrough()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var box = await AddBoxAsync(server, adminId);

        var id = await Run.CreateRepositoryAsync(server, adminId, "laptop", box);
        var folder = id.ToString("N");
        Assert.True(server.Remote.Folders[box].ContainsKey(folder));

        // Renaming never moves the folder; the address the clients use stays the same anyway.
        Assert.True((await Run.SendAsync(server, new UpdateRepositoryCommand(adminId, id, "notebook", null))).Succeeded);
        Assert.True(server.Remote.Folders[box].ContainsKey(folder));
        var details = (await Run.QueryAsync(server, new GetRepositoryQuery(adminId, id)))!;
        Assert.Equal($"ssh://u1@u1.your-storagebox.de:23/~/{folder}", details.Location);

        await Run.SendAsync(server, new AddSshKeyCommand(adminId, id, "key", AccessRulesTests.PublicKey));
        var key = Assert.Single(await Run.QueryAsync(server, new GetSshKeysQuery(adminId, id)));
        var plan = await Run.SendAsync(server, new OpenBorgSessionCommand(key.Id, Environment.ProcessId));
        Assert.Null(plan.Refusal);
        Assert.Equal(folder, plan.RepositoryPath);
        Assert.Equal("u1.your-storagebox.de", plan.Remote!.Host);
        Assert.Equal("borg-1.4", plan.Remote.RemoteBorg);
        Assert.Equal("ssh-ed25519 AAAAHETZNER", plan.Remote.HostKey);

        // After the connection the repository is measured over SSH.
        server.Remote.Folders[box][folder] = 5000;
        await Run.SendAsync(server, new CloseBorgSessionCommand(plan.SessionId, 6000, 100, 0));
        Assert.Equal(5000, (await Run.QueryAsync(server, new GetRepositoryQuery(adminId, id)))!.Summary.UsedBytes);

        // The trash keeps the folder; purging deletes it.
        await Run.SendAsync(server, new DeleteRepositoryCommand(adminId, id));
        Assert.True(server.Remote.Folders[box].ContainsKey(folder));
        await Run.SendAsync(server, new PurgeRepositoriesCommand(adminId));
        Assert.False(server.Remote.Folders[box].ContainsKey(folder));
    }

    [Fact]
    public async Task Repositories_MoveToAndFromExternalDatastores()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var box = await AddBoxAsync(server, adminId);
        var local = (await Run.QueryAsync(server, new GetDatastoresQuery(adminId))).Single(d => !d.IsExternal).Id;

        var id = await Run.CreateRepositoryAsync(server, adminId, "laptop", local);
        var localFolder = Path.Combine(server.DataDirectory, "repositories", "admin", "laptop");
        File.WriteAllBytes(Path.Combine(localFolder, "data"), new byte[1234]);

        Assert.True((await Run.SendAsync(server, new MoveRepositoryCommand(adminId, id, box))).Succeeded);
        await Run.SendAsync(server, new RunRepositoryMovesCommand());
        Assert.Equal(1234, server.Remote.Folders[box][id.ToString("N")]);
        Assert.False(Directory.Exists(localFolder));
        Assert.Equal("box", (await Run.QueryAsync(server, new GetRepositoryQuery(adminId, id)))!.Summary.DatastoreName);

        Assert.True((await Run.SendAsync(server, new MoveRepositoryCommand(adminId, id, local))).Succeeded);
        await Run.SendAsync(server, new RunRepositoryMovesCommand());
        Assert.False(server.Remote.Folders[box].ContainsKey(id.ToString("N")));
        Assert.Equal(1234, new FileInfo(Path.Combine(localFolder, "data")).Length);
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "repositories", ".incoming")));
    }

    [Fact]
    public async Task Maintenance_MeasuresExternalDatastores_AndNotesWhenTheyAreUnreachable()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var box = await AddBoxAsync(server, adminId);
        var id = await Run.CreateRepositoryAsync(server, adminId, "laptop", box);
        server.Remote.Folders[box][id.ToString("N")] = 777;
        server.Remote.Space = new StorageSpace(2_000, 500);

        await Run.SendAsync(server, new RunStorageMaintenanceCommand());
        Assert.Equal(777, (await Run.QueryAsync(server, new GetRepositoryQuery(adminId, id)))!.Summary.UsedBytes);
        var datastore = (await Run.QueryAsync(server, new GetDatastoresQuery(adminId))).Single(d => d.Id == box);
        Assert.Equal(new StorageSpace(2_000, 500), datastore.Space);
        Assert.Null(datastore.Error);

        server.Remote.Unreachable = true;
        await Run.SendAsync(server, new RunStorageMaintenanceCommand());
        datastore = (await Run.QueryAsync(server, new GetDatastoresQuery(adminId))).Single(d => d.Id == box);
        Assert.Equal("Connection refused", datastore.Error);
        Assert.Equal(777, (await Run.QueryAsync(server, new GetRepositoryQuery(adminId, id)))!.Summary.UsedBytes);
    }

    internal static async Task<Guid> AddBoxAsync(TestServer server, string adminId)
    {
        var draft = (await Run.SendAsync(server, new PrepareExternalDatastoreCommand(adminId, DatastoreKind.HetznerStorageBox, "u1", null, null))).Value!;
        var created = await Run.SendAsync(server, Create(adminId, draft, "box", "borg-1.4", "secret"));
        Assert.True(created.Succeeded, string.Join(" ", created.Errors));
        return draft.DraftId;
    }

    private static CreateExternalDatastoreCommand Create(string adminId, ExternalDatastoreDraft draft, string name, string borg, string? password = null) =>
        new(adminId, draft.DraftId, draft.Kind, name, draft.UserName, draft.Host, draft.Port, draft.TrustedHostKey!.PublicKey, borg, password);
}
