using System.Diagnostics;
using System.Globalization;
using System.Text;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Domain.Access;
using BorgStudio.Server.Domain.Storage;

using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Infrastructure.Ssh;

/// <summary>
/// External datastores through the system's OpenSSH and rsync: one key pair and one known_hosts
/// file per datastore in data/ssh/datastores/&lt;id&gt;, strict host key checking, and only plain
/// commands on the other side (a Storage Box has no real shell).
/// </summary>
internal sealed class RemoteStorage(IOptions<SshOptions> options, ServerPaths paths) : IRemoteStorage
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    private readonly SshOptions settings = options.Value;

    public static string Folder(ServerPaths paths, Guid datastoreId) =>
        Path.Combine(SshServer.Folder(paths), "datastores", datastoreId.ToString("N"));

    public static string KeyFile(ServerPaths paths, Guid datastoreId) => Path.Combine(Folder(paths, datastoreId), "id_ed25519");

    public async Task<IReadOnlyList<SshHostKey>> ScanHostKeysAsync(string host, int port, CancellationToken cancellationToken)
    {
        var result = await RunAsync(settings.SshKeyscanPath, ["-T", "10", "-p", port.ToString(CultureInfo.InvariantCulture), host], null, null, cancellationToken);
        var keys = new List<SshHostKey>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (line.StartsWith('#') || parts.Length < 3 || SshPublicKey.Parse($"{parts[1]} {parts[2]}") is not { } key)
            {
                continue;
            }

            if (keys.All(k => k.Fingerprint != key.Fingerprint))
            {
                keys.Add(new SshHostKey(key.Algorithm, key.Fingerprint, $"{key.Algorithm} {key.Blob}"));
            }
        }

        if (keys.Count == 0 && result.ExitCode != 0)
        {
            throw new RemoteStorageException(Reason(result));
        }

        return keys;
    }

    public async Task<string> PrepareKeyAsync(Guid datastoreId, CancellationToken cancellationToken)
    {
        var key = KeyFile(paths, datastoreId);
        if (!File.Exists(key))
        {
            CreatePrivate(Path.GetDirectoryName(key)!);
            var result = await RunAsync(
                settings.SshKeygenPath,
                ["-q", "-t", "ed25519", "-N", string.Empty, "-C", $"BorgStudio Server {datastoreId:N}", "-f", key],
                null,
                null,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new RemoteStorageException(Reason(result));
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return (await File.ReadAllTextAsync(key + ".pub", cancellationToken)).Trim();
    }

    public async Task InstallKeyAsync(RemoteTarget target, DatastoreKind kind, string password, CancellationToken cancellationToken)
    {
        var publicKey = await PrepareKeyAsync(target.DatastoreId, cancellationToken);
        WriteKnownHosts(target);

        // The password goes to ssh through its "askpass" program – this server program in a special
        // mode – and an environment variable, never through the command line.
        var arguments = new List<string>
        {
            "-F", NoConfig,
            "-p", target.Port.ToString(CultureInfo.InvariantCulture),
            "-o", "PubkeyAuthentication=no",
            "-o", "PreferredAuthentications=password,keyboard-interactive",
            "-o", "NumberOfPasswordPrompts=1",
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile={KnownHostsFile(target.DatastoreId)}",
            "-o", "ConnectTimeout=20",
            $"{target.UserName}@{target.Host}",
            kind == DatastoreKind.HetznerStorageBox
                ? HetznerStorageBox.InstallKeyCommand
                : "mkdir -p .ssh && chmod 700 .ssh && cat >> .ssh/authorized_keys && chmod 600 .ssh/authorized_keys",
        };
        var environment = new Dictionary<string, string>
        {
            ["SSH_ASKPASS"] = SshServer.ProgramPath,
            ["SSH_ASKPASS_REQUIRE"] = "force",
            ["DISPLAY"] = ":0",
            [SshConsole.AskPassModeVariable] = "1",
            [SshConsole.AskPassVariable] = password,
        };
        var result = await RunAsync(settings.SshPath, arguments, publicKey + "\n", environment, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RemoteStorageException(Reason(result));
        }
    }

    public async Task<string> CheckAsync(RemoteTarget target, CancellationToken cancellationToken)
    {
        var result = await RemoteAsync(target, [target.RemoteBorg, "--version"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RemoteStorageException(Reason(result));
        }

        return result.Output.Trim();
    }

    public async Task<bool> ExistsAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken)
    {
        var result = await RemoteAsync(target, ["ls", Plain(relativePath)], cancellationToken);
        if (result.ExitCode == 255)
        {
            throw new RemoteStorageException(Reason(result));
        }

        return result.ExitCode == 0;
    }

    public async Task CreateAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(target, relativePath, cancellationToken))
        {
            throw new IOException($"{relativePath} already exists.");
        }

        Check(await RemoteAsync(target, ["mkdir", Plain(relativePath)], cancellationToken));
    }

    public async Task DeleteAsync(RemoteTarget target, string relativePath, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(target, relativePath, cancellationToken))
        {
            Check(await RemoteAsync(target, ["rm", "-r", Plain(relativePath)], cancellationToken));
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> MeasureAsync(
        RemoteTarget target, IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken)
    {
        if (relativePaths.Count == 0)
        {
            return new Dictionary<string, long>();
        }

        // du also answers for the folders it finds when some are missing (and then exits with 1).
        var result = await RemoteAsync(target, ["du", "-sk", .. relativePaths.Select(Plain)], cancellationToken);
        if (result.ExitCode == 255)
        {
            throw new RemoteStorageException(Reason(result));
        }

        return ParseDu(result.Output);
    }

    public async Task<StorageSpace?> GetSpaceAsync(RemoteTarget target, CancellationToken cancellationToken)
    {
        var result = await RemoteAsync(target, ["df", "-k", "."], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RemoteStorageException(Reason(result));
        }

        return ParseDf(result.Output);
    }

    public async Task UploadAsync(RemoteTarget target, string localFolder, string relativePath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            settings.RsyncPath,
            ["-a", "-e", RsyncShell(target), localFolder.TrimEnd('/', '\\') + "/", $"{target.UserName}@{target.Host}:{Plain(relativePath)}/"],
            null,
            null,
            cancellationToken,
            timeout: Timeout.InfiniteTimeSpan);
        Check(result);
    }

    public async Task DownloadAsync(RemoteTarget target, string relativePath, string localFolder, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            settings.RsyncPath,
            ["-a", "-e", RsyncShell(target), $"{target.UserName}@{target.Host}:{Plain(relativePath)}/", localFolder.TrimEnd('/', '\\') + "/"],
            null,
            null,
            cancellationToken,
            timeout: Timeout.InfiniteTimeSpan);
        Check(result);
    }

    public void ForgetKey(Guid datastoreId)
    {
        var folder = Folder(paths, datastoreId);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The ssh command line that runs <paramref name="command"/> on the datastore with its key.</summary>
    public IReadOnlyList<string> SshArguments(RemoteTarget target, IEnumerable<string> command)
    {
        WriteKnownHosts(target);
        return
        [
            "-F", NoConfig,
            "-p", target.Port.ToString(CultureInfo.InvariantCulture),
            "-i", KeyFile(paths, target.DatastoreId),
            "-o", "IdentitiesOnly=yes",
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile={KnownHostsFile(target.DatastoreId)}",
            "-o", "ConnectTimeout=20",
            "-o", "ServerAliveInterval=30",
            $"{target.UserName}@{target.Host}",
            .. command,
        ];
    }

    public string SshPath => settings.SshPath;

    internal static IReadOnlyDictionary<string, long> ParseDu(string output)
    {
        var sizes = new Dictionary<string, long>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['\t', ' '], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
            {
                sizes[parts[1].Trim().TrimEnd('/')] = kilobytes * 1024;
            }
        }

        return sizes;
    }

    internal static StorageSpace? ParseDf(string output)
    {
        // "Filesystem 1K-blocks Used Available Use% Mounted on", then the values (maybe wrapped onto a second line).
        var numbers = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(word => long.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (long?)null)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .ToList();
        return numbers.Count >= 3 ? new StorageSpace(numbers[0] * 1024, numbers[2] * 1024) : null;
    }

    private string RsyncShell(RemoteTarget target)
    {
        var arguments = SshArguments(target, []);
        var ssh = new StringBuilder(Quote(settings.SshPath));
        foreach (var argument in arguments.Take(arguments.Count - 1))
        {
            ssh.Append(' ').Append(Quote(argument));
        }

        return ssh.ToString();
    }

    private Task<CommandResult> RemoteAsync(RemoteTarget target, IReadOnlyList<string> command, CancellationToken cancellationToken) =>
        RunAsync(settings.SshPath, SshArguments(target, command), null, null, cancellationToken);

    private void WriteKnownHosts(RemoteTarget target)
    {
        if (target.HostKey is null)
        {
            throw new RemoteStorageException("No host key is trusted for this datastore.");
        }

        var host = target.Port == 22 ? target.Host : $"[{target.Host}]:{target.Port}";
        var file = KnownHostsFile(target.DatastoreId);
        CreatePrivate(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"{host} {target.HostKey}\n");
    }

    private string KnownHostsFile(Guid datastoreId) => Path.Combine(Folder(paths, datastoreId), "known_hosts");

    private static string NoConfig => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>Paths on the other side are folder names BorgStudio chose; anything else would be a bug.</summary>
    private static string Plain(string relativePath) =>
        relativePath.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/') && !relativePath.StartsWith('-') && !relativePath.Contains("..")
            ? relativePath
            : throw new ArgumentException($"{relativePath} is not a plain folder name.", nameof(relativePath));

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static void Check(CommandResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new RemoteStorageException(Reason(result));
        }
    }

    private static string Reason(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Warning: Permanently added", StringComparison.Ordinal))
            .TakeLast(3);
        var reason = string.Join(" ", lines);
        return reason.Length > 0 ? reason : $"exit code {result.ExitCode}";
    }

    private static async Task<CommandResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string? input,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var timer = new CancellationTokenSource(timeout ?? CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new RemoteStorageException($"{program} did not start.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new RemoteStorageException($"{program} is not installed ({e.Message}).");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(linked.Token);
            var error = process.StandardError.ReadToEndAsync(linked.Token);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
            }

            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new RemoteStorageException("The connection timed out.");
            }

            return new CommandResult(process.ExitCode, await output, await error);
        }
    }

    private static void CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
