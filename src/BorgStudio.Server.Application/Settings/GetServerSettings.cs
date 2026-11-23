using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Settings;

namespace BorgStudio.Server.Application.Settings;

public sealed record GetServerSettingsQuery : IQuery<ServerSettingsInfo>;

public sealed record ServerSettingsInfo(string ServerName);

internal sealed class GetServerSettingsQueryHandler(IServerSettingsStore store)
    : IQueryHandler<GetServerSettingsQuery, ServerSettingsInfo>
{
    public async Task<ServerSettingsInfo> HandleAsync(GetServerSettingsQuery query, CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync(cancellationToken);
        return new ServerSettingsInfo(settings.ServerName);
    }
}
