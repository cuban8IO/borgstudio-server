using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Access;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Access;

public sealed record DeviceInfo(
    Guid Id,
    string UserId,
    string UserName,
    DeviceKind Kind,
    string Name,
    string? Platform,
    string? AppVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    bool IsExpired,
    int KeyCount);

/// <summary>Paired apps and API tokens: the user's own; administrators see everyone's.</summary>
public sealed record GetDevicesQuery([property: Required] string ActingUserId) : IQuery<IReadOnlyList<DeviceInfo>>;

internal sealed class GetDevicesQueryHandler(StorageContext storage, IAccessStore access) : IQueryHandler<GetDevicesQuery, IReadOnlyList<DeviceInfo>>
{
    public async Task<IReadOnlyList<DeviceInfo>> HandleAsync(GetDevicesQuery query, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var devices = await access.GetDevicesAsync(acting.IsAdministrator ? null : acting.Id, cancellationToken);
        var keys = (await access.GetKeysAsync(null, cancellationToken))
            .Where(k => k.DeviceId is not null)
            .GroupBy(k => k.DeviceId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        var users = await storage.UsersByIdAsync(cancellationToken);
        return devices
            .OrderBy(d => d.UserId == acting.Id ? 0 : 1)
            .ThenBy(d => users.TryGetValue(d.UserId, out var u) ? u.UserName : d.UserId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DeviceInfo(
                d.Id,
                d.UserId,
                users.TryGetValue(d.UserId, out var user) ? user.UserName : "?",
                d.Kind,
                d.Name,
                d.Platform,
                d.AppVersion,
                d.CreatedAt,
                d.LastUsedAt,
                d.ExpiresAt,
                d.IsExpired(storage.Now),
                keys.GetValueOrDefault(d.Id)))
            .ToList();
    }
}

/// <summary>Creates an API token for the acting user, e.g. for scripts; it is shown once.</summary>
public sealed record CreateApiTokenCommand(
    [property: Required] string ActingUserId,
    [property: Required] string Name,
    DateTimeOffset? ExpiresAt) : ICommand<AccountResult<string>>;

internal sealed class CreateApiTokenCommandHandler(StorageContext storage, IAccessStore access, ILogger<CreateApiTokenCommandHandler> logger)
    : ICommandHandler<CreateApiTokenCommand, AccountResult<string>>
{
    public async Task<AccountResult<string>> HandleAsync(CreateApiTokenCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var name = command.Name.Trim();
        if (name.Length is 0 or > Device.MaxNameLength)
        {
            return AccountResult<string>.Failed(storage.Texts.TokenNameInvalid);
        }

        if (command.ExpiresAt is { } end && end <= storage.Now)
        {
            return AccountResult<string>.Failed(storage.Texts.TokenExpiryInPast);
        }

        var (device, token) = Device.Create(acting.Id, DeviceKind.ApiToken, name, null, null, command.ExpiresAt, storage.Now);
        access.Add(device);
        await access.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{User} created the API token {Name}.", acting.UserName, name);
        return AccountResult<string>.Ok(token);
    }
}

/// <summary>Revokes a paired app or token; its SSH keys go with it. Running backups may finish.</summary>
public sealed record RevokeDeviceCommand([property: Required] string ActingUserId, Guid DeviceId) : ICommand;

internal sealed class RevokeDeviceCommandHandler(StorageContext storage, IAccessStore access, ILogger<RevokeDeviceCommandHandler> logger)
    : ICommandHandler<RevokeDeviceCommand>
{
    public async Task HandleAsync(RevokeDeviceCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var device = await access.FindDeviceAsync(command.DeviceId, cancellationToken);
        if (device is null)
        {
            return;
        }

        if (device.UserId != acting.Id && !acting.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Only the owner or an administrator may revoke a device.");
        }

        foreach (var key in (await access.GetKeysAsync(null, cancellationToken)).Where(k => k.DeviceId == device.Id))
        {
            access.Remove(key);
        }

        access.Remove(device);
        await access.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{Device} was revoked by {User}.", device.Name, acting.UserName);
    }
}

/// <summary>Checks a token from the interface: valid, not expired, its user enabled. Notes the use.</summary>
public sealed record AuthenticateTokenCommand([property: Required] string Token) : ICommand<TokenIdentity?>;

public sealed record TokenIdentity(string UserId, string UserName, Guid DeviceId, string DeviceName, DeviceKind Kind);

internal sealed class AuthenticateTokenCommandHandler(IAccessStore access, IAccountService accounts, TimeProvider timeProvider)
    : ICommandHandler<AuthenticateTokenCommand, TokenIdentity?>
{
    public async Task<TokenIdentity?> HandleAsync(AuthenticateTokenCommand command, CancellationToken cancellationToken)
    {
        if (!command.Token.StartsWith(Device.TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var device = await access.FindDeviceByTokenHashAsync(Device.HashToken(command.Token), cancellationToken);
        if (device is null || device.IsExpired(now))
        {
            return null;
        }

        var user = await accounts.GetAsync(device.UserId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return null;
        }

        if (device.Touch(now))
        {
            await access.SaveChangesAsync(cancellationToken);
        }

        return new TokenIdentity(user.Id, user.UserName, device.Id, device.Name, device.Kind);
    }
}
