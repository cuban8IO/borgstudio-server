namespace BorgStudio.Server.Domain.Settings;

/// <summary>Settings of this server that administrators can change. There is exactly one row.</summary>
public sealed class ServerSettings
{
    public const int SingletonId = 1;

    public const string DefaultServerName = "BorgStudio Server";

    public const int MaxServerNameLength = 64;

    public int Id { get; private set; } = SingletonId;

    /// <summary>Name shown in the app bar and the browser tab, e.g. "Backups at home".</summary>
    public string ServerName { get; private set; } = DefaultServerName;

    public void Rename(string? serverName)
    {
        var name = serverName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ServerName = DefaultServerName;
            return;
        }

        if (name.Length > MaxServerNameLength)
        {
            throw new ArgumentException($"The server name may have at most {MaxServerNameLength} characters.", nameof(serverName));
        }

        ServerName = name;
    }
}
