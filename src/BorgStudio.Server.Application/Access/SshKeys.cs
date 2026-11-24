using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Access;
using BorgStudio.Server.Domain.Settings;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Access;

/// <param name="DeviceName">The paired app or token that registered it; <c>null</c> for keys added by hand.</param>
public sealed record SshKeyInfo(
    Guid Id,
    string Name,
    string UserId,
    string UserName,
    string? DeviceName,
    string Algorithm,
    string Fingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool CanRemove);

/// <summary>The SSH keys of a repository: managers see all, users with a share their own.</summary>
public sealed record GetSshKeysQuery([property: Required] string ActingUserId, Guid RepositoryId) : IQuery<IReadOnlyList<SshKeyInfo>>;

internal sealed class GetSshKeysQueryHandler(StorageContext storage, IAccessStore access) : IQueryHandler<GetSshKeysQuery, IReadOnlyList<SshKeyInfo>>
{
    public async Task<IReadOnlyList<SshKeyInfo>> HandleAsync(GetSshKeysQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(query.RepositoryId, cancellationToken);
        if (repository is null || !StorageContext.CanSee(acting, repository))
        {
            return [];
        }

        var manage = StorageContext.CanManage(acting, repository);
        var users = await storage.UsersByIdAsync(cancellationToken);
        var devices = (await access.GetDevicesAsync(null, cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        return (await access.GetKeysAsync(repository.Id, cancellationToken))
            .Where(k => manage || k.UserId == acting.Id)
            .OrderBy(k => k.CreatedAt.UtcTicks)
            .Select(k => new SshKeyInfo(
                k.Id,
                k.Name,
                k.UserId,
                users.TryGetValue(k.UserId, out var u) ? u.UserName : "?",
                k.DeviceId is { } d ? devices.GetValueOrDefault(d) : null,
                k.Algorithm,
                k.Fingerprint,
                k.CreatedAt,
                k.LastUsedAt,
                manage || k.UserId == acting.Id))
            .ToList();
    }
}

/// <summary>Adds a public key by hand (web interface), with the rights the acting user has on the repository.</summary>
public sealed record AddSshKeyCommand(
    [property: Required] string ActingUserId,
    Guid RepositoryId,
    string? Name,
    [property: Required] string PublicKey) : ICommand<AccountResult>;

internal sealed class AddSshKeyCommandHandler(StorageContext storage, IAccessStore access, ILogger<AddSshKeyCommandHandler> logger)
    : ICommandHandler<AddSshKeyCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(AddSshKeyCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(command.RepositoryId, cancellationToken);
        if (repository is null || repository.IsDeleted || repository.AccessFor(acting.Id) is null)
        {
            return AccountResult.Failed(storage.Texts.NoAccessToRepository);
        }

        var key = SshPublicKey.Parse(command.PublicKey);
        if (key is null)
        {
            return AccountResult.Failed(storage.Texts.SshKeyInvalid);
        }

        if (await SshKeyRules.InUseAsync(storage, access, key, null, cancellationToken) is { } inUse)
        {
            return AccountResult.Failed(inUse);
        }

        access.Add(SshKey.Create(repository.Id, acting.Id, null, command.Name ?? string.Empty, key, storage.Now));
        await access.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{User} added an SSH key to repository {Name}.", acting.UserName, repository.Name);
        return AccountResult.Success;
    }
}

/// <summary>
/// A paired app registers its key for a repository (interface); it replaces the key that app had
/// for it before. Returns where to connect.
/// </summary>
public sealed record SetDeviceKeyCommand(
    [property: Required] string UserId,
    Guid DeviceId,
    Guid RepositoryId,
    [property: Required] string PublicKey,
    string? RequestHost) : ICommand<AccountResult<RepositoryConnection>>;

/// <param name="Url">The borg repository address, e.g. ssh://borgstudio@backup.example.org:2222/./3f2a…</param>
public sealed record RepositoryConnection(string Url, SshEndpoint Endpoint);

internal sealed class SetDeviceKeyCommandHandler(
    StorageContext storage,
    IAccessStore access,
    SshEndpoints endpoints,
    ILogger<SetDeviceKeyCommandHandler> logger) : ICommandHandler<SetDeviceKeyCommand, AccountResult<RepositoryConnection>>
{
    public async Task<AccountResult<RepositoryConnection>> HandleAsync(SetDeviceKeyCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.UserId, cancellationToken);
        var repository = await storage.Store.FindRepositoryAsync(command.RepositoryId, cancellationToken);
        if (repository is null || repository.IsDeleted || repository.AccessFor(acting.Id) is null)
        {
            return AccountResult<RepositoryConnection>.Failed(storage.Texts.NoAccessToRepository);
        }

        var device = await access.FindDeviceAsync(command.DeviceId, cancellationToken)
            ?? throw new UnauthorizedAccessException("The device does not exist.");
        var key = SshPublicKey.Parse(command.PublicKey);
        if (key is null)
        {
            return AccountResult<RepositoryConnection>.Failed(storage.Texts.SshKeyInvalid);
        }

        var existing = (await access.GetKeysAsync(repository.Id, cancellationToken)).FirstOrDefault(k => k.DeviceId == device.Id);
        if (existing?.Fingerprint != key.Fingerprint)
        {
            if (await SshKeyRules.InUseAsync(storage, access, key, existing?.Id, cancellationToken) is { } inUse)
            {
                return AccountResult<RepositoryConnection>.Failed(inUse);
            }

            if (existing is not null)
            {
                access.Remove(existing);
            }

            access.Add(SshKey.Create(repository.Id, acting.Id, device.Id, device.Name, key, storage.Now));
            await access.SaveChangesAsync(cancellationToken);
            logger.LogInformation("{Device} of {User} registered its SSH key for repository {Name}.", device.Name, acting.UserName, repository.Name);
        }

        var endpoint = await endpoints.GetAsync(command.RequestHost, cancellationToken);
        return AccountResult<RepositoryConnection>.Ok(new RepositoryConnection(endpoint.UrlFor(repository.Id), endpoint));
    }
}

/// <summary>Removes a key: its user, or whoever manages the repository.</summary>
public sealed record RemoveSshKeyCommand([property: Required] string ActingUserId, Guid KeyId) : ICommand;

internal sealed class RemoveSshKeyCommandHandler(StorageContext storage, IAccessStore access) : ICommandHandler<RemoveSshKeyCommand>
{
    public async Task HandleAsync(RemoveSshKeyCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var key = await access.FindKeyAsync(command.KeyId, cancellationToken);
        if (key is null)
        {
            return;
        }

        var repository = await storage.Store.FindRepositoryAsync(key.RepositoryId, cancellationToken);
        if (key.UserId != acting.Id && (repository is null || !StorageContext.CanManage(acting, repository)))
        {
            throw new UnauthorizedAccessException("This key may not be removed by this user.");
        }

        access.Remove(key);
        await access.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>How clients reach the SSH service.</summary>
/// <param name="Host">Host name or address clients connect to.</param>
public sealed record SshEndpoint(string Host, int Port, string UserName, IReadOnlyList<SshHostKey> HostKeys, SshServiceState State, string? Message)
{
    /// <summary>
    /// The repository's borg address. The path names the repository by its id, so it stays the same
    /// when the repository is renamed, gets another owner or moves to another datastore.
    /// </summary>
    public string UrlFor(Guid repositoryId)
    {
        var host = Host.Contains(':') ? $"[{Host}]" : Host;
        return $"ssh://{UserName}@{host}:{Port}/./{repositoryId:N}";
    }
}

/// <summary>Where the SSH service is, for pages and the interface; <paramref name="RequestHost"/> is the host the request came to.</summary>
public sealed record GetSshEndpointQuery(string? RequestHost) : IQuery<SshEndpoint>;

internal sealed class GetSshEndpointQueryHandler(SshEndpoints endpoints) : IQueryHandler<GetSshEndpointQuery, SshEndpoint>
{
    public Task<SshEndpoint> HandleAsync(GetSshEndpointQuery query, CancellationToken cancellationToken) =>
        endpoints.GetAsync(query.RequestHost, cancellationToken);
}

/// <summary>Sets (or clears) the public SSH address (administrators).</summary>
public sealed record UpdateSshSettingsCommand([property: Required] string ActingUserId, string? PublicAddress) : ICommand<AccountResult>;

internal sealed class UpdateSshSettingsCommandHandler(StorageContext storage, IServerSettingsStore settings)
    : ICommandHandler<UpdateSshSettingsCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(UpdateSshSettingsCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (!ServerSettings.TryParseSshAddress(command.PublicAddress, out _, out _))
        {
            return AccountResult.Failed(storage.Texts.SshAddressInvalid);
        }

        var current = await settings.GetAsync(cancellationToken);
        current.SetSshPublicAddress(command.PublicAddress);
        await settings.SaveAsync(current, cancellationToken);
        return AccountResult.Success;
    }
}

internal sealed class SshEndpoints(ISshService ssh, IServerSettingsStore settings)
{
    public async Task<SshEndpoint> GetAsync(string? requestHost, CancellationToken cancellationToken)
    {
        var status = ssh.Status;
        var configured = (await settings.GetAsync(cancellationToken)).SshPublicAddress;
        ServerSettings.TryParseSshAddress(configured, out var host, out var port);
        return new SshEndpoint(
            host ?? (string.IsNullOrWhiteSpace(requestHost) ? "localhost" : requestHost),
            port ?? status.Port,
            status.UserName,
            status.HostKeys,
            status.State,
            status.Message);
    }
}

internal static class SshKeyRules
{
    /// <summary>A key may open one repository only, so the server knows which one a connection is for.</summary>
    public static async Task<string?> InUseAsync(
        StorageContext storage, IAccessStore access, SshPublicKey key, Guid? except, CancellationToken cancellationToken)
    {
        var existing = await access.FindKeyByFingerprintAsync(key.Fingerprint, cancellationToken);
        if (existing is null || existing.Id == except)
        {
            return null;
        }

        var repository = await storage.Store.FindRepositoryAsync(existing.RepositoryId, cancellationToken);
        var users = await storage.UsersByIdAsync(cancellationToken);
        var name = repository is null ? "?" : $"{RepositoryViews.OwnerName(repository, users)}/{repository.Name}";
        return string.Format(storage.Texts.SshKeyInUse, name);
    }

    /// <summary>Removes the keys of users who no longer have access, e.g. after a share ended or the owner changed.</summary>
    public static async Task PruneAsync(IAccessStore access, BackupRepository repository, CancellationToken cancellationToken)
    {
        foreach (var key in await access.GetKeysAsync(repository.Id, cancellationToken))
        {
            if (repository.AccessFor(key.UserId) is null)
            {
                access.Remove(key);
            }
        }
    }
}
