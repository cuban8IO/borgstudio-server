namespace BorgStudio.Server.Application.Abstractions.Access;

/// <summary>The SSH service the server runs for borg (Linux only).</summary>
public interface ISshService
{
    SshServiceStatus Status { get; }
}

/// <param name="Port">The port it listens on (or would).</param>
/// <param name="UserName">The account clients log in as: the one the server runs under.</param>
/// <param name="HostKeys">The host keys clients should trust.</param>
/// <param name="Message">Why it is not running, if it is not.</param>
public sealed record SshServiceStatus(
    SshServiceState State,
    int Port,
    string UserName,
    IReadOnlyList<SshHostKey> HostKeys,
    string? Message = null);

/// <param name="Algorithm">e.g. "ssh-ed25519".</param>
/// <param name="Fingerprint">"SHA256:…".</param>
/// <param name="PublicKey">The key as in a known_hosts line: "ssh-ed25519 AAAA…".</param>
public sealed record SshHostKey(string Algorithm, string Fingerprint, string PublicKey);

public enum SshServiceState
{
    /// <summary>Not on this platform (Windows) – repositories can be managed, not used.</summary>
    Unsupported,

    /// <summary>Switched off in the configuration.</summary>
    Disabled,

    Starting,

    Running,

    /// <summary>Could not start, e.g. OpenSSH is not installed; see the message.</summary>
    Failed,
}

/// <summary>Whether processes are still alive (for borg sessions whose process may have died).</summary>
public interface IProcessInfo
{
    int CurrentProcessId { get; }

    bool IsAlive(int processId);
}
