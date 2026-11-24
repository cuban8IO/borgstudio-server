using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Access;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Access;

/// <summary>An app asks to be paired; no sign-in needed. It gets a secret pairing code to poll with and a user code to show.</summary>
public sealed record StartPairingCommand(
    [property: Required, MaxLength(Device.MaxNameLength)] string DeviceName,
    [property: MaxLength(100)] string? Platform,
    [property: MaxLength(50)] string? AppVersion) : ICommand<PairingStarted>;

public sealed record PairingStarted(string PairingCode, string UserCode, DateTimeOffset ExpiresAt);

internal sealed class StartPairingCommandHandler(IAccessStore access, TimeProvider timeProvider)
    : ICommandHandler<StartPairingCommand, PairingStarted>
{
    public async Task<PairingStarted> HandleAsync(StartPairingCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await access.RemoveExpiredPairingsAsync(now, cancellationToken);
        var (request, code) = PairingRequest.Create(command.DeviceName, command.Platform, command.AppVersion, now);

        // User codes are short; a clash with an open request is unlikely but possible.
        while (await access.FindPairingByUserCodeAsync(request.UserCode, cancellationToken) is not null)
        {
            (request, code) = PairingRequest.Create(command.DeviceName, command.Platform, command.AppVersion, now);
        }

        access.Add(request);
        await access.SaveChangesAsync(cancellationToken);
        return new PairingStarted(code, request.UserCode, request.ExpiresAt);
    }
}

/// <summary>What the confirmation page shows about an open pairing request; <c>null</c> if the code is unknown or expired.</summary>
public sealed record GetPairingQuery([property: Required] string ActingUserId, string? UserCode) : IQuery<PairingInfo?>;

public sealed record PairingInfo(string UserCode, string DeviceName, string? Platform, string? AppVersion, DateTimeOffset ExpiresAt);

internal sealed class GetPairingQueryHandler(StorageContext storage, IAccessStore access)
    : IQueryHandler<GetPairingQuery, PairingInfo?>
{
    public async Task<PairingInfo?> HandleAsync(GetPairingQuery query, CancellationToken cancellationToken)
    {
        await storage.RequireActingAsync(query.ActingUserId, cancellationToken);
        var request = await access.FindPairingByUserCodeAsync(PairingRequest.NormalizeUserCode(query.UserCode), cancellationToken);
        return request is null || request.IsExpired(storage.Now) || request.IsDenied || request.ApprovedUserId is not null
            ? null
            : new PairingInfo(request.UserCode, request.DeviceName, request.Platform, request.AppVersion, request.ExpiresAt);
    }
}

/// <summary>The signed-in user confirms (or refuses) a pairing request; the app picks up its token afterwards.</summary>
public sealed record DecidePairingCommand([property: Required] string ActingUserId, string? UserCode, bool Approve)
    : ICommand<AccountResult>;

internal sealed class DecidePairingCommandHandler(StorageContext storage, IAccessStore access, ILogger<DecidePairingCommandHandler> logger)
    : ICommandHandler<DecidePairingCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(DecidePairingCommand command, CancellationToken cancellationToken)
    {
        var acting = await storage.RequireActingAsync(command.ActingUserId, cancellationToken);
        var request = await access.FindPairingByUserCodeAsync(PairingRequest.NormalizeUserCode(command.UserCode), cancellationToken);
        if (request is null || request.IsExpired(storage.Now) || request.IsDenied || request.ApprovedUserId is not null)
        {
            return AccountResult.Failed(storage.Texts.PairingCodeUnknown);
        }

        if (command.Approve)
        {
            request.Approve(acting.Id);
            logger.LogInformation("{User} confirmed the pairing of {Device}.", acting.UserName, request.DeviceName);
        }
        else
        {
            request.Deny();
        }

        await access.SaveChangesAsync(cancellationToken);
        return AccountResult.Success;
    }
}

/// <summary>The app polls with its pairing code until the user decided.</summary>
public sealed record CompletePairingCommand([property: Required] string PairingCode) : ICommand<PairingOutcome>;

/// <param name="Token">The device token, once (only with <see cref="PairingState.Approved"/>).</param>
public sealed record PairingOutcome(PairingState State, string? Token = null, Guid? DeviceId = null, string? UserName = null);

public enum PairingState
{
    /// <summary>The user has not decided yet: ask again in a few seconds.</summary>
    Pending,

    Approved,

    Denied,

    /// <summary>Unknown, expired, or already completed.</summary>
    Expired,
}

internal sealed class CompletePairingCommandHandler(
    IAccessStore access,
    StorageContext storage,
    ILogger<CompletePairingCommandHandler> logger) : ICommandHandler<CompletePairingCommand, PairingOutcome>
{
    public async Task<PairingOutcome> HandleAsync(CompletePairingCommand command, CancellationToken cancellationToken)
    {
        var request = await access.FindPairingByCodeHashAsync(Device.HashToken(command.PairingCode.Trim()), cancellationToken);
        if (request is null || request.IsExpired(storage.Now))
        {
            return new PairingOutcome(PairingState.Expired);
        }

        if (request.IsDenied)
        {
            access.Remove(request);
            await access.SaveChangesAsync(cancellationToken);
            return new PairingOutcome(PairingState.Denied);
        }

        if (request.ApprovedUserId is not { } userId)
        {
            return new PairingOutcome(PairingState.Pending);
        }

        var user = await storage.Accounts.GetAsync(userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            access.Remove(request);
            await access.SaveChangesAsync(cancellationToken);
            return new PairingOutcome(PairingState.Denied);
        }

        var (device, token) = Device.Create(user.Id, DeviceKind.App, request.DeviceName, request.Platform, request.AppVersion, null, storage.Now);
        access.Add(device);
        access.Remove(request);
        await access.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{Device} is paired with {User}.", device.Name, user.UserName);
        return new PairingOutcome(PairingState.Approved, token, device.Id, user.UserName);
    }
}
