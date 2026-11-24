using Microsoft.Extensions.Configuration;

namespace BorgStudio.Server.Infrastructure;

/// <summary>
/// Where the server keeps its data: by default a <c>data</c> folder next to the program, so an
/// installation is one self-contained folder. <c>BorgStudio:DataDirectory</c> (environment variable
/// <c>BorgStudio__DataDirectory</c>) moves it elsewhere.
/// </summary>
public sealed class ServerPaths
{
    public const string ConfigurationKey = "BorgStudio:DataDirectory";

    public ServerPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string DatabaseFile => Path.Combine(DataDirectory, "borgstudio-server.db");

    /// <summary>Keys that protect sign-in cookies; without them every restart signs everyone out.</summary>
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    public string SetupCodeFile => Path.Combine(DataDirectory, "setup-code.txt");

    /// <summary>The datastore a new server starts with.</summary>
    public string DefaultDatastoreDirectory => Path.Combine(DataDirectory, "repositories");

    public static ServerPaths FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];
        return new ServerPaths(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : configured);
    }

    /// <summary>Creates the data folder; on Linux only the server's own account may read it.</summary>
    public void EnsureDataDirectory()
    {
        if (Directory.Exists(DataDirectory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DataDirectory);
        }
        else
        {
            Directory.CreateDirectory(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
