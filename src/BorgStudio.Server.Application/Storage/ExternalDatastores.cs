using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Storage;

/// <summary>
/// First step of adding an external datastore: where it is. The server reads the host keys it offers
/// and prepares its own key for it. For a Storage Box the host key is trusted when it is one Hetzner
/// publishes; for other SSH servers the administrator compares the fingerprint.
/// </summary>
public sealed record PrepareExternalDatastoreCommand(
    [property: Required] string ActingUserId,
    DatastoreKind Kind,
    [property: Required] string UserName,
    string? Host,
    int? Port) : ICommand<AccountResult<ExternalDatastoreDraft>>;

/// <param name="DraftId">The new datastore's id; its key is already prepared under it.</param>
/// <param name="TrustedHostKey">The host key that is trusted without asking (Storage Box), if any.</param>
/// <param name="PublicKey">The server's key for it, to install by hand if no password is given.</param>
public sealed record ExternalDatastoreDraft(
    Guid DraftId,
    DatastoreKind Kind,
    string Host,
    int Port,
    string UserName,
    IReadOnlyList<SshHostKey> HostKeys,
    SshHostKey? TrustedHostKey,
    string PublicKey,
    IReadOnlyList<string> BorgChoices);

internal sealed class PrepareExternalDatastoreCommandHandler(StorageContext storage)
    : ICommandHandler<PrepareExternalDatastoreCommand, AccountResult<ExternalDatastoreDraft>>
{
    public async Task<AccountResult<ExternalDatastoreDraft>> HandleAsync(PrepareExternalDatastoreCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var where = ExternalDatastoreRules.Where(storage, command.Kind, command.UserName, command.Host, command.Port);
        if (where.Error is { } error)
        {
            return AccountResult<ExternalDatastoreDraft>.Failed(error);
        }

        var (host, port, user) = (where.Host, where.Port, where.User);

        var address = Datastore.ExternalAddress(user, host, port);
        if ((await storage.Store.GetDatastoresAsync(cancellationToken)).Any(d => d.Path == address))
        {
            return AccountResult<ExternalDatastoreDraft>.Failed(string.Format(storage.Texts.ExternalAlreadyAdded, address));
        }

        IReadOnlyList<SshHostKey> keys;
        try
        {
            keys = await storage.Remote.ScanHostKeysAsync(host, port, cancellationToken);
        }
        catch (RemoteStorageException e)
        {
            return AccountResult<ExternalDatastoreDraft>.Failed(string.Format(storage.Texts.ExternalUnreachable, $"{host}:{port}", e.Message));
        }

        if (keys.Count == 0)
        {
            return AccountResult<ExternalDatastoreDraft>.Failed(string.Format(storage.Texts.ExternalUnreachable, $"{host}:{port}", string.Empty));
        }

        SshHostKey? trusted = null;
        if (command.Kind == DatastoreKind.HetznerStorageBox)
        {
            trusted = keys.FirstOrDefault(k => HetznerStorageBox.HostKeyFingerprints.Contains(k.Fingerprint));
            if (trusted is null)
            {
                return AccountResult<ExternalDatastoreDraft>.Failed(storage.Texts.ExternalHostKeyNotPublished);
            }
        }

        var draftId = Guid.NewGuid();
        var publicKey = await storage.Remote.PrepareKeyAsync(draftId, cancellationToken);
        return AccountResult<ExternalDatastoreDraft>.Ok(new ExternalDatastoreDraft(
            draftId, command.Kind, host, port, user, keys, trusted, publicKey, ExternalDatastoreRules.BorgChoices(command.Kind)));
    }
}

/// <summary>
/// Second step: installs the server's key with the password (used once, never kept) or relies on it
/// having been installed by hand, checks that borg runs there, and adds the datastore.
/// </summary>
public sealed record CreateExternalDatastoreCommand(
    [property: Required] string ActingUserId,
    Guid DraftId,
    DatastoreKind Kind,
    [property: Required] string Name,
    [property: Required] string UserName,
    string? Host,
    int? Port,
    [property: Required] string HostKey,
    [property: Required] string RemoteBorg,
    string? Password) : ICommand<AccountResult<string>>;

internal sealed class CreateExternalDatastoreCommandHandler(StorageContext storage, ILogger<CreateExternalDatastoreCommandHandler> logger)
    : ICommandHandler<CreateExternalDatastoreCommand, AccountResult<string>>
{
    public async Task<AccountResult<string>> HandleAsync(CreateExternalDatastoreCommand command, CancellationToken cancellationToken)
    {
        await storage.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        var existing = await storage.Store.GetDatastoresAsync(cancellationToken);
        if (DatastoreRules.CheckName(storage, existing, command.Name, null) is { } nameError)
        {
            return AccountResult<string>.Failed(nameError);
        }

        var where = ExternalDatastoreRules.Where(storage, command.Kind, command.UserName, command.Host, command.Port);
        if (where.Error is { } whereError)
        {
            return AccountResult<string>.Failed(whereError);
        }

        if (ExternalDatastoreRules.CheckBorg(storage, command.Kind, command.RemoteBorg) is { } borgError)
        {
            return AccountResult<string>.Failed(borgError);
        }

        var address = Datastore.ExternalAddress(where.User, where.Host, where.Port);
        if (existing.Any(d => d.Path == address || d.Id == command.DraftId))
        {
            return AccountResult<string>.Failed(string.Format(storage.Texts.ExternalAlreadyAdded, address));
        }

        var target = new RemoteTarget(command.DraftId, where.Host, where.Port, where.User, command.HostKey.Trim(), command.RemoteBorg.Trim());
        string version;
        try
        {
            if (!string.IsNullOrEmpty(command.Password))
            {
                await storage.Remote.InstallKeyAsync(target, command.Kind, command.Password, cancellationToken);
            }

            version = await storage.Remote.CheckAsync(target, cancellationToken);
        }
        catch (RemoteStorageException e)
        {
            return AccountResult<string>.Failed(string.Format(storage.Texts.ExternalCheckFailed, e.Message));
        }

        var datastore = Datastore.CreateExternal(
            command.DraftId, command.Name, command.Kind, where.Host, where.Port, where.User, target.RemoteBorg, target.HostKey!, storage.Now);
        try
        {
            if (await storage.Remote.GetSpaceAsync(target, cancellationToken) is { } space)
            {
                datastore.RecordSpace(space.TotalBytes, space.FreeBytes, storage.Now);
            }
        }
        catch (RemoteStorageException e)
        {
            datastore.RecordError(e.Message);
        }

        storage.Store.Add(datastore);
        await storage.Store.SaveChangesAsync(cancellationToken);
        logger.LogInformation("External datastore {Name} was added at {Address} ({Version}).", datastore.Name, address, version);
        return AccountResult<string>.Ok(version);
    }
}

internal static partial class ExternalDatastoreRules
{
    public static IReadOnlyList<string> BorgChoices(DatastoreKind kind) =>
        kind == DatastoreKind.HetznerStorageBox ? HetznerStorageBox.BorgVersions : ["borg", "borg-1.4", "borg1"];

    /// <summary>Host, port and user of an external datastore, with a Storage Box's host and port filled in.</summary>
    public static (string? Error, string Host, int Port, string User) Where(
        StorageContext storage, DatastoreKind kind, string userName, string? host, int? port)
    {
        var user = userName.Trim();
        switch (kind)
        {
            case DatastoreKind.HetznerStorageBox:
                return HetznerStorageBox.IsUserName(user.ToLowerInvariant())
                    ? (null, HetznerStorageBox.HostFor(user), HetznerStorageBox.Port, user.ToLowerInvariant())
                    : (storage.Texts.ExternalUserInvalid, string.Empty, 0, string.Empty);

            case DatastoreKind.Ssh:
                var name = host?.Trim() ?? string.Empty;
                if (!PlainUser().IsMatch(user))
                {
                    return (storage.Texts.ExternalUserInvalid, string.Empty, 0, string.Empty);
                }

                if (name.Length == 0 || Uri.CheckHostName(name) == UriHostNameType.Unknown)
                {
                    return (storage.Texts.ExternalHostInvalid, string.Empty, 0, string.Empty);
                }

                var number = port ?? 22;
                return number is < 1 or > 65535
                    ? (storage.Texts.ExternalHostInvalid, string.Empty, 0, string.Empty)
                    : (null, name, number, user);

            default:
                throw new ArgumentException("Not an external datastore.", nameof(kind));
        }
    }

    public static string? CheckBorg(StorageContext storage, DatastoreKind kind, string remoteBorg)
    {
        var borg = remoteBorg.Trim();
        var valid = kind == DatastoreKind.HetznerStorageBox
            ? HetznerStorageBox.BorgVersions.Contains(borg)
            : PlainCommand().IsMatch(borg);
        return valid ? null : storage.Texts.ExternalBorgInvalid;
    }

    [GeneratedRegex("^[A-Za-z0-9._][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex PlainUser();

    // Runs on the other side through its shell: only characters every shell takes literally.
    [GeneratedRegex("^[A-Za-z0-9._/+-]{1,128}$")]
    private static partial Regex PlainCommand();
}
