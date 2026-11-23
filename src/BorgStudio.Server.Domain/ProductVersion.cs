namespace BorgStudio.Server.Domain;

/// <summary>
/// The version of a BorgStudio Server build, read from an informational version such as
/// <c>0.1.0+3f2a9c1d…</c> (release) or <c>0.0.0-dev+3f2a9c1d…</c> (local build).
/// </summary>
public sealed record ProductVersion
{
    /// <summary>Version local builds carry; release builds get a real one from the release workflow.</summary>
    public const string DevelopmentVersion = "0.0.0-dev";

    private const int ShortCommitLength = 7;

    private ProductVersion(string version, string? commit)
    {
        Version = version;
        Commit = commit;
    }

    /// <summary>The version without build metadata, e.g. <c>0.1.0</c>.</summary>
    public string Version { get; }

    /// <summary>The short commit hash from the build metadata, if there is one.</summary>
    public string? Commit { get; }

    /// <summary>True for local builds that were not made by the release workflow.</summary>
    public bool IsDevelopmentBuild => Version == DevelopmentVersion;

    public static ProductVersion Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new ProductVersion(DevelopmentVersion, null);
        }

        var parts = informationalVersion.Trim().Split('+', 2);
        var version = parts[0];
        var commit = parts.Length > 1 ? ShortenCommit(parts[1]) : null;
        return new ProductVersion(version.Length == 0 ? DevelopmentVersion : version, commit);
    }

    private static string? ShortenCommit(string metadata)
    {
        // Build metadata may hold more than the commit (e.g. "abc123.ci"); the commit comes first.
        var commit = metadata.Split('.', 2)[0];
        if (commit.Length == 0)
        {
            return null;
        }

        return commit.Length > ShortCommitLength ? commit[..ShortCommitLength] : commit;
    }

    public override string ToString() => Commit is null ? Version : $"{Version} ({Commit})";
}
