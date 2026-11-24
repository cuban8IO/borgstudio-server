namespace BorgStudio.Server.Application.Abstractions;

/// <summary>Facts about the running server process and the machine it runs on.</summary>
public interface IServerEnvironment
{
    /// <summary>Informational version of the server build, e.g. <c>0.1.0+3f2a9c1d…</c>.</summary>
    string InformationalVersion { get; }

    /// <summary>Operating system description, e.g. <c>Debian GNU/Linux 13</c>.</summary>
    string OperatingSystem { get; }

    /// <summary>.NET runtime description, e.g. <c>.NET 10.0.1</c>.</summary>
    string Runtime { get; }

    /// <summary>When the server process started.</summary>
    DateTimeOffset StartedAt { get; }
}
