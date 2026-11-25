namespace BorgStudio.Server.Domain.Access;

/// <summary>
/// A running borg connection to a repository, written by the process that serves it. Moves wait
/// until a repository has none; rows of processes that died are cleaned up.
/// </summary>
public sealed class RepositorySession
{
    private RepositorySession()
    {
    }

    public Guid Id { get; private set; }

    public Guid RepositoryId { get; private set; }

    public Guid SshKeyId { get; private set; }

    public int ProcessId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Whose key it is and its name (or the paired app's), kept for the connection's record.</summary>
    public string UserId { get; private set; } = string.Empty;

    public string By { get; private set; } = string.Empty;

    /// <summary>The repository's size when the connection began, to see how much it grew.</summary>
    public long UsedBefore { get; private set; }

    public static RepositorySession Start(
        Guid repositoryId, Guid sshKeyId, int processId, DateTimeOffset now, string userId = "", string by = "", long usedBefore = 0) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repositoryId,
        SshKeyId = sshKeyId,
        ProcessId = processId,
        StartedAt = now,
        UserId = userId,
        By = by.Length > 100 ? by[..100] : by,
        UsedBefore = usedBefore,
    };
}
