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

    public static RepositorySession Start(Guid repositoryId, Guid sshKeyId, int processId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repositoryId,
        SshKeyId = sshKeyId,
        ProcessId = processId,
        StartedAt = now,
    };
}
