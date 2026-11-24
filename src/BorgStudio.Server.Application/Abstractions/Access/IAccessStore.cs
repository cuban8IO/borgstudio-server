using BorgStudio.Server.Domain.Access;

namespace BorgStudio.Server.Application.Abstractions.Access;

/// <summary>
/// Paired apps, API tokens, pairing requests, SSH keys and running borg sessions in the database.
/// Entities that come out of it are tracked: change them, then call <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IAccessStore
{
    /// <param name="userId">Only this user's devices; <c>null</c> for all.</param>
    Task<IReadOnlyList<Device>> GetDevicesAsync(string? userId, CancellationToken cancellationToken);

    Task<Device?> FindDeviceAsync(Guid id, CancellationToken cancellationToken);

    Task<Device?> FindDeviceByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    void Add(Device device);

    void Remove(Device device);

    Task<PairingRequest?> FindPairingByCodeHashAsync(string pairingCodeHash, CancellationToken cancellationToken);

    Task<PairingRequest?> FindPairingByUserCodeAsync(string userCode, CancellationToken cancellationToken);

    void Add(PairingRequest request);

    void Remove(PairingRequest request);

    /// <summary>Removes pairing requests that ran out (an hour after the fact, so a late poll still learns why).</summary>
    Task RemoveExpiredPairingsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <param name="repositoryId">Only the keys of this repository; <c>null</c> for all.</param>
    Task<IReadOnlyList<SshKey>> GetKeysAsync(Guid? repositoryId, CancellationToken cancellationToken);

    Task<SshKey?> FindKeyAsync(Guid id, CancellationToken cancellationToken);

    Task<SshKey?> FindKeyByFingerprintAsync(string fingerprint, CancellationToken cancellationToken);

    void Add(SshKey key);

    void Remove(SshKey key);

    /// <param name="repositoryId">Only the sessions of this repository; <c>null</c> for all.</param>
    Task<IReadOnlyList<RepositorySession>> GetSessionsAsync(Guid? repositoryId, CancellationToken cancellationToken);

    Task<RepositorySession?> FindSessionAsync(Guid id, CancellationToken cancellationToken);

    void Add(RepositorySession session);

    void Remove(RepositorySession session);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
