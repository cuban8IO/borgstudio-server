using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Domain.Settings;

namespace BorgStudio.Server.Infrastructure.Persistence;

internal sealed class ServerSettingsStore(AppDbContext db) : IServerSettingsStore
{
    public async Task<ServerSettings> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ServerSettings.FindAsync([ServerSettings.SingletonId], cancellationToken);
        if (settings is null)
        {
            // The migration seeds the row; this only covers a database where it was deleted.
            settings = new ServerSettings();
            db.ServerSettings.Add(settings);
        }

        return settings;
    }

    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken) =>
        db.SaveChangesAsync(cancellationToken);
}
