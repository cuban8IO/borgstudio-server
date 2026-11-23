using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Infrastructure.Platform;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BorgStudio.Server.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the implementations of the application layer's interfaces.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IServerEnvironment, ServerEnvironment>();
        return services;
    }
}
