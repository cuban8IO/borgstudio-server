using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain;

namespace BorgStudio.Server.Application.Status;

/// <summary>Reads the version and runtime facts of this server.</summary>
public sealed record GetServerStatusQuery : IQuery<ServerStatus>;

public sealed record ServerStatus(
    ProductVersion Version,
    string OperatingSystem,
    string Runtime,
    DateTimeOffset StartedAt,
    TimeSpan Uptime);

internal sealed class GetServerStatusQueryHandler(IServerEnvironment environment, TimeProvider timeProvider)
    : IQueryHandler<GetServerStatusQuery, ServerStatus>
{
    public Task<ServerStatus> HandleAsync(GetServerStatusQuery query, CancellationToken cancellationToken)
    {
        var uptime = timeProvider.GetUtcNow() - environment.StartedAt;
        var status = new ServerStatus(
            ProductVersion.Parse(environment.InformationalVersion),
            environment.OperatingSystem,
            environment.Runtime,
            environment.StartedAt,
            uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime);
        return Task.FromResult(status);
    }
}
