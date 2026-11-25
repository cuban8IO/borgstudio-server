namespace BorgStudio.Server.Domain.Storage;

/// <summary>
/// Where repositories are kept, like a datastore in Proxmox Backup Server: a folder on this server
/// (e.g. a second disk or a mounted NAS share), or an external one reached over SSH – a Hetzner
/// Storage Box or any SSH server with borg. For external ones the server passes borg connections
/// through, so access rights, quotas and statistics work the same. Only administrators manage them.
/// </summary>
public sealed class Datastore
{
    public const int MaxNameLength = 64;

    /// <summary>The datastore the server creates on its first start, inside its data folder.</summary>
    public const string DefaultName = "default";

    private Datastore()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DatastoreKind Kind { get; private set; }

    /// <summary>
    /// A folder datastore's full path without a trailing separator; for external ones the address
    /// "ssh://user@host:port", which also keeps two datastores from pointing to the same account.
    /// </summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>External datastores: the SSH server.</summary>
    public string? Host { get; private set; }

    public int? Port { get; private set; }

    public string? UserName { get; private set; }

    /// <summary>External datastores: the borg program there, e.g. "borg-1.4" on a Storage Box.</summary>
    public string? RemoteBorg { get; private set; }

    /// <summary>External datastores: the trusted host key ("ssh-ed25519 AAAA…"); any other is refused.</summary>
    public string? HostKey { get; private set; }

    /// <summary>Read-only datastores (e.g. during maintenance) take no new repositories and no moves.</summary>
    public bool IsReadOnly { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Size and free space at the last measurement (external ones are only measured now and then).</summary>
    public long? TotalBytes { get; private set; }

    public long? FreeBytes { get; private set; }

    public DateTimeOffset? SpaceMeasuredAt { get; private set; }

    /// <summary>External datastores: why the last attempt to reach it failed; <c>null</c> when it worked.</summary>
    public string? LastError { get; private set; }

    public bool IsExternal => Kind != DatastoreKind.Folder;

    public static Datastore Create(string name, string path, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Name = name.Trim(),
        Kind = DatastoreKind.Folder,
        Path = path,
        CreatedAt = now,
    };

    public static Datastore CreateExternal(
        Guid id, string name, DatastoreKind kind, string host, int port, string userName, string remoteBorg, string hostKey, DateTimeOffset now)
    {
        if (kind == DatastoreKind.Folder)
        {
            throw new ArgumentException("A folder datastore is created with Create.", nameof(kind));
        }

        return new Datastore
        {
            Id = id,
            Name = name.Trim(),
            Kind = kind,
            Path = ExternalAddress(userName, host, port),
            Host = host,
            Port = port,
            UserName = userName,
            RemoteBorg = remoteBorg,
            HostKey = hostKey,
            CreatedAt = now,
        };
    }

    public static string ExternalAddress(string userName, string host, int port) =>
        $"ssh://{userName}@{(host.Contains(':') ? $"[{host}]" : host)}:{port}";

    public void Rename(string name) => Name = name.Trim();

    public void SetReadOnly(bool readOnly) => IsReadOnly = readOnly;

    public void SetRemoteBorg(string remoteBorg)
    {
        if (!IsExternal)
        {
            throw new InvalidOperationException("Only external datastores run borg elsewhere.");
        }

        RemoteBorg = remoteBorg;
    }

    public void RecordSpace(long totalBytes, long freeBytes, DateTimeOffset now)
    {
        TotalBytes = totalBytes;
        FreeBytes = freeBytes;
        SpaceMeasuredAt = now;
        LastError = null;
    }

    public void RecordError(string error) => LastError = error.Length > 1000 ? error[..1000] : error;
}

public enum DatastoreKind
{
    /// <summary>A folder on this server.</summary>
    Folder = 0,

    /// <summary>A Hetzner Storage Box (SSH on port 23, borg-1.x there).</summary>
    HetznerStorageBox = 1,

    /// <summary>Any SSH server with borg, e.g. a NAS or rsync.net.</summary>
    Ssh = 2,
}

/// <summary>What BorgStudio knows about Hetzner Storage Boxes.</summary>
public static class HetznerStorageBox
{
    public const string Domain = "your-storagebox.de";

    /// <summary>Port with SSH commands (borg, rsync …); port 22 only offers SFTP and SCP.</summary>
    public const int Port = 23;

    /// <summary>Hetzner's command that adds a public key read from standard input.</summary>
    public const string InstallKeyCommand = "install-ssh-key";

    /// <summary>The borg versions Hetzner offers that work here (borg 1.1 is too old).</summary>
    public static readonly IReadOnlyList<string> BorgVersions = ["borg-1.4", "borg-1.2"];

    /// <summary>
    /// Host key fingerprints of all Storage Box servers (ED25519, RSA, ECDSA), as published at
    /// https://docs.hetzner.com/storage/storage-box/general – update them if Hetzner changes its keys.
    /// </summary>
    public static readonly IReadOnlyList<string> HostKeyFingerprints =
    [
        "SHA256:XqONwb1S0zuj5A1CDxpOSuD2hnAArV1A3wKY7Z3sdgM",
        "SHA256:EMlfI8GsRIfpVkoW1H2u0zYVpFGKkIMKHFZIRkf2ioI",
        "SHA256:oDHZqKXnoMtgvPBjjC57pcuFez28roaEuFcfwyg8O5c",
    ];

    /// <summary>Main account u123456 or sub-account u123456-sub1.</summary>
    public static bool IsUserName(string? user) =>
        user is not null && System.Text.RegularExpressions.Regex.IsMatch(user, "^u[0-9]+(-sub[0-9]+)?$");

    public static string HostFor(string userName) => $"{userName.ToLowerInvariant()}.{Domain}";
}
