using System.Diagnostics;
using System.Text;

using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Domain.Access;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Infrastructure.Ssh;

/// <summary>
/// Runs OpenSSH's sshd for borg as a child of the server, under the server's own account: no root,
/// no change to the system's SSH service. It only lets in the keys the database knows (asking the
/// server program for each login), and every key can only run borg for its one repository.
/// It is the same in an LXC container, as a Linux service and in Docker.
/// </summary>
internal sealed class SshServer(
    IOptions<SshOptions> options,
    ServerPaths paths,
    ILogger<SshServer> logger) : BackgroundService, ISshService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(30);

    private readonly SshOptions settings = options.Value;
    private volatile SshServiceStatus status = Initial(options.Value);

    public SshServiceStatus Status => status;

    /// <summary>The folder with sshd's configuration, host key and the borg session folders.</summary>
    public static string Folder(ServerPaths paths) => Path.Combine(paths.DataDirectory, "ssh");

    /// <summary>The program sshd runs for logins and borg connections: this server's own executable.</summary>
    public static string ProgramPath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BorgStudio.Server");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (status.State is SshServiceState.Unsupported or SshServiceState.Disabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Fail(exception.Message);
                logger.LogError("The SSH service for borg could not start: {Message}", exception.Message);
            }

            try
            {
                await Task.Delay(RestartDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (!File.Exists(settings.SshdPath))
        {
            throw new InvalidOperationException($"OpenSSH's server ({settings.SshdPath}) is not installed; the install script sets it up.");
        }

        var folder = Folder(paths);
        CreatePrivate(folder);
        var hostKey = await EnsureHostKeyAsync(folder, stoppingToken);
        var config = Path.Combine(folder, "sshd_config");
        await File.WriteAllTextAsync(config, Configuration(folder), stoppingToken);

        var start = new ProcessStartInfo(settings.SshdPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-D", "-e", "-f", config })
        {
            start.ArgumentList.Add(argument);
        }

        using var sshd = Process.Start(start) ?? throw new InvalidOperationException("sshd did not start.");
        status = status with { State = SshServiceState.Starting, HostKeys = [hostKey], Message = null };
        var lastLines = new Queue<string>();
        sshd.ErrorDataReceived += (_, e) => Log(e.Data, lastLines);
        sshd.OutputDataReceived += (_, e) => Log(e.Data, lastLines);
        sshd.BeginErrorReadLine();
        sshd.BeginOutputReadLine();

        // sshd says "Server listening on …" once it is ready.
        using var stop = stoppingToken.Register(() => Kill(sshd));
        await sshd.WaitForExitAsync(CancellationToken.None);
        stoppingToken.ThrowIfCancellationRequested();

        string why;
        lock (lastLines)
        {
            why = string.Join(" ", lastLines);
        }

        Fail($"sshd stopped (exit code {sshd.ExitCode}): {why}");
        logger.LogError("The SSH service for borg stopped with exit code {Code}: {Output}", sshd.ExitCode, why);
    }

    private void Log(string? line, Queue<string> lastLines)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (lastLines)
        {
            lastLines.Enqueue(line);
            while (lastLines.Count > 5)
            {
                lastLines.Dequeue();
            }
        }

        if (line.Contains("Server listening on", StringComparison.Ordinal))
        {
            status = status with { State = SshServiceState.Running, Message = null };
        }

        logger.LogInformation("sshd: {Line}", line);
    }

    private async Task<SshHostKey> EnsureHostKeyAsync(string folder, CancellationToken cancellationToken)
    {
        var key = Path.Combine(folder, "ssh_host_ed25519_key");
        if (!File.Exists(key))
        {
            var start = new ProcessStartInfo(settings.SshKeygenPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", string.Empty, "-C", "BorgStudio Server", "-f", key })
            {
                start.ArgumentList.Add(argument);
            }

            using var keygen = Process.Start(start) ?? throw new InvalidOperationException("ssh-keygen did not start.");
            var error = await keygen.StandardError.ReadToEndAsync(cancellationToken);
            await keygen.WaitForExitAsync(cancellationToken);
            if (keygen.ExitCode != 0)
            {
                throw new InvalidOperationException($"ssh-keygen failed: {error.Trim()}");
            }

            logger.LogInformation("Created the SSH host key {Key}.", key);
        }

        // sshd ignores a private key others can read, and ssh-keygen leaves the mode to the umask.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var publicKey = SshPublicKey.Parse(await File.ReadAllTextAsync(key + ".pub", cancellationToken))
            ?? throw new InvalidOperationException($"{key}.pub is not a valid public key.");
        return new SshHostKey(publicKey.Algorithm, publicKey.Fingerprint, $"{publicKey.Algorithm} {publicKey.Blob}");
    }

    /// <summary>
    /// sshd's configuration, written at every start. Only public keys, only this account, no
    /// shells, forwarding or terminals: each key may run nothing but borg for its repository.
    /// </summary>
    internal string Configuration(string folder)
    {
        var user = status.UserName;
        var builder = new StringBuilder();
        builder.AppendLine("# Written by BorgStudio Server at every start; changes here are overwritten.");
        builder.AppendLine("# Settings: \"BorgStudio:Ssh\" in borgstudio-server.json.");
        builder.AppendLine($"Port {settings.Port}");
        builder.AppendLine("ListenAddress 0.0.0.0");
        builder.AppendLine("ListenAddress ::");
        builder.AppendLine($"HostKey {Quote(Path.Combine(folder, "ssh_host_ed25519_key"))}");
        builder.AppendLine($"PidFile {Quote(Path.Combine(folder, "sshd.pid"))}");
        builder.AppendLine("AuthorizedKeysFile none");
        builder.AppendLine($"AuthorizedKeysCommand {Quote(ProgramPath)} {SshConsole.AuthorizedKeysCommand} --data {Quote(paths.DataDirectory)} %f");
        builder.AppendLine($"AuthorizedKeysCommandUser {user}");
        builder.AppendLine($"AllowUsers {user}");
        builder.AppendLine("PubkeyAuthentication yes");
        builder.AppendLine("PasswordAuthentication no");
        builder.AppendLine("KbdInteractiveAuthentication no");
        builder.AppendLine("UsePAM no");
        builder.AppendLine("PermitRootLogin no");
        builder.AppendLine("AllowTcpForwarding no");
        builder.AppendLine("AllowStreamLocalForwarding no");
        builder.AppendLine("AllowAgentForwarding no");
        builder.AppendLine("X11Forwarding no");
        builder.AppendLine("PermitTTY no");
        builder.AppendLine("PermitTunnel no");
        builder.AppendLine("PermitUserEnvironment no");
        builder.AppendLine("PrintMotd no");
        builder.AppendLine("ClientAliveInterval 30");
        builder.AppendLine("ClientAliveCountMax 6");
        builder.AppendLine("LogLevel INFO");
        return builder.ToString();
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private void Fail(string message) => status = status with { State = SshServiceState.Failed, Message = message };

    private static SshServiceStatus Initial(SshOptions options)
    {
        var state = !OperatingSystem.IsLinux() ? SshServiceState.Unsupported
            : !options.Enabled ? SshServiceState.Disabled
            : SshServiceState.Starting;
        return new SshServiceStatus(state, options.Port, Environment.UserName, []);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
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
}
