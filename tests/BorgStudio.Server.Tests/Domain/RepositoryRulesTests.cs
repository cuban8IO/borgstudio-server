using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Tests.Domain;

public sealed class RepositoryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("laptop", true)]
    [InlineData("Laptop-2026_v1.0", true)]
    [InlineData("1", true)]
    [InlineData("", false)]
    [InlineData(".hidden", false)]
    [InlineData("-dash", false)]
    [InlineData("with space", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData("umlaut-ä", false)]
    [InlineData(null, false)]
    public void Names(string? name, bool valid) => Assert.Equal(valid, RepositoryNames.IsValid(name));

    [Fact]
    public void Names_AreLimitedTo64Characters()
    {
        Assert.True(RepositoryNames.IsValid(new string('a', 64)));
        Assert.False(RepositoryNames.IsValid(new string('a', 65)));
    }

    [Theory]
    [InlineData("niklas", "niklas/laptop")]
    [InlineData("first.last@example.org", "first.last@example.org/laptop")]
    [InlineData("..", "_../laptop")]
    [InlineData(".trash", "_.trash/laptop")]
    public void Folders_FollowTheOwner(string owner, string expected) =>
        Assert.Equal(expected, RepositoryNames.RelativePath(owner, "laptop"));

    [Fact]
    public void Owner_HasFullAccess_SharesTheirOwn()
    {
        var repository = BackupRepository.Create("owner", "niklas", "laptop", Guid.NewGuid(), null, Now);
        repository.Share("alice", RepositoryAccess.AppendOnly);
        repository.Share("bob", RepositoryAccess.Full);
        repository.Share("alice", RepositoryAccess.Full);

        Assert.Equal(RepositoryAccess.Full, repository.AccessFor("owner"));
        Assert.Equal(RepositoryAccess.Full, repository.AccessFor("alice"));
        Assert.Equal(2, repository.Shares.Count);
        Assert.Null(repository.AccessFor("mallory"));
        Assert.Throws<InvalidOperationException>(() => repository.Share("owner", RepositoryAccess.AppendOnly));

        Assert.True(repository.Unshare("bob"));
        Assert.Null(repository.AccessFor("bob"));
    }

    [Fact]
    public void NewOwner_LosesTheShareTheyHad()
    {
        var repository = BackupRepository.Create("owner", "niklas", "laptop", Guid.NewGuid(), null, Now);
        repository.Share("alice", RepositoryAccess.AppendOnly);

        repository.SetOwner("alice", "alice");

        Assert.Empty(repository.Shares);
        Assert.Equal("alice/laptop", repository.RelativePath);
        Assert.Null(repository.AccessFor("owner"));
    }

    [Fact]
    public void Trash_KeepsTheRepositoryForSevenDays()
    {
        var repository = BackupRepository.Create("owner", "niklas", "laptop", Guid.NewGuid(), null, Now);

        repository.MoveToTrash(Now);

        Assert.True(repository.IsDeleted);
        Assert.Equal(Now.AddDays(7), repository.PurgeAt);
        Assert.Equal($".trash/{repository.Id:N}", repository.RelativePath);

        repository.Restore("owner", "niklas", "laptop-old");
        Assert.False(repository.IsDeleted);
        Assert.Equal("niklas/laptop-old", repository.RelativePath);
    }

    [Fact]
    public void DeletedOwner_LeavesTheRepositoryInTheTrash()
    {
        var repository = BackupRepository.Create("owner", "niklas", "laptop", Guid.NewGuid(), null, Now);

        repository.Orphan("niklas", Now);

        Assert.Null(repository.OwnerId);
        Assert.Equal("niklas", repository.FormerOwnerName);
        Assert.True(repository.IsDeleted);
    }
}
