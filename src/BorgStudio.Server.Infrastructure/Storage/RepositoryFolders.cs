using BorgStudio.Server.Application.Abstractions.Storage;

namespace BorgStudio.Server.Infrastructure.Storage;

/// <summary>Repository folders on the local file system. On Linux, only the server's account may read them.</summary>
internal sealed class RepositoryFolders(ServerPaths paths) : IRepositoryFolders
{
    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Folders being copied to a datastore land here first, so a half copy never looks like a repository.</summary>
    private const string IncomingFolder = ".incoming";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool HostingSupported => OperatingSystem.IsLinux();

    public string? Normalize(string path)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        var full = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public bool Overlap(string a, string b) => IsSameOrInside(a, b) || IsSameOrInside(b, a);

    public DatastoreProblem? Check(string datastorePath)
    {
        if (!Directory.Exists(datastorePath))
        {
            return DatastoreProblem.Missing;
        }

        // The data folder itself (database, keys) must not end up inside a datastore; a datastore
        // inside the data folder, like the default data/repositories, is fine.
        if (IsSameOrInside(datastorePath, Normalize(paths.DataDirectory)!))
        {
            return DatastoreProblem.ContainsServerData;
        }

        var probe = Path.Combine(datastorePath, $".borgstudio-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DatastoreProblem.NotWritable;
        }
    }

    public StorageSpace? GetSpace(string datastorePath)
    {
        try
        {
            if (!Directory.Exists(datastorePath))
            {
                return null;
            }

            // DriveInfo asks the file system the folder is on (statvfs on Linux), also for mounted disks.
            var drive = new DriveInfo(datastorePath);
            return new StorageSpace(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public StorageSpace? DataSpace() => GetSpace(paths.DataDirectory);

    public bool Exists(string datastorePath, string relativePath) => Directory.Exists(Full(datastorePath, relativePath));

    public void Create(string datastorePath, string relativePath)
    {
        var full = Full(datastorePath, relativePath);
        CreatePrivate(Path.GetDirectoryName(full)!);
        if (Directory.Exists(full))
        {
            throw new IOException($"The folder {full} already exists.");
        }

        CreatePrivate(full);
    }

    public void Move(string datastorePath, string fromRelativePath, string toRelativePath)
    {
        var from = Full(datastorePath, fromRelativePath);
        var to = Full(datastorePath, toRelativePath);
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }

        CreatePrivate(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            // A repository whose folder vanished (e.g. deleted by hand) still gets its new place.
            CreatePrivate(to);
        }

        RemoveEmptyParent(datastorePath, from);
    }

    public void Delete(string datastorePath, string relativePath)
    {
        var full = Full(datastorePath, relativePath);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }

        RemoveEmptyParent(datastorePath, full);
    }

    public long Measure(string datastorePath, string relativePath)
    {
        var full = Full(datastorePath, relativePath);
        if (!Directory.Exists(full))
        {
            return 0;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return new DirectoryInfo(full).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    public string FullPath(string datastorePath, string relativePath) => Full(datastorePath, relativePath);

    public string NewStagingFolder(string? datastorePath)
    {
        var folder = datastorePath is null
            ? Path.Combine(paths.DataDirectory, "staging", Guid.NewGuid().ToString("N"))
            : Path.Combine(datastorePath, IncomingFolder, Guid.NewGuid().ToString("N"));
        CreatePrivate(folder);
        return folder;
    }

    public void Adopt(string stagingFolder, string datastorePath, string relativePath)
    {
        var target = Full(datastorePath, relativePath);
        if (Directory.Exists(target))
        {
            throw new IOException($"The folder {target} already exists.");
        }

        CreatePrivate(Path.GetDirectoryName(target)!);
        Directory.Move(stagingFolder, target);
        RemoveEmptyParent(datastorePath, stagingFolder);
    }

    public void DeleteStaging(string stagingFolder)
    {
        if (Directory.Exists(stagingFolder))
        {
            Directory.Delete(stagingFolder, recursive: true);
        }

        var parent = Path.GetDirectoryName(stagingFolder);
        if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }
    }

    public async Task MoveToDatastoreAsync(
        string fromDatastorePath,
        string fromRelativePath,
        string toDatastorePath,
        string toRelativePath,
        IProgress<long> progress,
        CancellationToken cancellationToken)
    {
        var source = Full(fromDatastorePath, fromRelativePath);
        var target = Full(toDatastorePath, toRelativePath);
        if (Directory.Exists(target))
        {
            throw new IOException($"The folder {target} already exists.");
        }

        CreatePrivate(Path.GetDirectoryName(target)!);
        if (!Directory.Exists(source))
        {
            CreatePrivate(target);
            return;
        }

        // Same disk: a rename is instant. Across disks Directory.Move fails, then the folder is copied.
        try
        {
            Directory.Move(source, target);
            RemoveEmptyParent(fromDatastorePath, source);
            return;
        }
        catch (IOException)
        {
        }

        var incoming = Path.Combine(toDatastorePath, IncomingFolder, Guid.NewGuid().ToString("N"));
        try
        {
            CreatePrivate(incoming);
            await CopyAsync(source, incoming, progress, cancellationToken);
            Directory.Move(incoming, target);
        }
        catch
        {
            if (Directory.Exists(incoming))
            {
                Directory.Delete(incoming, recursive: true);
            }

            throw;
        }
        finally
        {
            RemoveEmptyParent(toDatastorePath, incoming);
        }

        Directory.Delete(source, recursive: true);
        RemoveEmptyParent(fromDatastorePath, source);
    }

    private static async Task CopyAsync(string source, string target, IProgress<long> progress, CancellationToken cancellationToken)
    {
        long copied = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            CreatePrivate(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            await using (var input = File.OpenRead(file))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    progress.Report(copied);
                }

                // borg relies on its segment files being on disk before it continues.
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
        }
    }

    private static string Full(string datastorePath, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(datastorePath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrInside(datastorePath, full) || string.Equals(full, datastorePath, PathComparison))
        {
            throw new ArgumentException($"{relativePath} is not a folder inside the datastore.", nameof(relativePath));
        }

        return full;
    }

    private static bool IsSameOrInside(string folder, string path) =>
        string.Equals(folder, path, PathComparison)
        || path.StartsWith(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar, PathComparison);

    private static void CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, PrivateFolder);
        }
    }

    /// <summary>Removes the owner (or .trash/.incoming) folder once its last repository is gone.</summary>
    private static void RemoveEmptyParent(string datastorePath, string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || string.Equals(parent, datastorePath, PathComparison) || !Directory.Exists(parent))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
            // Someone else just put something there; then it stays.
        }
    }
}
