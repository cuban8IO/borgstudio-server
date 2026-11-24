using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Application.Users;
using BorgStudio.Server.Domain.Storage;
using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Infrastructure.Ssh;
using BorgStudio.Server.Tests.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Pairing, tokens, the interface, SSH keys and the decision for each borg connection.</summary>
public sealed class AccessTests
{
    private const string SecondKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMrwjEp6Mo0LkS6ZmykR85afM0Sln3gGYEG5t4bVjFvc second";

    [Fact]
    public async Task Pairing_GivesTheAppAToken_AfterTheUserConfirmed()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        using var app = server.CreateClient();

        var started = await PostAsync(app, "/api/v1/pairing", new { deviceName = "NB-NIKLAS", platform = "Windows 11", appVersion = "0.6.0" });
        var pairingCode = started.GetProperty("pairingCode").GetString()!;
        var userCode = started.GetProperty("userCode").GetString()!;
        Assert.Equal($"http://localhost/pair?code={userCode}", started.GetProperty("verificationUrlComplete").GetString());

        using (var pending = await app.PostAsJsonAsync("/api/v1/pairing/complete", new { pairingCode }))
        {
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        }

        // The user opens the page (code typed in lower case) and confirms.
        var page = await admin.GetAsync($"/pair?code={userCode.ToLowerInvariant().Replace("-", " ")}");
        Assert.Contains("NB-NIKLAS", page.Text);
        Assert.Contains("Windows 11", page.Text);
        page = await admin.SubmitAsync(page, "approve-pairing");
        Assert.Contains("The app is paired", page.Text);

        var completed = await PostAsync(app, "/api/v1/pairing/complete", new { pairingCode });
        Assert.Equal("approved", completed.GetProperty("state").GetString());
        Assert.Equal(TestServer.AdminName, completed.GetProperty("userName").GetString());
        var token = completed.GetProperty("token").GetString()!;

        // The token comes once; the code is used up.
        using (var again = await app.PostAsJsonAsync("/api/v1/pairing/complete", new { pairingCode }))
        {
            Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        }

        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var info = await GetAsync(app, "/api/v1/server");
        Assert.Equal("Test Server", info.GetProperty("name").GetString());
        Assert.Equal(TestServer.AdminName, info.GetProperty("userName").GetString());
        Assert.Equal(2222, info.GetProperty("ssh").GetProperty("port").GetInt32());
        Assert.Equal("borgstudio", info.GetProperty("ssh").GetProperty("user").GetString());
        Assert.Equal("SHA256:sMsVbpgq6JvKtZOhmmeeUNA6cAlp2WqnF8XXcWcTsGI",
            info.GetProperty("ssh").GetProperty("hostKeys")[0].GetProperty("fingerprint").GetString());

        var devices = await admin.GetAsync("/devices");
        Assert.Contains("NB-NIKLAS", devices.Text);
        Assert.Contains("BorgStudio 0.6.0", devices.Text);
    }

    [Fact]
    public async Task Pairing_CanBeRefused_AndExpires()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        using var app = server.CreateClient();

        var first = await PostAsync(app, "/api/v1/pairing", new { deviceName = "stranger" });
        var page = await admin.GetAsync($"/pair?code={first.GetProperty("userCode").GetString()}");
        page = await admin.SubmitAsync(page, "deny-pairing");
        Assert.Contains("was not paired", page.Text);
        using (var denied = await app.PostAsJsonAsync("/api/v1/pairing/complete", new { pairingCode = first.GetProperty("pairingCode").GetString() }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        var second = await PostAsync(app, "/api/v1/pairing", new { deviceName = "slow" });
        server.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains("unknown or expired", (await admin.GetAsync($"/pair?code={second.GetProperty("userCode").GetString()}")).Text);
        using (var expired = await app.PostAsJsonAsync("/api/v1/pairing/complete", new { pairingCode = second.GetProperty("pairingCode").GetString() }))
        {
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        }

        using (var noName = await app.PostAsJsonAsync("/api/v1/pairing", new { deviceName = " " }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        }
    }

    [Fact]
    public async Task Interface_NeedsAValidToken()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        var adminId = await SignInTests.AdminIdAsync(server);
        using var app = server.CreateClient();

        using (var anonymous = await app.GetAsync("/api/v1/repositories"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("unauthorized", (await anonymous.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        // A signed-in browser is not enough: the interface only takes tokens.
        using (var withCookie = await admin.Client.GetAsync("/api/v1/repositories"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, withCookie.StatusCode);
        }

        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "bss_forged");
        using (var forged = await app.GetAsync("/api/v1/repositories"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }

        var token = (await SendAsync(server, new CreateApiTokenCommand(adminId, "script", server.Clock.GetUtcNow().AddDays(2)))).Value!;
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(JsonValueKind.Array, (await GetAsync(app, "/api/v1/repositories")).ValueKind);

        using (var unknown = await app.GetAsync("/api/v1/nothing-here"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("not_found", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        server.Clock.Advance(TimeSpan.FromDays(3));
        using (var expired = await app.GetAsync("/api/v1/repositories"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        }

        var device = Assert.Single(await QueryAsync(server, new GetDevicesQuery(adminId)));
        Assert.True(device.IsExpired);
        await SendAsync(server, new RevokeDeviceCommand(adminId, device.Id));
        Assert.Empty(await QueryAsync(server, new GetDevicesQuery(adminId)));
    }

    [Fact]
    public async Task App_CreatesRepositories_RegistersItsKey_AndSeesStatistics()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var aliceId = await CreateUserAsync(server, adminId, "alice");
        using var app = await PairedAppAsync(server, adminId, "laptop");

        var datastores = await GetAsync(app, "/api/v1/datastores");
        Assert.True(datastores.GetProperty("mayCreate").GetBoolean());
        Assert.Equal(Datastore.DefaultName, datastores.GetProperty("datastores")[0].GetProperty("name").GetString());

        using var created = await app.PostAsJsonAsync("/api/v1/repositories", new { name = "laptop", quotaBytes = 5_000_000_000L });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var repository = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = repository.GetProperty("id").GetGuid();
        Assert.Equal("admin/laptop", repository.GetProperty("fullName").GetString());
        Assert.Equal("full", repository.GetProperty("access").GetString());
        Assert.Equal($"ssh://borgstudio@localhost:2222/./{id:N}", repository.GetProperty("url").GetString());
        Assert.True(repository.GetProperty("datastoreFreeBytes").GetInt64() > 0);

        using (var duplicate = await app.PostAsJsonAsync("/api/v1/repositories", new { name = "laptop" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        }

        using (var bad = await app.PutAsJsonAsync($"/api/v1/repositories/{id}/ssh-key", new { publicKey = "nonsense" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        }

        using var registered = await app.PutAsJsonAsync($"/api/v1/repositories/{id}/ssh-key", new { publicKey = AccessRulesTests.PublicKey });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal($"ssh://borgstudio@localhost:2222/./{id:N}", (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString());

        // A new key of the same app replaces its old one; a key opens one repository only.
        Assert.True((await app.PutAsJsonAsync($"/api/v1/repositories/{id}/ssh-key", new { publicKey = SecondKey })).IsSuccessStatusCode);
        var key = Assert.Single(await QueryAsync(server, new GetSshKeysQuery(adminId, id)));
        Assert.Equal("laptop", key.DeviceName);
        Assert.Equal("SHA256:sMsVbpgq6JvKtZOhmmeeUNA6cAlp2WqnF8XXcWcTsGI", key.Fingerprint);
        var other = await CreateRepositoryAsync(server, adminId, "desktop");
        using (var reused = await app.PutAsJsonAsync($"/api/v1/repositories/{other}/ssh-key", new { publicKey = SecondKey }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
            Assert.Contains("admin/laptop", (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        }

        // Alice's repository is not the app's business until it is shared; then the app sees who else uses it.
        var alices = await CreateRepositoryAsync(server, aliceId, "shared");
        using (var foreign = await app.GetAsync($"/api/v1/repositories/{alices}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        await SendAsync(server, new ShareRepositoryCommand(aliceId, alices, TestServer.AdminName, RepositoryAccess.AppendOnly));
        var shared = await GetAsync(app, $"/api/v1/repositories/{alices}");
        Assert.Equal("appendOnly", shared.GetProperty("access").GetString());
        Assert.False(shared.GetProperty("isOwner").GetBoolean());
        Assert.Equal(2, shared.GetProperty("participants").GetArrayLength());
        Assert.Equal(3, (await GetAsync(app, "/api/v1/repositories")).GetArrayLength());
    }

    [Fact]
    public async Task BorgSessions_FollowAccessQuotaAndMoves()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var aliceId = await CreateUserAsync(server, adminId, "alice");
        var id = await CreateRepositoryAsync(server, adminId, "laptop");
        File.WriteAllBytes(Path.Combine(server.DataDirectory, "repositories", "admin", "laptop", "data"), new byte[3000]);
        await SendAsync(server, new MeasureRepositoryCommand(adminId, id));

        // Owner: full access, no quota.
        Assert.True((await SendAsync(server, new AddSshKeyCommand(adminId, id, "borgmatic", AccessRulesTests.PublicKey))).Succeeded);
        var ownerKey = Assert.Single(await QueryAsync(server, new GetSshKeysQuery(adminId, id)));
        var plan = await SendAsync(server, new OpenBorgSessionCommand(ownerKey.Id, Environment.ProcessId));
        Assert.Null(plan.Refusal);
        Assert.False(plan.AppendOnly);
        Assert.Null(plan.StorageQuotaBytes);
        Assert.Equal(Path.Combine(server.DataDirectory, "repositories", "admin", "laptop"), plan.RepositoryPath);
        Assert.Equal(["serve", "--umask=077", "--restrict-to-repository", plan.RepositoryPath], SshConsole.BorgArguments(plan));

        // After the session the repository is measured again, and the last access shows.
        await SendAsync(server, new CloseBorgSessionCommand(plan.SessionId));
        var stats = Assert.Single(await QueryAsync(server, new GetRepositoryStatsQuery(adminId, "localhost", id)));
        Assert.Equal("borgmatic", stats.LastAccess!.By);
        Assert.Equal(TestServer.AdminName, stats.LastAccess.UserName);
        Assert.Equal(3000, stats.UsedBytes);

        // Shared append-only, with the owner's quota: the repository may grow by what is left of it.
        await SendAsync(server, new ShareRepositoryCommand(adminId, id, "alice", RepositoryAccess.AppendOnly));
        Assert.True((await SendAsync(server, new AddSshKeyCommand(aliceId, id, null, SecondKey))).Succeeded);
        var aliceKey = (await QueryAsync(server, new GetSshKeysQuery(adminId, id))).Single(k => k.UserName == "alice");
        Assert.Equal("second", aliceKey.Name);
        await SendAsync(server, new SetUserStorageCommand(adminId, adminId, true, 10_000));
        plan = await SendAsync(server, new OpenBorgSessionCommand(aliceKey.Id, Environment.ProcessId));
        Assert.True(plan.AppendOnly);
        Assert.Equal(10_000, plan.StorageQuotaBytes);
        Assert.Contains("--append-only", SshConsole.BorgArguments(plan));
        await SendAsync(server, new CloseBorgSessionCommand(plan.SessionId));

        // Over the owner's quota (another repository takes the space): no growth, deleting still works.
        var other = await CreateRepositoryAsync(server, adminId, "desktop");
        File.WriteAllBytes(Path.Combine(server.DataDirectory, "repositories", "admin", "desktop", "data"), new byte[9000]);
        await SendAsync(server, new MeasureRepositoryCommand(adminId, other));
        plan = await SendAsync(server, new OpenBorgSessionCommand(ownerKey.Id, Environment.ProcessId));
        Assert.Equal(3000, plan.StorageQuotaBytes);
        await SendAsync(server, new CloseBorgSessionCommand(plan.SessionId));

        // Ending the share removes Alice's key; the connection is refused.
        await SendAsync(server, new RemoveShareCommand(adminId, id, aliceId));
        Assert.Contains("no longer known", (await SendAsync(server, new OpenBorgSessionCommand(aliceKey.Id, Environment.ProcessId))).Refusal);

        // A repository in the trash: refused.
        await SendAsync(server, new DeleteRepositoryCommand(adminId, id));
        Assert.Contains("deleted", (await SendAsync(server, new OpenBorgSessionCommand(ownerKey.Id, Environment.ProcessId))).Refusal);
    }

    [Fact]
    public async Task Sessions_KeepMovesWaiting()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var id = await CreateRepositoryAsync(server, adminId, "laptop");
        await SendAsync(server, new AddSshKeyCommand(adminId, id, "key", AccessRulesTests.PublicKey));
        var key = Assert.Single(await QueryAsync(server, new GetSshKeysQuery(adminId, id)));
        var second = Path.Combine(Path.GetTempPath(), "borgstudio-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        try
        {
            await SendAsync(server, new CreateDatastoreCommand(adminId, "usb", second));
            var usb = (await QueryAsync(server, new GetDatastoresQuery(adminId))).Single(d => d.Name == "usb");

            // This test process counts as the one serving borg.
            var plan = await SendAsync(server, new OpenBorgSessionCommand(key.Id, Environment.ProcessId));
            await SendAsync(server, new MoveRepositoryCommand(adminId, id, usb.Id));
            Assert.Contains("being moved", (await SendAsync(server, new OpenBorgSessionCommand(key.Id, Environment.ProcessId))).Refusal);

            await SendAsync(server, new RunRepositoryMovesCommand());
            Assert.Equal(Datastore.DefaultName, (await QueryAsync(server, new GetRepositoryQuery(adminId, id)))!.Summary.DatastoreName);

            await SendAsync(server, new CloseBorgSessionCommand(plan.SessionId));
            await SendAsync(server, new RunRepositoryMovesCommand());
            Assert.Equal("usb", (await QueryAsync(server, new GetRepositoryQuery(adminId, id)))!.Summary.DatastoreName);
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public async Task AuthorizedKeys_AnswersWithARestrictedLine()
    {
        using var server = new TestServer();
        (await SignInTests.CompleteSetupAsync(server, twoFactor: false)).Dispose();
        var adminId = await SignInTests.AdminIdAsync(server);
        var id = await CreateRepositoryAsync(server, adminId, "laptop");
        await SendAsync(server, new AddSshKeyCommand(adminId, id, "key", AccessRulesTests.PublicKey));
        var key = Assert.Single(await QueryAsync(server, new GetSshKeysQuery(adminId, id)));

        using var known = new StringWriter();
        await SshConsole.AuthorizedKeysAsync(server.DataDirectory, AccessRulesTests.Fingerprint, known);
        var line = known.ToString().Trim();
        Assert.StartsWith("restrict,command=\"'", line);
        Assert.Contains($" borg-serve --data '{server.DataDirectory}' {key.Id:D}\" ", line);
        Assert.EndsWith(string.Join(' ', AccessRulesTests.PublicKey.Split(' ')[..2]), line);

        using var unknown = new StringWriter();
        await SshConsole.AuthorizedKeysAsync(server.DataDirectory, "SHA256:unknown", unknown);
        Assert.Equal(string.Empty, unknown.ToString());

        Assert.Equal(
            "restrict,command=\"'/opt/it'\\''s/BorgStudio.Server' borg-serve --data '/opt/data' 00000000-0000-0000-0000-000000000001\" ssh-ed25519 AAAA",
            SshConsole.AuthorizedKeyLine("/opt/it's/BorgStudio.Server", "/opt/data", new Guid("00000000-0000-0000-0000-000000000001"), "ssh-ed25519", "AAAA"));
    }

    [Fact]
    public async Task Pages_ShowDevicesKeysAndTheSshService()
    {
        using var server = new TestServer();
        using var admin = await SignInTests.CompleteSetupAsync(server, twoFactor: false);
        var adminId = await SignInTests.AdminIdAsync(server);
        var id = await CreateRepositoryAsync(server, adminId, "laptop");
        await SendAsync(server, new AddSshKeyCommand(adminId, id, "borgmatic", AccessRulesTests.PublicKey));
        await SendAsync(server, new CreateApiTokenCommand(adminId, "nightly script", null));

        var german = new Browser(admin.Client, "de-DE");
        var repository = await german.GetAsync($"/repositories/{id}");
        Assert.Contains("Zugriff mit borg", repository.Text);
        Assert.Contains($"ssh://borgstudio@localhost:2222/./{id:N}", repository.Text);
        Assert.Contains(AccessRulesTests.Fingerprint, repository.Text);
        Assert.Contains("borgmatic", repository.Text);

        var devices = await german.GetAsync("/devices");
        Assert.Contains("nightly script", devices.Text);
        Assert.Contains("API-Token", devices.Text);

        var settings = await german.GetAsync("/settings");
        Assert.Contains("SSH für borg", settings.Text);
        Assert.Contains("Der SSH-Dienst für borg läuft.", settings.Text);

        Assert.True((await SendAsync(server, new UpdateSshSettingsCommand(adminId, "backup.example.org:22022"))).Succeeded);
        Assert.False((await SendAsync(server, new UpdateSshSettingsCommand(adminId, "ssh://x"))).Succeeded);
        var endpoint = await QueryAsync(server, new GetSshEndpointQuery("localhost"));
        Assert.Equal($"ssh://borgstudio@backup.example.org:22022/./{id:N}", endpoint.UrlFor(id));

        // Pairing needs a full sign-in.
        using var anonymous = server.CreateBrowser();
        Assert.StartsWith("/account/login", (await anonymous.GetAsync("/pair?code=ABCD-EFGH")).Path);
    }

    private static async Task<HttpClient> PairedAppAsync(TestServer server, string userId, string name)
    {
        var started = await SendAsync(server, new StartPairingCommand(name, "Linux", "0.6.0"));
        await SendAsync(server, new DecidePairingCommand(userId, started.UserCode, Approve: true));
        var outcome = await SendAsync(server, new CompletePairingCommand(started.PairingCode));
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outcome.Token);
        return client;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateRepositoryAsync(TestServer server, string userId, string name) =>
        (await SendAsync(server, new CreateRepositoryCommand(userId, name, null, null))).Value;

    private static async Task<string> CreateUserAsync(TestServer server, string adminId, string name) =>
        (await SendAsync(server, new CreateUserCommand(adminId, name, null, Roles.User, NewUserAccess.TemporaryPassword, 7, "http://localhost/"))).Value!.UserId;

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
