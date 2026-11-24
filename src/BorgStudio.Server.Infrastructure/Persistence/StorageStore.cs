using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Domain.Storage;

using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Persistence;

internal sealed class StorageStore(AppDbContext db) : IStorageStore
{
    public async Task<IReadOnlyList<Datastore>> GetDatastoresAsync(CancellationToken cancellationToken) =>
        await db.Datastores.ToListAsync(cancellationToken);

    public Task<Datastore?> FindDatastoreAsync(Guid id, CancellationToken cancellationToken) =>
        db.Datastores.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public void Add(Datastore datastore) => db.Datastores.Add(datastore);

    public void Remove(Datastore datastore) => db.Datastores.Remove(datastore);

    public async Task<IReadOnlyList<BackupRepository>> GetRepositoriesAsync(CancellationToken cancellationToken) =>
        await db.Repositories.Include(r => r.Shares).ToListAsync(cancellationToken);

    public Task<BackupRepository?> FindRepositoryAsync(Guid id, CancellationToken cancellationToken) =>
        db.Repositories.Include(r => r.Shares).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(BackupRepository repository) => db.Repositories.Add(repository);

    public void Remove(BackupRepository repository) => db.Repositories.Remove(repository);

    public async Task<UserStoragePolicy> GetPolicyAsync(string userId, CancellationToken cancellationToken)
    {
        var policy = await db.StoragePolicies.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (policy is null)
        {
            policy = new UserStoragePolicy(userId);
            db.StoragePolicies.Add(policy);
        }

        return policy;
    }

    public async Task<IReadOnlyList<UserStoragePolicy>> GetPoliciesAsync(CancellationToken cancellationToken) =>
        await db.StoragePolicies.AsNoTracking().ToListAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
