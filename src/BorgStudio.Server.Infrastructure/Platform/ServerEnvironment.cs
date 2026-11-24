using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

using BorgStudio.Server.Application.Abstractions;

namespace BorgStudio.Server.Infrastructure.Platform;

internal sealed class ServerEnvironment : IServerEnvironment
{
    public ServerEnvironment()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ServerEnvironment).Assembly;
        InformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? string.Empty;

        using var process = Process.GetCurrentProcess();
        StartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    public string InformationalVersion { get; }

    public string OperatingSystem => RuntimeInformation.OSDescription;

    public string Runtime => RuntimeInformation.FrameworkDescription;

    public DateTimeOffset StartedAt { get; }
}
