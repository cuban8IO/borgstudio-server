using BorgStudio.Server.Domain;

namespace BorgStudio.Server.Tests.Domain;

public sealed class ProductVersionTests
{
    [Fact]
    public void Release_WithCommit()
    {
        var version = ProductVersion.Parse("0.1.0+3f2a9c1d0e4b");

        Assert.Equal("0.1.0", version.Version);
        Assert.Equal("3f2a9c1", version.Commit);
        Assert.False(version.IsDevelopmentBuild);
        Assert.Equal("0.1.0 (3f2a9c1)", version.ToString());
    }

    [Fact]
    public void DevelopmentBuild_WithCommit()
    {
        var version = ProductVersion.Parse("0.0.0-dev+abcdef0123456789");

        Assert.True(version.IsDevelopmentBuild);
        Assert.Equal("abcdef0", version.Commit);
    }

    [Fact]
    public void Release_WithoutCommit()
    {
        var version = ProductVersion.Parse("1.2.3");

        Assert.Equal("1.2.3", version.Version);
        Assert.Null(version.Commit);
        Assert.Equal("1.2.3", version.ToString());
    }

    [Fact]
    public void Metadata_TakesTheCommitBeforeTheFirstDot()
    {
        Assert.Equal("abc123", ProductVersion.Parse("0.2.0+abc123.ci").Commit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc")]
    public void Missing_CountsAsDevelopmentBuild(string? informationalVersion)
    {
        Assert.True(ProductVersion.Parse(informationalVersion).IsDevelopmentBuild);
    }
}
