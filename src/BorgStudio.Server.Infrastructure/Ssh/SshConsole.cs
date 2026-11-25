using System.Diagnostics;
using System.Globalization;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Application.Access;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Infrastructure.Ssh;

/// <summary>
/// The two commands sshd runs, each as a short-lived process of the server program:
/// <list type="bullet">
/// <item><c>ssh-authorized-keys --data &lt;folder&gt; &lt;fingerprint&gt;</c> answers which key may log in
/// (one authorized_keys line, restricted to <c>borg-serve</c> for that key);</item>
/// <item><c>borg-serve --data &lt;folder&gt; &lt;key id&gt;</c> checks access, quota and moves, then runs
/// <c>borg serve</c> for exactly that key's repository.</item>
/// </list>
/// </summary>
public static class SshConsole
{
    public const string AuthorizedKeysCommand = "ssh-authorized-keys";

    public const string BorgServeCommand = "borg-serve";

    /// <summary>
    /// When ssh asks the server program for a password (installing the key on an external datastore),
    /// it sets this to "1"; the password itself is in <see cref="AskPassVariable"/>.
    /// </summary>
    public const string AskPassModeVariable = "BORGSTUDIO_ASKPASS_MODE";

    public const string AskPassVariable = "BORGSTUDIO_ASKPASS";

    /// <summary>Answers ssh's password question, if this is what the program was started for.</summary>
    public static bool TryAnswerPassword(TextWriter output)
    {
        if (Environment.GetEnvironmentVariable(AskPassModeVariable) != "1")
        {
            return false;
        }

        output.WriteLine(Environment.GetEnvironmentVariable(AskPassVariable) ?? string.Empty);
        return true;
    }

    public static bool Handles(string[] args) =>
        args.Length > 0 && (args[0] == AuthorizedKeysCommand || args[0] == BorgServeCommand);

    /// <summary>"--data &lt;folder&gt;" from the arguments (sshd passes no environment), and the remaining arguments.</summary>
    public static (string? DataDirectory, string[] Arguments) ParseArguments(string[] args)
    {
        string? data = null;
        var rest = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Length)
            {
                data = args[++i];
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        return (data, [.. rest]);
    }

    /// <summary>
    /// Prints the authorized_keys line for a key fingerprint, or nothing. Reads the database directly:
    /// sshd runs this for every login attempt, so it has to be quick.
    /// </summary>
    public static async Task<int> AuthorizedKeysAsync(string dataDirectory, string fingerprint, TextWriter output)
    {
        var paths = new ServerPaths(dataDirectory);
        if (!File.Exists(paths.DatabaseFile))
        {
            return 0;
        }

        var connection = new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Mode = SqliteOpenMode.ReadOnly }.ToString();
        await using var db = new SqliteConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT Id, Algorithm, Blob FROM SshKeys WHERE Fingerprint = $fingerprint";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var id = Guid.Parse(reader.GetString(0));
            await output.WriteLineAsync(AuthorizedKeyLine(SshServer.ProgramPath, paths.DataDirectory, id, reader.GetString(1), reader.GetString(2)));
        }

        return 0;
    }

    /// <summary>
    /// "restrict" turns off forwarding, terminals and the like; the forced command is the only thing
    /// the key can run. The paths go through the account's shell, hence the single quotes.
    /// </summary>
    public static string AuthorizedKeyLine(string program, string dataDirectory, Guid keyId, string algorithm, string blob) =>
        $"restrict,command=\"{ShellQuote(program)} {BorgServeCommand} --data {ShellQuote(dataDirectory)} {keyId:D}\" {algorithm} {blob}";

    /// <summary>Opens the session, runs borg serve with the client's input and output, and closes the session.</summary>
    public static async Task<int> BorgServeAsync(IServiceProvider services, Guid keyId, string borgPath, ServerPaths paths, TextWriter error)
    {
        if (!OperatingSystem.IsLinux())
        {
            await error.WriteLineAsync("BorgStudio Server: repositories can only be used on a server running on Linux.");
            return 1;
        }

        var dispatcher = services.GetRequiredService<IDispatcher>();
        var plan = await dispatcher.SendAsync(new OpenBorgSessionCommand(keyId, Environment.ProcessId));
        if (plan.Refusal is { } refusal)
        {
            await error.WriteLineAsync($"BorgStudio Server: {refusal}");
            return 1;
        }

        // The client names the repository "./<id>". On this server a link with that name in the
        // session's own working folder points to the real folder; borg resolves the link, so the
        // restriction to the repository holds and the address stays the same when the repository
        // moves or is renamed. On an external datastore the folder itself is named by the id, in
        // the account's home, and borg runs there through ssh.
        var sessionFolder = Path.Combine(SshServer.Folder(paths), "sessions", plan.SessionId.ToString("N"));
        var link = Path.Combine(sessionFolder, plan.RepositoryId.ToString("N"));
        long received = 0, sent = 0;
        int? exitCode = null;
        try
        {
            ProcessStartInfo start;
            if (plan.Remote is { } remote)
            {
                var remoteStorage = (RemoteStorage)services.GetRequiredService<IRemoteStorage>();
                start = new ProcessStartInfo(remoteStorage.SshPath);
                foreach (var argument in remoteStorage.SshArguments(remote, [remote.RemoteBorg, .. BorgArguments(plan)]))
                {
                    start.ArgumentList.Add(argument);
                }
            }
            else
            {
                Directory.CreateDirectory(sessionFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.CreateSymbolicLink(link, plan.RepositoryPath);
                var baseDirectory = Path.Combine(SshServer.Folder(paths), "borg");
                Directory.CreateDirectory(baseDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                start = new ProcessStartInfo(borgPath) { WorkingDirectory = sessionFolder };
                foreach (var argument in BorgArguments(plan))
                {
                    start.ArgumentList.Add(argument);
                }

                start.Environment["BORG_BASE_DIR"] = baseDirectory;
                start.Environment["LANG"] = "C.UTF-8";
            }

            // borg's protocol goes through this process, which counts the bytes in both directions;
            // its messages (standard error) go straight to the client.
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            using var borg = Process.Start(start) ?? throw new InvalidOperationException("borg did not start.");
            var toBorg = PumpAsync(Console.OpenStandardInput(), borg.StandardInput.BaseStream, bytes => received += bytes, closeTarget: true);
            var fromBorg = PumpAsync(borg.StandardOutput.BaseStream, Console.OpenStandardOutput(), bytes => sent += bytes, closeTarget: false);
            await borg.WaitForExitAsync();
            await fromBorg;
            exitCode = borg.ExitCode;

            // Reading the client's input may still wait for more; borg is done, so it is left behind.
            _ = toBorg;
            return borg.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await error.WriteLineAsync($"BorgStudio Server: borg could not be started: {exception.Message}");
            return 1;
        }
        finally
        {
            await dispatcher.SendAsync(new CloseBorgSessionCommand(plan.SessionId, received, sent, exitCode));
            try
            {
                // The link first, on its own: deleting it never touches the repository it points to.
                if (File.Exists(link) || Directory.Exists(link))
                {
                    Directory.Delete(link);
                }

                if (Directory.Exists(sessionFolder))
                {
                    Directory.Delete(sessionFolder);
                }
            }
            catch (IOException)
            {
                // Left over; harmless, and it does not grant access to anything.
            }
        }
    }

    /// <summary>Copies one direction of the connection, counting the bytes; writes go out at once (borg waits for them).</summary>
    private static async Task PumpAsync(Stream from, Stream to, Action<long> count, bool closeTarget)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read));
                await to.FlushAsync();
                count(read);
            }
        }
        catch (IOException)
        {
            // The other side went away; borg's exit tells what happened.
        }
        finally
        {
            if (closeTarget)
            {
                to.Close();
            }
        }
    }

    public static IReadOnlyList<string> BorgArguments(BorgSessionPlan plan)
    {
        var arguments = new List<string> { "serve", "--umask=077", "--restrict-to-repository", plan.RepositoryPath };
        if (plan.AppendOnly)
        {
            arguments.Add("--append-only");
        }

        if (plan.StorageQuotaBytes is { } quota)
        {
            arguments.Add("--storage-quota");
            arguments.Add(quota.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
