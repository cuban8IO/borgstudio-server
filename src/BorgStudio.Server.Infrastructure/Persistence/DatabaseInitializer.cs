using BorgStudio.Server.Application.Abstractions.Setup;
using BorgStudio.Server.Domain.Users;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Infrastructure.Persistence;

/// <summary>Brings the database up to date at startup and prepares the setup wizard if needed.</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        services.GetRequiredService<ServerPaths>().EnsureDataDirectory();

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));

        var db = provider.GetRequiredService<AppDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Updating the database: {Migrations}", string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
        }

        var roles = provider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole(role));
            }
        }

        var setupCodes = provider.GetRequiredService<ISetupCodeStore>();
        if (await db.Users.AnyAsync(cancellationToken))
        {
            await setupCodes.RemoveAsync(cancellationToken);
        }
        else
        {
            await setupCodes.EnsureAsync(cancellationToken);
        }
    }
}
