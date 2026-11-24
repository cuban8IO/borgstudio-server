using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Application.Abstractions.Updates;
using BorgStudio.Server.Domain;

namespace BorgStudio.Server.Infrastructure.Updates;

/// <summary>CHANGELOG.md, embedded into this assembly at build time.</summary>
internal sealed class EmbeddedChangelog : IChangelogSource
{
    private static readonly Lazy<string> Text = new(() =>
    {
        using var stream = typeof(EmbeddedChangelog).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public string Read() => Text.Value;
}

/// <summary>Tells the installation methods apart by the traces each one leaves.</summary>
internal sealed class InstallationInfo(IServerEnvironment environment) : IInstallationInfo
{
    public InstallationKind Kind
    {
        get
        {
            if (ProductVersion.Parse(environment.InformationalVersion).IsDevelopmentBuild)
            {
                return InstallationKind.Development;
            }

            if (string.Equals(System.Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return InstallationKind.Docker;
            }

            if (OperatingSystem.IsWindows())
            {
                return InstallationKind.WindowsService;
            }

            // lxc-install.sh sets up /usr/bin/update; install.sh alone does not.
            if (File.Exists("/usr/bin/update") && File.ReadAllText("/usr/bin/update").Contains("lxc-install.sh", StringComparison.Ordinal))
            {
                return InstallationKind.Lxc;
            }

            return OperatingSystem.IsLinux() ? InstallationKind.LinuxService : InstallationKind.Unknown;
        }
    }
}
