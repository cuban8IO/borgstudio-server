using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Tests.Web;

/// <summary>External datastores without a network: folders and their sizes in memory, one set per datastore.</summary>
public sealed class FakeRemoteStorage : IRemoteStorage
{
    /// <summary>One of the keys Hetzner publishes for its Storage Boxes (ED25519).</summary>
    public static readonly SshHostKey HetznerKey = new("ssh-ed25519", "SHA256:XqONwb1S0zuj5A1CDxpOSuD2hnAArV1A3wKY7Z3sdgM", "ssh-ed25519 AAAAHETZNER");

    public static readonly SshHostKey OtherKey = new("ssh-ed25519", "SHA256:sMsVbpgq6JvKtZOhmmeeUNA6cAlp2WqnF8XXcWcTsGI", "ssh-ed25519 AAAAOTHER");

    /// <summary>What ssh-keyscan answers.</summary>
    public List<SshHostKey> HostKeys { get; } = [HetznerKey];

    /// <summary>Folders per datastore, with the bytes they take.</summary>
    public Dictionary<Guid, Dictionary<string, long>> Folders { get; } = [];

    public List<(Guid Datastore, DatastoreKind Kind, string Password)> InstalledKeys { get; } = [];

    public StorageSpace Space { get; set; } = new(1_000_000_000_000, 400_000_000_000);

    public bool Unreachable { get; set; }

    public Task<IReadOnlyList<SshHostKey>> ScanHostKeysAsync(string host, int port, CancellationToken cancellationToken) =>
        Unreachable ? throw new RemoteStorageException("Connection refused") : Task.FromResult<IReadOnlyList<SshHostKey>>([.. HostKeys]);

    public Task<string> PrepareKeyAsync(Guid datastoreId, CancellationToken cancellationToken) =>
        Task.FromResult($"ssh-ed25519 AAAASERVERKEY BorgStudio Server {datastoreId:N}");

    public Task InstallKeyAsync(RemoteTarget target, DatastoreKind kind, string password, CancellationToken cancellationToken)
    {
        if (password == "wrong")
        {
            throw new RemoteStorageException("Permission denied (password).");
        }

        InstalledKeys.Add((target.DatastoreId, kind, password));
        return Task.CompletedTask;
    }

    public Task<string> CheckAsync(RemoteTarget target, CancellationToken cancellationToken) =>
        Unreachable ? throw new RemoteStorageException("Connection refused") : Task.FromResult($"{target.RemoteBorg.Replace('-', ' ')}.1");

    public Task<bool> ExistsAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult(Of(target).ContainsKey(relativePath));

    public Task CreateAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken)
    {
        Of(target)[relativePath] = 0;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken)
    {
        Of(target).Remove(relativePath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, long>> MeasureAsync(
        RemoteTarget target, IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken) =>
        Unreachable
            ? throw new RemoteStorageException("Connection refused")
            : Task.FromResult<IReadOnlyDictionary<string, long>>(Of(target).Where(f => relativePaths.Contains(f.Key)).ToDictionary());

    public Task<StorageSpace?> GetSpaceAsync(RemoteTarget target, CancellationToken cancellationToken) =>
        Unreachable ? throw new RemoteStorageException("Connection refused") : Task.FromResult<StorageSpace?>(Space);

    public Task UploadAsync(RemoteTarget target, string localFolder, string relativePath, CancellationToken cancellationToken)
    {
        Of(target)[relativePath] = new DirectoryInfo(localFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return Task.CompletedTask;
    }

    public Task DownloadAsync(RemoteTarget target, string relativePath, string localFolder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localFolder);
        File.WriteAllBytes(Path.Combine(localFolder, "data"), new byte[Of(target)[relativePath]]);
        return Task.CompletedTask;
    }

    public void ForgetKey(Guid datastoreId) => Folders.Remove(datastoreId);

    private Dictionary<string, long> Of(RemoteTarget target)
    {
        if (!Folders.TryGetValue(target.DatastoreId, out var folders))
        {
            Folders[target.DatastoreId] = folders = [];
        }

        return folders;
    }
}
