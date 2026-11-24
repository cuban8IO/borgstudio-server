using System.Diagnostics;

using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Infrastructure;
using BorgStudio.Server.Infrastructure.Ssh;

using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Tests.EndToEnd;

/// <summary>
/// External datastores against a real OpenSSH server with borg and rsync (CI sets one up on Linux):
/// host key scan, the server's own key, borg's version, folders, measuring and copying.
/// Runs only when BORGSTUDIO_REMOTE_TEST_USER names the account on localhost; otherwise it passes.
/// </summary>
public sealed class RemoteStorageTests : IDisposable
{
    private readonly string? user = Environment.GetEnvironmentVariable("BORGSTUDIO_REMOTE_TEST_USER");
    private readonly string work = Path.Combine(Path.GetTempPath(), "borgstudio-remote-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EverythingAnExternalDatastoreNeeds()
    {
        if (string.IsNullOrEmpty(user) || !OperatingSystem.IsLinux())
        {
            return;
        }

        var storage = new RemoteStorage(Options.Create(new SshOptions()), new ServerPaths(Path.Combine(work, "data")));
        var keys = await storage.ScanHostKeysAsync("localhost", 22, CancellationToken.None);
        var hostKey = keys.First(k => k.Algorithm == "ssh-ed25519");

        var datastoreId = Guid.NewGuid();
        var publicKey = await storage.PrepareKeyAsync(datastoreId, CancellationToken.None);
        Assert.StartsWith("ssh-ed25519 ", publicKey);
        await SudoAsync($"mkdir -p ~{user}/.ssh && echo '{publicKey}' >> ~{user}/.ssh/authorized_keys && chown -R {user} ~{user}/.ssh && chmod 700 ~{user}/.ssh && chmod 600 ~{user}/.ssh/authorized_keys");

        var target = new RemoteTarget(datastoreId, "localhost", 22, user, hostKey.PublicKey, "borg");
        Assert.StartsWith("borg 1.", await storage.CheckAsync(target, CancellationToken.None));

        var folder = Guid.NewGuid().ToString("N");
        Assert.False(await storage.ExistsAsync(target, folder, CancellationToken.None));
        await storage.CreateAsync(target, folder, CancellationToken.None);
        Assert.True(await storage.ExistsAsync(target, folder, CancellationToken.None));

        var local = Path.Combine(work, "upload");
        Directory.CreateDirectory(Path.Combine(local, "data", "0"));
        await File.WriteAllBytesAsync(Path.Combine(local, "data", "0", "1"), new byte[50_000]);
        await storage.UploadAsync(target, local, folder, CancellationToken.None);

        var sizes = await storage.MeasureAsync(target, [folder, "missing"], CancellationToken.None);
        Assert.InRange(sizes[folder], 50_000, 80_000);
        Assert.False(sizes.ContainsKey("missing"));
        var space = await storage.GetSpaceAsync(target, CancellationToken.None);
        Assert.True(space!.TotalBytes > space.FreeBytes);

        var back = Path.Combine(work, "download");
        Directory.CreateDirectory(back);
        await storage.DownloadAsync(target, folder, back, CancellationToken.None);
        Assert.Equal(50_000, new FileInfo(Path.Combine(back, "data", "0", "1")).Length);

        await storage.DeleteAsync(target, folder, CancellationToken.None);
        Assert.False(await storage.ExistsAsync(target, folder, CancellationToken.None));

        // Another host key than the trusted one is refused.
        var forged = target with { HostKey = keys.First(k => k.Algorithm != "ssh-ed25519").PublicKey.Replace("A", "B", StringComparison.Ordinal) };
        await Assert.ThrowsAsync<RemoteStorageException>(() => storage.CheckAsync(forged, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static async Task SudoAsync(string script)
    {
        var start = new ProcessStartInfo("sudo") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "sh", "-c", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
