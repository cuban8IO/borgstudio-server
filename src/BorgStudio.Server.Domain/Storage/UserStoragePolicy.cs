namespace BorgStudio.Server.Domain.Storage;

/// <summary>What an administrator allows a user: creating repositories, and how much they may use in total.</summary>
public sealed class UserStoragePolicy
{
    private UserStoragePolicy()
    {
    }

    public UserStoragePolicy(string userId)
    {
        UserId = userId;
    }

    public string UserId { get; private set; } = string.Empty;

    /// <summary>Every user may create repositories unless an administrator forbids it.</summary>
    public bool MayCreateRepositories { get; set; } = true;

    /// <summary>
    /// Limit for the space all repositories the user owns take on disk; <c>null</c> = no limit. It is
    /// measured, not reserved: above it, no new backups start, but deleting to free space still works.
    /// </summary>
    public long? QuotaBytes { get; set; }
}
