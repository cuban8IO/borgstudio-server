using BorgStudio.Server.Application.Abstractions.Setup;
using BorgStudio.Server.Domain.Setup;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Infrastructure.Setup;

/// <summary>
/// Keeps the setup code in memory and in <c>data/setup-code.txt</c>, so it survives restarts until
/// setup is done, and announces it in the log (visible with <c>journalctl</c> or <c>docker logs</c>).
/// </summary>
internal sealed class SetupCodeStore(ServerPaths paths, ILogger<SetupCodeStore> logger) : ISetupCodeStore
{
    private readonly Lock gate = new();
    private string? current;

    public string? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        paths.EnsureDataDirectory();
        string? code = null;
        if (File.Exists(paths.SetupCodeFile))
        {
            code = (await File.ReadAllTextAsync(paths.SetupCodeFile, cancellationToken)).Trim();
        }

        if (SetupCode.Normalize(code).Length == 0)
        {
            code = SetupCode.Generate();
            await WriteProtectedAsync(paths.SetupCodeFile, code + Environment.NewLine, cancellationToken);
        }

        lock (gate)
        {
            current = code;
        }

        logger.LogWarning(
            "BorgStudio Server has no administrator yet. Open the web interface and enter this setup code: {SetupCode} "
            + "(it is also in {SetupCodeFile}).",
            code,
            paths.SetupCodeFile);
    }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            current = null;
        }

        if (File.Exists(paths.SetupCodeFile))
        {
            File.Delete(paths.SetupCodeFile);
        }

        return Task.CompletedTask;
    }

    private static async Task WriteProtectedAsync(string path, string content, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }
}
