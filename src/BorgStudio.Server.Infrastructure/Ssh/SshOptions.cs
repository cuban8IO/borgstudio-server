namespace BorgStudio.Server.Infrastructure.Ssh;

/// <summary>The SSH service for borg, from "BorgStudio:Ssh" in borgstudio-server.json.</summary>
public sealed class SshOptions
{
    public const string SectionName = "BorgStudio:Ssh";

    /// <summary>On Linux it runs unless switched off; other platforms have none.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The server runs it under its own account, so the port has to be above 1023.</summary>
    public int Port { get; set; } = 2222;

    public string SshdPath { get; set; } = "/usr/sbin/sshd";

    public string SshKeygenPath { get; set; } = "/usr/bin/ssh-keygen";

    /// <summary>The borg program that serves the repositories; by default the one shipped with the server.</summary>
    public string? BorgPath { get; set; }

    public string ResolvedBorgPath => string.IsNullOrWhiteSpace(BorgPath)
        ? Path.Combine(AppContext.BaseDirectory, "borg", "borg.exe")
        : BorgPath;
}
