namespace BorgStudio.Server.Application.Abstractions.Storage;

/// <summary>Messages the storage use cases show to users, in the user's language.</summary>
public interface IStorageTexts
{
    string RepositoryNameInvalid { get; }

    /// <summary>{0}: the name.</summary>
    string RepositoryNameTaken { get; }

    string MayNotCreateRepositories { get; }

    string UserQuotaExceeded { get; }

    string QuotaInvalid { get; }

    string DatastoreUnknown { get; }

    string DatastoreReadOnly { get; }

    string DatastoreNameInvalid { get; }

    /// <summary>{0}: the name.</summary>
    string DatastoreNameTaken { get; }

    string DatastorePathNotAbsolute { get; }

    string DatastorePathMissing { get; }

    string DatastorePathNotWritable { get; }

    string DatastorePathContainsServerData { get; }

    /// <summary>{0}: the other datastore's name.</summary>
    string DatastorePathOverlaps { get; }

    string DatastoreNotEmpty { get; }

    string DatastoreLast { get; }

    /// <summary>{0}: the user name.</summary>
    string UserUnknown { get; }

    string ShareWithOwner { get; }

    string RepositoryMoving { get; }

    string RestoreNeedsOwner { get; }

    /// <summary>{0}: the folder.</summary>
    string FolderProblem { get; }
}
