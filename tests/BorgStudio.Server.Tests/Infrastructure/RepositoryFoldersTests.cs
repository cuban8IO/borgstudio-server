using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Infrastructure;
using BorgStudio.Server.Infrastructure.Storage;

namespace BorgStudio.Server.Tests.Infrastructure;

public sealed class RepositoryFoldersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "borgstudio-folders-tests", Guid.NewGuid().ToString("N"));
    private readonly RepositoryFolders folders;
    private readonly string datastore;

    public RepositoryFoldersTests()
    {
        datastore = Path.Combine(root, "store");
        Directory.CreateDirectory(datastore);
        folders = new RepositoryFolders(new ServerPaths(Path.Combine(root, "data")));
    }

    [Fact]
    public void Normalize_WantsFullPaths()
    {
        Assert.Null(folders.Normalize("relative/folder"));
        Assert.Null(folders.Normalize(" "));
        Assert.Equal(datastore, folders.Normalize(datastore + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Overlap_FindsNestedFolders()
    {
        var a = Path.Combine(root, "a");
        Assert.True(folders.Overlap(a, Path.Combine(a, "b")));
        Assert.True(folders.Overlap(Path.Combine(a, "b"), a));
        Assert.True(folders.Overlap(a, a));
        Assert.False(folders.Overlap(a, Path.Combine(root, "ab")));
    }

    [Fact]
    public void Check_FindsMissingFolders_AndTheServersOwnData()
    {
        Assert.Null(folders.Check(datastore));
        Assert.Equal(DatastoreProblem.Missing, folders.Check(Path.Combine(root, "missing")));

        // A datastore that holds the data folder would mix backups with the database; one inside it is fine.
        Directory.CreateDirectory(Path.Combine(root, "data", "repositories"));
        Assert.Equal(DatastoreProblem.ContainsServerData, folders.Check(root));
        Assert.Null(folders.Check(Path.Combine(root, "data", "repositories")));
    }

    [Fact]
    public void CreateMoveMeasureDelete()
    {
        folders.Create(datastore, "niklas/laptop");
        File.WriteAllBytes(Path.Combine(datastore, "niklas", "laptop", "data"), new byte[1234]);
        Assert.Throws<IOException>(() => folders.Create(datastore, "niklas/laptop"));
        Assert.Equal(1234, folders.Measure(datastore, "niklas/laptop"));

        folders.Move(datastore, "niklas/laptop", "alice/laptop");
        Assert.False(Directory.Exists(Path.Combine(datastore, "niklas")));
        Assert.True(folders.Exists(datastore, "alice/laptop"));

        folders.Delete(datastore, "alice/laptop");
        Assert.False(Directory.Exists(Path.Combine(datastore, "alice")));
        Assert.Equal(0, folders.Measure(datastore, "alice/laptop"));
    }

    [Fact]
    public void Paths_StayInsideTheDatastore()
    {
        Assert.Throws<ArgumentException>(() => folders.Create(datastore, "../outside"));
        Assert.Throws<ArgumentException>(() => folders.Delete(datastore, "."));
    }

    [Fact]
    public async Task MoveToDatastore_CarriesEverythingOver()
    {
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        folders.Create(datastore, "niklas/laptop");
        Directory.CreateDirectory(Path.Combine(datastore, "niklas", "laptop", "data", "0"));
        File.WriteAllBytes(Path.Combine(datastore, "niklas", "laptop", "data", "0", "1"), new byte[5000]);
        File.WriteAllText(Path.Combine(datastore, "niklas", "laptop", "config"), "[repository]");

        await folders.MoveToDatastoreAsync(datastore, "niklas/laptop", other, "niklas/laptop", new Progress<long>(), CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(datastore, "niklas")));
        Assert.Equal("[repository]", File.ReadAllText(Path.Combine(other, "niklas", "laptop", "config")));
        Assert.Equal(5000 + "[repository]".Length, folders.Measure(other, "niklas/laptop"));
        Assert.False(Directory.Exists(Path.Combine(other, ".incoming")));
    }

    [Fact]
    public void Space_IsReadFromTheDisk()
    {
        var space = folders.GetSpace(datastore);
        Assert.NotNull(space);
        Assert.InRange(space.FreeBytes, 1, space.TotalBytes);
        Assert.Null(folders.GetSpace(Path.Combine(root, "missing")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
