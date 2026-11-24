using System.Globalization;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Application.Users;
using BorgStudio.Server.Domain.Storage;
using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Web.Localization;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Datastores, repositories, sharing, quotas, the trash and moves, through the real server.</summary>
public sealed class StorageTests
{
    [Fact]
    public async Task NewServer_HasADatastoreInItsDataFolder()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);

        var datastore = Assert.Single(await QueryAsync(server, new GetDatastoresQuery(adminId)));

        Assert.Equal(Datastore.DefaultName, datastore.Name);
        Assert.Equal(Path.Combine(server.DataDirectory, "repositories"), datastore.Path);
        Assert.Null(datastore.Problem);
        Assert.True(Directory.Exists(datastore.Path));
    }

    [Fact]
    public async Task Repositories_AreFoldersNamedAfterOwnerAndName()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);

        var id = await CreateAsync(server, adminId, "laptop");
        Assert.True(Directory.Exists(RepositoryFolder(server, "admin", "laptop")));

        var duplicate = await SendAsync(server, new CreateRepositoryCommand(adminId, "LAPTOP", null, null));
        Assert.Equal("There is already a repository called LAPTOP.", Assert.Single(duplicate.Errors));
        var invalid = await SendAsync(server, new CreateRepositoryCommand(adminId, "../etc", null, null));
        Assert.False(invalid.Succeeded);

        Assert.True((await SendAsync(server, new UpdateRepositoryCommand(adminId, id, "notebook", 10 * ByteSize.BytesPerGigabyte))).Succeeded);
        Assert.False(Directory.Exists(RepositoryFolder(server, "admin", "laptop")));
        Assert.True(Directory.Exists(RepositoryFolder(server, "admin", "notebook")));

        var details = (await QueryAsync(server, new GetRepositoryQuery(adminId, id)))!;
        Assert.Equal("admin/notebook", details.Summary.FullName);
        Assert.Equal(10 * ByteSize.BytesPerGigabyte, details.Summary.QuotaBytes);
        Assert.Equal(RepositoryFolder(server, "admin", "notebook"), details.Location);
    }

    [Fact]
    public async Task Sharing_GivesOthersAccess_UntilItIsTakenBack()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var (aliceId, _) = await CreateUserAsync(server, adminId, "alice");
        var (bobId, _) = await CreateUserAsync(server, adminId, "bob");

        var id = await CreateAsync(server, aliceId, "laptop");
        Assert.Empty((await QueryAsync(server, new GetRepositoriesQuery(bobId))).Repositories);
        Assert.Null(await QueryAsync(server, new GetRepositoryQuery(bobId, id)));
        Assert.Equal(["admin", "bob"], await QueryAsync(server, new GetShareCandidatesQuery(aliceId, id)));

        Assert.True((await SendAsync(server, new ShareRepositoryCommand(aliceId, id, "bob", RepositoryAccess.AppendOnly))).Succeeded);
        var shared = Assert.Single((await QueryAsync(server, new GetRepositoriesQuery(bobId))).Repositories);
        Assert.Equal("alice/laptop", shared.FullName);
        Assert.Equal(RepositoryAccess.AppendOnly, shared.Access);
        Assert.False(shared.CanManage);

        // Users with a share cannot pass it on; the owner cannot share with themselves.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            SendAsync(server, new ShareRepositoryCommand(bobId, id, "admin", RepositoryAccess.Full)));
        Assert.Equal("The owner always has full access.",
            Assert.Single((await SendAsync(server, new ShareRepositoryCommand(aliceId, id, "alice", RepositoryAccess.Full))).Errors));

        // Administrators see every repository and can manage it, without access to the data.
        var forAdmin = Assert.Single((await QueryAsync(server, new GetRepositoriesQuery(adminId))).Repositories);
        Assert.True(forAdmin.CanManage);
        Assert.Null(forAdmin.Access);

        // Bob leaves.
        await SendAsync(server, new RemoveShareCommand(bobId, id, bobId));
        Assert.Empty((await QueryAsync(server, new GetRepositoriesQuery(bobId))).Repositories);
    }

    [Fact]
    public async Task Owners_AreChangedByAdministrators_AndFolderFollows()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var (aliceId, _) = await CreateUserAsync(server, adminId, "alice");
        var id = await CreateAsync(server, aliceId, "laptop");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            SendAsync(server, new SetRepositoryOwnerCommand(aliceId, id, "admin")));

        Assert.True((await SendAsync(server, new SetRepositoryOwnerCommand(adminId, id, "admin"))).Succeeded);
        Assert.True(Directory.Exists(RepositoryFolder(server, "admin", "laptop")));
        Assert.False(Directory.Exists(Path.Combine(DatastorePath(server), "alice")));

        // Renaming a user moves the folders of their repositories along.
        Assert.True((await SendAsync(server, new UpdateUserCommand(adminId, adminId, "root", null, Roles.Administrator))).Succeeded);
        Assert.True(Directory.Exists(RepositoryFolder(server, "root", "laptop")));
        Assert.Equal("root/laptop", (await QueryAsync(server, new GetRepositoryQuery(adminId, id)))!.Summary.FullName);
    }

    [Fact]
    public async Task Trash_KeepsRepositoriesForSevenDays()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var (aliceId, _) = await CreateUserAsync(server, adminId, "alice");
        var id = await CreateAsync(server, aliceId, "laptop");
        File.WriteAllText(Path.Combine(RepositoryFolder(server, "alice", "laptop"), "config"), "[repository]");

        Assert.True((await SendAsync(server, new DeleteRepositoryCommand(aliceId, id))).Succeeded);
        Assert.Empty((await QueryAsync(server, new GetRepositoriesQuery(aliceId))).Repositories);
        Assert.False(Directory.Exists(RepositoryFolder(server, "alice", "laptop")));
        var item = Assert.Single(await QueryAsync(server, new GetTrashQuery(aliceId)));
        Assert.Equal(item.DeletedAt.AddDays(7), item.PurgeAt);

        // The name is free again while the old one waits in the trash; restoring then needs another name.
        var newer = await CreateAsync(server, aliceId, "laptop");
        Assert.False((await SendAsync(server, new RestoreRepositoryCommand(aliceId, id))).Succeeded);
        Assert.True((await SendAsync(server, new RestoreRepositoryCommand(aliceId, id, Name: "laptop-old"))).Succeeded);
        Assert.Equal("[repository]", File.ReadAllText(Path.Combine(RepositoryFolder(server, "alice", "laptop-old"), "config")));

        // After 7 days the maintenance removes it for good.
        Assert.True((await SendAsync(server, new DeleteRepositoryCommand(aliceId, newer))).Succeeded);
        server.Clock.Advance(TimeSpan.FromDays(6));
        await SendAsync(server, new RunStorageMaintenanceCommand());
        Assert.Single(await QueryAsync(server, new GetTrashQuery(adminId)));
        server.Clock.Advance(TimeSpan.FromDays(1));
        await SendAsync(server, new RunStorageMaintenanceCommand());
        Assert.Empty(await QueryAsync(server, new GetTrashQuery(adminId)));
        Assert.False(Directory.Exists(Path.Combine(DatastorePath(server), RepositoryNames.TrashFolder)));
    }

    [Fact]
    public async Task DeletedUsers_LeaveTheirRepositoriesInTheTrash_ForAnAdministratorToHandOn()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var (aliceId, _) = await CreateUserAsync(server, adminId, "alice");
        var (bobId, _) = await CreateUserAsync(server, adminId, "bob");
        var id = await CreateAsync(server, aliceId, "laptop");
        await SendAsync(server, new ShareRepositoryCommand(aliceId, id, "bob", RepositoryAccess.Full));

        Assert.True((await SendAsync(server, new DeleteUserCommand(adminId, aliceId))).Succeeded);

        Assert.Empty((await QueryAsync(server, new GetRepositoriesQuery(bobId))).Repositories);
        var item = Assert.Single(await QueryAsync(server, new GetTrashQuery(adminId)));
        Assert.Equal("alice", item.OwnerName);
        Assert.False(item.OwnerExists);

        Assert.Equal("The owner was deleted: an administrator has to choose a new one.",
            Assert.Single((await SendAsync(server, new RestoreRepositoryCommand(adminId, id))).Errors));
        Assert.True((await SendAsync(server, new RestoreRepositoryCommand(adminId, id, "bob"))).Succeeded);
        var restored = Assert.Single((await QueryAsync(server, new GetRepositoriesQuery(bobId))).Repositories);
        Assert.True(restored.IsOwner);
        Assert.Equal(0, restored.ShareCount);
        Assert.True(Directory.Exists(RepositoryFolder(server, "bob", "laptop")));
    }

    [Fact]
    public async Task Quotas_AndTheRightToCreate()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var (aliceId, _) = await CreateUserAsync(server, adminId, "alice");

        await SendAsync(server, new SetUserStorageCommand(adminId, aliceId, MayCreateRepositories: false, QuotaBytes: null));
        Assert.Equal("Your account may not create repositories.",
            Assert.Single((await SendAsync(server, new CreateRepositoryCommand(aliceId, "laptop", null, null))).Errors));

        await SendAsync(server, new SetUserStorageCommand(adminId, aliceId, MayCreateRepositories: true, QuotaBytes: 1000));
        var id = await CreateAsync(server, aliceId, "laptop");
        File.WriteAllBytes(Path.Combine(RepositoryFolder(server, "alice", "laptop"), "segment"), new byte[1500]);
        await SendAsync(server, new MeasureRepositoryCommand(aliceId, id));

        var view = await QueryAsync(server, new GetRepositoriesQuery(aliceId));
        Assert.Equal(1500, view.Allowance.UsedBytes);
        Assert.True(view.Allowance.Exceeded);
        Assert.Equal("Your storage quota is used up.",
            Assert.Single((await SendAsync(server, new CreateRepositoryCommand(aliceId, "desktop", null, null))).Errors));

        var storage = await QueryAsync(server, new GetUserStorageQuery(adminId));
        Assert.Equal(new StorageAllowance(true, 1500, 1000), storage[aliceId]);
        Assert.Equal(new StorageAllowance(true, 0, null), storage[adminId]);

        Assert.False((await SendAsync(server, new UpdateRepositoryCommand(aliceId, id, "laptop", 0))).Succeeded);
    }

    [Fact]
    public async Task Datastores_AreCheckedAndRepositoriesMoveBetweenThem()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var (aliceId, _) = await CreateUserAsync(server, adminId, "alice");
        var second = Path.Combine(Path.GetTempPath(), "borgstudio-server-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SendAsync(server, new CreateDatastoreCommand(aliceId, "usb", second)));
            Assert.Equal("The folder does not exist (is the disk mounted?).",
                Assert.Single((await SendAsync(server, new CreateDatastoreCommand(adminId, "usb", second))).Errors));
            Assert.Equal("Enter the full path of the folder, e.g. /mnt/backup.",
                Assert.Single((await SendAsync(server, new CreateDatastoreCommand(adminId, "usb", "relative"))).Errors));
            Assert.Equal("The folder overlaps with the datastore default.",
                Assert.Single((await SendAsync(server, new CreateDatastoreCommand(adminId, "usb", Path.Combine(DatastorePath(server), "x")))).Errors));

            Directory.CreateDirectory(second);
            Assert.True((await SendAsync(server, new CreateDatastoreCommand(adminId, "usb", second))).Succeeded);
            Assert.False((await SendAsync(server, new CreateDatastoreCommand(adminId, "USB", second))).Succeeded);
            var usb = (await QueryAsync(server, new GetDatastoresQuery(adminId))).Single(d => d.Name == "usb");

            var id = await CreateAsync(server, aliceId, "laptop");
            File.WriteAllText(Path.Combine(RepositoryFolder(server, "alice", "laptop"), "config"), "[repository]");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SendAsync(server, new MoveRepositoryCommand(aliceId, id, usb.Id)));
            Assert.True((await SendAsync(server, new MoveRepositoryCommand(adminId, id, usb.Id))).Succeeded);
            Assert.True((await QueryAsync(server, new GetRepositoryQuery(aliceId, id)))!.Summary.IsMoving);
            Assert.Equal("The repository is being moved; try again when it is done.",
                Assert.Single((await SendAsync(server, new DeleteRepositoryCommand(aliceId, id))).Errors));

            await SendAsync(server, new RunRepositoryMovesCommand());

            var moved = (await QueryAsync(server, new GetRepositoryQuery(aliceId, id)))!;
            Assert.False(moved.Summary.IsMoving);
            Assert.Equal("usb", moved.Summary.DatastoreName);
            Assert.Equal("[repository]", File.ReadAllText(Path.Combine(second, "alice", "laptop", "config")));
            Assert.False(Directory.Exists(RepositoryFolder(server, "alice", "laptop")));

            // A datastore with repositories stays; read-only ones take no new repositories.
            Assert.Equal("The datastore still holds repositories (also in the trash). Move or delete them first.",
                Assert.Single((await SendAsync(server, new DeleteDatastoreCommand(adminId, usb.Id))).Errors));
            Assert.True((await SendAsync(server, new UpdateDatastoreCommand(adminId, usb.Id, "usb", IsReadOnly: true))).Succeeded);
            Assert.Equal("This datastore is read-only.",
                Assert.Single((await SendAsync(server, new CreateRepositoryCommand(aliceId, "desktop", usb.Id, null))).Errors));

            // The default datastore is the only writable one left, so new repositories go there.
            var desktop = await CreateAsync(server, aliceId, "desktop");
            Assert.Equal(Datastore.DefaultName, (await QueryAsync(server, new GetRepositoryQuery(aliceId, desktop)))!.Summary.DatastoreName);

            var defaultStore = (await QueryAsync(server, new GetDatastoresQuery(adminId))).Single(d => d.Name == Datastore.DefaultName);
            await SendAsync(server, new DeleteRepositoryCommand(aliceId, desktop));
            await SendAsync(server, new PurgeRepositoriesCommand(adminId));
            Assert.True((await SendAsync(server, new DeleteDatastoreCommand(adminId, defaultStore.Id))).Succeeded);

            // Never a datastore around the server's own data (database, keys).
            Assert.Equal("The folder contains the server's own data folder.",
                Assert.Single((await SendAsync(server, new CreateDatastoreCommand(adminId, "data", server.DataDirectory))).Errors));
            Assert.Equal("The last datastore cannot be removed.",
                Assert.Single((await SendAsync(server, new DeleteDatastoreCommand(adminId, usb.Id))).Errors));
        }
        finally
        {
            if (Directory.Exists(second))
            {
                Directory.Delete(second, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Pages_ShowRepositoriesTrashAndDatastores()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        var adminId = await SignInTests.AdminIdAsync(server);
        var (aliceId, alicePassword) = await CreateUserAsync(server, adminId, "alice");
        var id = await CreateAsync(server, aliceId, "laptop");
        await SendAsync(server, new ShareRepositoryCommand(aliceId, id, "admin", RepositoryAccess.AppendOnly));
        var old = await CreateAsync(server, aliceId, "old");
        await SendAsync(server, new DeleteRepositoryCommand(aliceId, old));

        var german = new Browser(admin.Client, "de-DE");
        var list = await german.GetAsync("/repositories");
        Assert.Contains("alice/laptop", list.Text);
        Assert.Contains("Nur anhängen", list.Text);
        Assert.Contains("Papierkorb (1)", list.Text);
        Assert.DoesNotContain("alice/old", list.Text);

        var page = await german.GetAsync($"/repositories/{id}");
        Assert.Contains("Ordner auf dem Server", page.Text);
        Assert.Contains("Nur anhängen", page.Text);
        Assert.Contains("Repository löschen", page.Text);

        Assert.Contains("alice/old", (await german.GetAsync("/repositories/trash")).Text);

        var datastores = await german.GetAsync("/datastores");
        Assert.Contains(Path.Combine(server.DataDirectory, "repositories"), datastores.Text);
        Assert.Contains("Datenspeicher hinzufügen", datastores.Text);

        Assert.Contains("Speicher", (await german.GetAsync("/users")).Text);

        // Users see their own pages, but not the datastores or someone else's repository.
        using var alice = server.CreateBrowser();
        await UpdateTests.SignInNewUserAsync(alice, "alice", alicePassword);
        Assert.Contains("alice/laptop", (await alice.GetAsync("/repositories")).Text);
        Assert.DoesNotContain("Folder on the server", (await alice.GetAsync($"/repositories/{id}")).Text);
        Assert.StartsWith("/account/access-denied", (await alice.GetAsync("/datastores")).Path);
        var foreign = await CreateAsync(server, adminId, "mine");
        Assert.Contains("Repository not found", (await alice.GetAsync($"/repositories/{foreign}")).Text);
    }

    private static string DatastorePath(TestServer server) => Path.Combine(server.DataDirectory, "repositories");

    private static string RepositoryFolder(TestServer server, string owner, string name) => Path.Combine(DatastorePath(server), owner, name);

    private static async Task<Guid> CreateAsync(TestServer server, string userId, string name)
    {
        var created = await SendAsync(server, new CreateRepositoryCommand(userId, name, null, null));
        Assert.True(created.Succeeded, string.Join(" ", created.Errors));
        return created.Value;
    }

    private static async Task<(string Id, string Password)> CreateUserAsync(TestServer server, string adminId, string name)
    {
        var grant = await SendAsync(server, new CreateUserCommand(
            adminId, name, null, Roles.User, NewUserAccess.TemporaryPassword, 7, "http://localhost/"));
        return (grant.Value!.UserId, grant.Value.TemporaryPassword!);
    }

    private static async Task<TResult> SendAsync<TResult>(TestServer server, ICommand<TResult> command)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        return await server.Services.GetRequiredService<IDispatcher>().SendAsync(command);
    }

    private static async Task SendAsync(TestServer server, ICommand command)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        await server.Services.GetRequiredService<IDispatcher>().SendAsync(command);
    }

    private static async Task<TResult> QueryAsync<TResult>(TestServer server, IQuery<TResult> query) =>
        await server.Services.GetRequiredService<IDispatcher>().QueryAsync(query);
}
