using BorgStudio.Server.Application;
using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Status;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BorgStudio.Server.Tests.Application;

public sealed class GetServerStatusTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReturnsVersionRuntimeAndUptime()
    {
        var clock = new FakeTimeProvider(Started.AddHours(2));
        await using var services = CreateServices(clock, "0.1.0+3f2a9c1d0e");

        await using var scope = services.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<IDispatcher>().QueryAsync(new GetServerStatusQuery());

        Assert.Equal("0.1.0", status.Version.Version);
        Assert.Equal("3f2a9c1", status.Version.Commit);
        Assert.Equal("Test OS", status.OperatingSystem);
        Assert.Equal(".NET Test", status.Runtime);
        Assert.Equal(Started, status.StartedAt);
        Assert.Equal(TimeSpan.FromHours(2), status.Uptime);
    }

    [Fact]
    public async Task UptimeIsNeverNegative()
    {
        var clock = new FakeTimeProvider(Started.AddMinutes(-1));
        await using var services = CreateServices(clock, "0.0.0-dev");

        await using var scope = services.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<IDispatcher>().QueryAsync(new GetServerStatusQuery());

        Assert.Equal(TimeSpan.Zero, status.Uptime);
    }

    private static ServiceProvider CreateServices(TimeProvider clock, string version)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton(clock);
        services.AddSingleton<IServerEnvironment>(new FakeEnvironment(version));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record FakeEnvironment(string InformationalVersion) : IServerEnvironment
    {
        public string OperatingSystem => "Test OS";

        public string Runtime => ".NET Test";

        public DateTimeOffset StartedAt => Started;
    }
}
