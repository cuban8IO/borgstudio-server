using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Abstractions.Storage;

/// <summary>
/// Datastores, repositories and storage policies in the database. Entities that come out of it are
/// tracked: change them, then call <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IStorageStore
{
    Task<IReadOnlyList<Datastore>> GetDatastoresAsync(CancellationToken cancellationToken);

    Task<Datastore?> FindDatastoreAsync(Guid id, CancellationToken cancellationToken);

    void Add(Datastore datastore);

    void Remove(Datastore datastore);

    /// <summary>All repositories, those in the trash included, with their shares.</summary>
    Task<IReadOnlyList<BackupRepository>> GetRepositoriesAsync(CancellationToken cancellationToken);

    Task<BackupRepository?> FindRepositoryAsync(Guid id, CancellationToken cancellationToken);

    void Add(BackupRepository repository);

    void Remove(BackupRepository repository);

    /// <summary>The user's policy; a user without one gets the defaults (added, so a save keeps it).</summary>
    Task<UserStoragePolicy> GetPolicyAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserStoragePolicy>> GetPoliciesAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
