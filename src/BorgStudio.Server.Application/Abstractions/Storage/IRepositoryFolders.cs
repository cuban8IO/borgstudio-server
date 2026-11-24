namespace BorgStudio.Server.Application.Abstractions.Storage;

/// <summary>
/// The folders of datastores and repositories on disk. Relative paths use '/' and are always
/// relative to a datastore's folder.
/// </summary>
public interface IRepositoryFolders
{
    /// <summary>Whether repositories on this server can be used for backups (Linux only).</summary>
    bool HostingSupported { get; }

    /// <summary>The full path without a trailing separator; <c>null</c> if it is not an absolute path.</summary>
    string? Normalize(string path);

    /// <summary>Whether two normalized paths name the same folder or one lies inside the other.</summary>
    bool Overlap(string a, string b);

    /// <summary>Checks that a datastore folder exists, the server may write to it and it holds none of the server's own data.</summary>
    DatastoreProblem? Check(string datastorePath);

    /// <summary>Size and free space of the disk the folder is on; <c>null</c> if it cannot be read.</summary>
    StorageSpace? GetSpace(string datastorePath);

    /// <summary>Size and free space of the disk with the server's own data (database, keys).</summary>
    StorageSpace? DataSpace();

    bool Exists(string datastorePath, string relativePath);

    /// <summary>Creates an empty repository folder (the client initializes the repository in it).</summary>
    void Create(string datastorePath, string relativePath);

    /// <summary>Renames a folder within one datastore.</summary>
    void Move(string datastorePath, string fromRelativePath, string toRelativePath);

    void Delete(string datastorePath, string relativePath);

    /// <summary>Bytes the folder takes, with everything in it; 0 if it does not exist.</summary>
    long Measure(string datastorePath, string relativePath);

    /// <summary>
    /// Moves a folder to another datastore: renamed if both are on the same disk, otherwise copied
    /// (reporting the bytes copied so far) and then deleted at the source.
    /// </summary>
    Task MoveToDatastoreAsync(
        string fromDatastorePath,
        string fromRelativePath,
        string toDatastorePath,
        string toRelativePath,
        IProgress<long> progress,
        CancellationToken cancellationToken);

    string FullPath(string datastorePath, string relativePath);

    /// <summary>
    /// A new empty folder to copy into: inside the datastore's ".incoming" (so moving it into place is
    /// a rename), or in the server's data folder when no datastore is given.
    /// </summary>
    string NewStagingFolder(string? datastorePath);

    /// <summary>Moves a complete staging folder into its place in the datastore.</summary>
    void Adopt(string stagingFolder, string datastorePath, string relativePath);

    void DeleteStaging(string stagingFolder);
}

public enum DatastoreProblem
{
    Missing,
    NotWritable,
    ContainsServerData,
}

public sealed record StorageSpace(long TotalBytes, long FreeBytes);
