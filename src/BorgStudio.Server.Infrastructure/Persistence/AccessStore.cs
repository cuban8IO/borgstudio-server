using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Domain.Access;

using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Persistence;

internal sealed class AccessStore(AppDbContext db) : IAccessStore
{
    public async Task<IReadOnlyList<Device>> GetDevicesAsync(string? userId, CancellationToken cancellationToken) =>
        await db.Devices.Where(d => userId == null || d.UserId == userId).ToListAsync(cancellationToken);

    public Task<Device?> FindDeviceAsync(Guid id, CancellationToken cancellationToken) =>
        db.Devices.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<Device?> FindDeviceByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.Devices.FirstOrDefaultAsync(d => d.TokenHash == tokenHash, cancellationToken);

    public void Add(Device device) => db.Devices.Add(device);

    public void Remove(Device device) => db.Devices.Remove(device);

    public Task<PairingRequest?> FindPairingByCodeHashAsync(string pairingCodeHash, CancellationToken cancellationToken) =>
        db.PairingRequests.FirstOrDefaultAsync(p => p.PairingCodeHash == pairingCodeHash, cancellationToken);

    public Task<PairingRequest?> FindPairingByUserCodeAsync(string userCode, CancellationToken cancellationToken) =>
        db.PairingRequests.FirstOrDefaultAsync(p => p.UserCode == userCode, cancellationToken);

    public void Add(PairingRequest request) => db.PairingRequests.Add(request);

    public void Remove(PairingRequest request) => db.PairingRequests.Remove(request);

    public async Task RemoveExpiredPairingsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // SQLite cannot compare DateTimeOffset values in queries; there are only ever a few requests.
        var all = await db.PairingRequests.ToListAsync(cancellationToken);
        db.PairingRequests.RemoveRange(all.Where(p => p.ExpiresAt.AddHours(1) < now));
    }

    public async Task<IReadOnlyList<SshKey>> GetKeysAsync(Guid? repositoryId, CancellationToken cancellationToken) =>
        await db.SshKeys.Where(k => repositoryId == null || k.RepositoryId == repositoryId).ToListAsync(cancellationToken);

    public Task<SshKey?> FindKeyAsync(Guid id, CancellationToken cancellationToken) =>
        db.SshKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);

    public Task<SshKey?> FindKeyByFingerprintAsync(string fingerprint, CancellationToken cancellationToken) =>
        db.SshKeys.FirstOrDefaultAsync(k => k.Fingerprint == fingerprint, cancellationToken);

    public void Add(SshKey key) => db.SshKeys.Add(key);

    public void Remove(SshKey key) => db.SshKeys.Remove(key);

    public async Task<IReadOnlyList<RepositorySession>> GetSessionsAsync(Guid? repositoryId, CancellationToken cancellationToken) =>
        await db.RepositorySessions.Where(s => repositoryId == null || s.RepositoryId == repositoryId).ToListAsync(cancellationToken);

    public Task<RepositorySession?> FindSessionAsync(Guid id, CancellationToken cancellationToken) =>
        db.RepositorySessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Add(RepositorySession session) => db.RepositorySessions.Add(session);

    public void Remove(RepositorySession session) => db.RepositorySessions.Remove(session);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
