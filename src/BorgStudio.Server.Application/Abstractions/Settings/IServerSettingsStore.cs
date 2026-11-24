using BorgStudio.Server.Domain.Settings;

namespace BorgStudio.Server.Application.Abstractions.Settings;

public interface IServerSettingsStore
{
    Task<ServerSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken);
}
