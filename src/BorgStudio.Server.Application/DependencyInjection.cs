using System.Reflection;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Application.Messaging;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Application.Updates;
using BorgStudio.Server.Application.Users;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Application;

public static class DependencyInjection
{
    private static readonly Type[] HandlerInterfaces =
    [
        typeof(ICommandHandler<>),
        typeof(ICommandHandler<,>),
        typeof(IQueryHandler<,>),
    ];

    /// <summary>Registers the dispatcher and every command and query handler of the application layer.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDispatcher, Dispatcher>();
        services.AddScoped<UserAdministration>();
        services.AddSingleton<UpdateStatusStore>();
        services.AddScoped<StorageContext>();
        services.AddScoped<RepositoryOwners>();
        services.AddSingleton<RepositoryMoves>();
        services.AddScoped<SshEndpoints>();
        services.AddScoped<Statistics.StatisticsRecorder>();
        services.AddRequestHandlers(typeof(DependencyInjection).Assembly);
        return services;
    }

    /// <summary>Registers every non-abstract command and query handler in <paramref name="assembly"/> as scoped.</summary>
    public static IServiceCollection AddRequestHandlers(this IServiceCollection services, Assembly assembly)
    {
        var handlers = assembly.DefinedTypes
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false });

        foreach (var handler in handlers)
        {
            var interfaces = handler.ImplementedInterfaces
                .Where(i => i.IsGenericType && HandlerInterfaces.Contains(i.GetGenericTypeDefinition()));

            foreach (var handlerInterface in interfaces)
            {
                services.AddScoped(handlerInterface, handler);
            }
        }

        return services;
    }
}
