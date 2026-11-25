using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Application.Users;
using BorgStudio.Server.Domain.Settings;

namespace BorgStudio.Server.Application.Settings;

public sealed record GetServerSettingsQuery : IQuery<ServerSettingsInfo>;

public sealed record ServerSettingsInfo(
    string ServerName, string? PublicUrl, bool CheckForUpdates, string? SshPublicAddress = null, bool ShowParticipantsInApps = true);

internal sealed class GetServerSettingsQueryHandler(IServerSettingsStore store)
    : IQueryHandler<GetServerSettingsQuery, ServerSettingsInfo>
{
    public async Task<ServerSettingsInfo> HandleAsync(GetServerSettingsQuery query, CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync(cancellationToken);
        return new ServerSettingsInfo(
            settings.ServerName, settings.PublicUrl, settings.CheckForUpdates, settings.SshPublicAddress, settings.ShowParticipantsInApps);
    }
}

/// <summary>Changes the server's name and public address (administrators only).</summary>
public sealed record UpdateServerSettingsCommand(
    [property: Required] string ActingUserId,
    [property: MaxLength(ServerSettings.MaxServerNameLength)] string? ServerName,
    string? PublicUrl,
    bool CheckForUpdates = true,
    bool ShowParticipantsInApps = true) : ICommand<AccountResult>;

internal sealed class UpdateServerSettingsCommandHandler(UserAdministration admin, IServerSettingsStore store)
    : ICommandHandler<UpdateServerSettingsCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(UpdateServerSettingsCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        if (!ServerSettings.TryNormalizePublicUrl(command.PublicUrl, out _))
        {
            return AccountResult.Failed(admin.Texts.InvalidPublicUrl);
        }

        var settings = await store.GetAsync(cancellationToken);
        settings.Rename(command.ServerName);
        settings.SetPublicUrl(command.PublicUrl);
        settings.CheckForUpdates = command.CheckForUpdates;
        settings.ShowParticipantsInApps = command.ShowParticipantsInApps;
        await store.SaveAsync(settings, cancellationToken);
        return AccountResult.Success;
    }
}
