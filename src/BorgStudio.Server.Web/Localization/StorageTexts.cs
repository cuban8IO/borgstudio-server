using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Web.Resources;

namespace BorgStudio.Server.Web.Localization;

internal sealed class StorageTexts : IStorageTexts
{
    public string RepositoryNameInvalid => Strings.ErrorRepositoryNameInvalid;

    public string RepositoryNameTaken => Strings.ErrorRepositoryNameTaken;

    public string MayNotCreateRepositories => Strings.ErrorMayNotCreateRepositories;

    public string UserQuotaExceeded => Strings.ErrorUserQuotaExceeded;

    public string QuotaInvalid => Strings.ErrorQuotaInvalid;

    public string DatastoreUnknown => Strings.ErrorDatastoreUnknown;

    public string DatastoreReadOnly => Strings.ErrorDatastoreReadOnly;

    public string DatastoreNameInvalid => Strings.ErrorDatastoreNameInvalid;

    public string DatastoreNameTaken => Strings.ErrorDatastoreNameTaken;

    public string DatastorePathNotAbsolute => Strings.ErrorDatastorePathNotAbsolute;

    public string DatastorePathMissing => Strings.ErrorDatastorePathMissing;

    public string DatastorePathNotWritable => Strings.ErrorDatastorePathNotWritable;

    public string DatastorePathContainsServerData => Strings.ErrorDatastorePathContainsServerData;

    public string DatastorePathOverlaps => Strings.ErrorDatastorePathOverlaps;

    public string DatastoreNotEmpty => Strings.ErrorDatastoreNotEmpty;

    public string DatastoreLast => Strings.ErrorDatastoreLast;

    public string UserUnknown => Strings.ErrorUserUnknown;

    public string ShareWithOwner => Strings.ErrorShareWithOwner;

    public string RepositoryMoving => Strings.ErrorRepositoryMoving;

    public string RestoreNeedsOwner => Strings.ErrorRestoreNeedsOwner;

    public string FolderProblem => Strings.ErrorFolderProblem;

    public string SshKeyInvalid => Strings.ErrorSshKeyInvalid;

    public string SshKeyInUse => Strings.ErrorSshKeyInUse;

    public string NoAccessToRepository => Strings.ErrorNoAccessToRepository;

    public string PairingCodeUnknown => Strings.ErrorPairingCodeUnknown;

    public string TokenNameInvalid => Strings.ErrorTokenNameInvalid;

    public string TokenExpiryInPast => Strings.ErrorTokenExpiryInPast;

    public string SshAddressInvalid => Strings.ErrorSshAddressInvalid;

    public string ExternalUserInvalid => Strings.ErrorExternalUserInvalid;

    public string ExternalHostInvalid => Strings.ErrorExternalHostInvalid;

    public string ExternalBorgInvalid => Strings.ErrorExternalBorgInvalid;

    public string ExternalHostKeyNotPublished => Strings.ErrorExternalHostKeyNotPublished;

    public string ExternalAlreadyAdded => Strings.ErrorExternalAlreadyAdded;

    public string ExternalUnreachable => Strings.ErrorExternalUnreachable;

    public string ExternalCheckFailed => Strings.ErrorExternalCheckFailed;
}
