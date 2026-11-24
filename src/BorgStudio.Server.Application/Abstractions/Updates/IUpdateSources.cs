namespace BorgStudio.Server.Application.Abstractions.Updates;

/// <summary>Published releases of BorgStudio Server (GitHub).</summary>
public interface IReleaseFeed
{
    /// <summary>The newest releases, newest first; drafts and pre-releases are left out.</summary>
    Task<IReadOnlyList<PublishedRelease>> GetReleasesAsync(CancellationToken cancellationToken);
}

/// <param name="Notes">The release notes (Markdown, "### New features" / "### Fixes" …).</param>
public sealed record PublishedRelease(Version Version, DateTimeOffset PublishedAt, string Notes, string Url);

/// <summary>The CHANGELOG.md this build was made with.</summary>
public interface IChangelogSource
{
    string Read();
}

/// <summary>How this server was installed, so an update hint can name the right command.</summary>
public interface IInstallationInfo
{
    InstallationKind Kind { get; }
}

public enum InstallationKind
{
    Unknown,

    /// <summary>A local build, not from a release.</summary>
    Development,

    /// <summary>An LXC container or VM set up with lxc-install.sh (has the "update" command).</summary>
    Lxc,

    /// <summary>Linux with install.sh (systemd service).</summary>
    LinuxService,

    /// <summary>Windows with install.ps1 (Windows service).</summary>
    WindowsService,

    Docker,
}
