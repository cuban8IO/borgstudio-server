namespace BorgStudio.Server.Domain.Storage;

/// <summary>
/// A borg repository on this server. It belongs to one user, who can share it with others; the
/// data in it is encrypted by the clients, so the server never sees its contents.
/// </summary>
public sealed class BackupRepository
{
    /// <summary>How long a deleted repository stays in the trash before it is removed for good.</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(7);

    private readonly List<RepositoryShare> shares = [];

    private BackupRepository()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The owner; <c>null</c> after the owner was deleted (the repository is then in the trash).</summary>
    public string? OwnerId { get; private set; }

    /// <summary>The user name of a deleted owner, so the trash can still say whose it was.</summary>
    public string? FormerOwnerName { get; private set; }

    public Guid DatastoreId { get; private set; }

    /// <summary>
    /// The folder inside the datastore, with '/' as separator: "owner/name" or ".trash/id" on a folder
    /// datastore, the repository's id on an external one (see <see cref="FolderById"/>).
    /// </summary>
    public string RelativePath { get; private set; } = string.Empty;

    /// <summary>
    /// On external datastores (Storage Box, SSH server) the folder is named by the id and never moves:
    /// borg there is reached through the account's home, so the clients' address must not change.
    /// </summary>
    public bool FolderById { get; private set; }

    /// <summary>Upper limit for this repository (borg's storage quota); <c>null</c> = no limit.</summary>
    public long? QuotaBytes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it was moved to the trash; <c>null</c> while it is in use.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Space on disk at the last measurement.</summary>
    public long UsedBytes { get; private set; }

    public DateTimeOffset? UsageMeasuredAt { get; private set; }

    /// <summary>Set while the repository is being moved to another datastore.</summary>
    public Guid? MoveTargetDatastoreId { get; private set; }

    /// <summary>Why the last move failed, if it did.</summary>
    public string? MoveError { get; private set; }

    /// <summary>When borg last connected to it.</summary>
    public DateTimeOffset? LastAccessAt { get; private set; }

    /// <summary>Whose key the last connection used.</summary>
    public string? LastAccessUserId { get; private set; }

    /// <summary>Name of the key (or paired app) of the last connection.</summary>
    public string? LastAccessBy { get; private set; }

    public IReadOnlyList<RepositoryShare> Shares => shares;

    public bool IsDeleted => DeletedAt is not null;

    public bool IsMoving => MoveTargetDatastoreId is not null;

    public DateTimeOffset? PurgeAt => DeletedAt + TrashRetention;

    public static BackupRepository Create(
        string ownerId, string ownerName, string name, Guid datastoreId, long? quotaBytes, DateTimeOffset now, bool folderById = false)
    {
        var repository = new BackupRepository
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = name,
            DatastoreId = datastoreId,
            QuotaBytes = quotaBytes,
            CreatedAt = now,
            FolderById = folderById,
        };
        repository.RelativePath = repository.FolderFor(ownerName, name);
        return repository;
    }

    /// <summary>Where the folder belongs for this owner and name.</summary>
    public string FolderFor(string ownerName, string name) =>
        FolderById ? RepositoryNames.IdFolder(Id) : RepositoryNames.RelativePath(ownerName, name);

    /// <summary>The access <paramref name="userId"/> has: owners always have full access.</summary>
    public RepositoryAccess? AccessFor(string userId)
    {
        if (OwnerId == userId)
        {
            return RepositoryAccess.Full;
        }

        return shares.FirstOrDefault(s => s.UserId == userId)?.Access;
    }

    public void Rename(string name, string ownerName)
    {
        Name = name;
        RelativePath = FolderFor(ownerName, name);
    }

    /// <summary>The folder follows the owner's user name, e.g. after the user was renamed.</summary>
    public void FollowOwnerName(string ownerName) => RelativePath = FolderFor(ownerName, Name);

    public void SetQuota(long? quotaBytes) => QuotaBytes = quotaBytes;

    /// <summary>Gives the repository to another user; a share the new owner had is no longer needed.</summary>
    public void SetOwner(string ownerId, string ownerName)
    {
        OwnerId = ownerId;
        FormerOwnerName = null;
        shares.RemoveAll(s => s.UserId == ownerId);
        RelativePath = FolderFor(ownerName, Name);
    }

    /// <summary>Adds a share or changes its access.</summary>
    public void Share(string userId, RepositoryAccess access)
    {
        if (userId == OwnerId)
        {
            throw new InvalidOperationException("The owner already has full access.");
        }

        var existing = shares.FirstOrDefault(s => s.UserId == userId);
        if (existing is null)
        {
            shares.Add(new RepositoryShare(Id, userId, access));
        }
        else
        {
            existing.Access = access;
        }
    }

    public bool Unshare(string userId) => shares.RemoveAll(s => s.UserId == userId) > 0;

    public void MoveToTrash(DateTimeOffset now)
    {
        DeletedAt = now;
        if (!FolderById)
        {
            RelativePath = RepositoryNames.TrashPath(Id);
        }
    }

    /// <summary>The owner was deleted: the repository goes to the trash and keeps the owner's name for reference.</summary>
    public void Orphan(string ownerName, DateTimeOffset now)
    {
        FormerOwnerName = ownerName;
        OwnerId = null;
        if (!IsDeleted)
        {
            MoveToTrash(now);
        }
    }

    public void Restore(string ownerId, string ownerName, string name)
    {
        OwnerId = ownerId;
        FormerOwnerName = null;
        Name = name;
        DeletedAt = null;
        shares.RemoveAll(s => s.UserId == ownerId);
        RelativePath = FolderFor(ownerName, name);
    }

    public void RecordAccess(string userId, string by, DateTimeOffset now)
    {
        LastAccessAt = now;
        LastAccessUserId = userId;
        LastAccessBy = by.Length > 100 ? by[..100] : by;
    }

    public void RecordUsage(long usedBytes, DateTimeOffset now)
    {
        UsedBytes = usedBytes;
        UsageMeasuredAt = now;
    }

    public void RequestMove(Guid targetDatastoreId)
    {
        if (targetDatastoreId == DatastoreId)
        {
            throw new InvalidOperationException("The repository is already on that datastore.");
        }

        MoveTargetDatastoreId = targetDatastoreId;
        MoveError = null;
    }

    /// <summary>The repository arrived at the target datastore, in <paramref name="relativePath"/>.</summary>
    public void CompleteMove(string relativePath, bool folderById)
    {
        DatastoreId = MoveTargetDatastoreId ?? throw new InvalidOperationException("No move was requested.");
        MoveTargetDatastoreId = null;
        MoveError = null;
        RelativePath = relativePath;
        FolderById = folderById;
    }

    public void FailMove(string error)
    {
        MoveTargetDatastoreId = null;
        MoveError = error;
    }
}

/// <summary>Access a user was given to someone else's repository.</summary>
public sealed class RepositoryShare
{
    private RepositoryShare()
    {
    }

    internal RepositoryShare(Guid repositoryId, string userId, RepositoryAccess access)
    {
        RepositoryId = repositoryId;
        UserId = userId;
        Access = access;
    }

    public Guid RepositoryId { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public RepositoryAccess Access { get; internal set; }
}

/// <summary>
/// What a user may do with a repository. borg 1 can only tell "everything" from "append only";
/// a read-only level can follow with borg 2, so the numbers leave room for it.
/// </summary>
public enum RepositoryAccess
{
    /// <summary>New backups, listing and restoring, but nothing can be deleted (borg serve --append-only).</summary>
    AppendOnly = 1,

    /// <summary>Everything, including prune, compact and deleting archives.</summary>
    Full = 2,
}
