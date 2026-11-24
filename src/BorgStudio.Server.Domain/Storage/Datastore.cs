namespace BorgStudio.Server.Domain.Storage;

/// <summary>
/// A folder that holds repositories, like a datastore in Proxmox Backup Server: e.g. a second disk
/// or a mounted NAS share. Only administrators manage them.
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

    /// <summary>Full path of the folder, without a trailing separator.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>Read-only datastores (e.g. during maintenance) take no new repositories and no moves.</summary>
    public bool IsReadOnly { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Datastore Create(string name, string path, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Name = name.Trim(),
        Path = path,
        CreatedAt = now,
    };

    public void Rename(string name) => Name = name.Trim();

    public void SetReadOnly(bool readOnly) => IsReadOnly = readOnly;
}
