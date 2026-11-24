using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Abstractions.Storage;

/// <summary>
/// External datastores over SSH (Hetzner Storage Box, SSH servers with borg): the server's own key per
/// datastore, folders by repository id in the account's home, and borg passed through.
/// Failures are <see cref="RemoteStorageException"/>s with a message for people.
/// </summary>
public interface IRemoteStorage
{
    /// <summary>The host keys an SSH server offers (ssh-keyscan); nothing is trusted yet.</summary>
    Task<IReadOnlyList<SshHostKey>> ScanHostKeysAsync(string host, int port, CancellationToken cancellationToken);

    /// <summary>Creates the datastore's key pair if there is none; returns the public key ("ssh-ed25519 AAAA… comment").</summary>
    Task<string> PrepareKeyAsync(Guid datastoreId, CancellationToken cancellationToken);

    /// <summary>Signs in with the password once and adds the datastore's public key; the password is not kept.</summary>
    Task InstallKeyAsync(RemoteTarget target, DatastoreKind kind, string password, CancellationToken cancellationToken);

    /// <summary>Connects with the key and asks borg for its version (e.g. "borg 1.4.1").</summary>
    Task<string> CheckAsync(RemoteTarget target, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken);

    Task CreateAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken);

    Task DeleteAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken);

    /// <summary>Bytes each folder takes (missing ones are left out).</summary>
    Task<IReadOnlyDictionary<string, long>> MeasureAsync(RemoteTarget target, IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken);

    /// <summary>Size and free space of the account (on a Storage Box: its quota).</summary>
    Task<StorageSpace?> GetSpaceAsync(RemoteTarget target, CancellationToken cancellationToken);

    /// <summary>Copies a local folder to <paramref name="relativePath"/> on the target (rsync over SSH).</summary>
    Task UploadAsync(RemoteTarget target, string localFolder, string relativePath, CancellationToken cancellationToken);

    /// <summary>Copies <paramref name="relativePath"/> from the target into a local folder (rsync over SSH).</summary>
    Task DownloadAsync(RemoteTarget target, string relativePath, string localFolder, CancellationToken cancellationToken);

    /// <summary>Removes the key files of a datastore that is gone.</summary>
    void ForgetKey(Guid datastoreId);
}

/// <summary>An external datastore as the SSH side needs it.</summary>
public sealed record RemoteTarget(Guid DatastoreId, string Host, int Port, string UserName, string? HostKey, string RemoteBorg)
{
    public static RemoteTarget Of(Datastore datastore) => datastore.IsExternal
        ? new RemoteTarget(datastore.Id, datastore.Host!, datastore.Port!.Value, datastore.UserName!, datastore.HostKey, datastore.RemoteBorg ?? "borg")
        : throw new ArgumentException("The datastore is a folder on this server.", nameof(datastore));
}

public sealed class RemoteStorageException(string message) : Exception(message);
