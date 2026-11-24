using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Storage;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Infrastructure.Storage;

/// <summary>
/// Measures the repositories and empties expired trash every 15 minutes, and carries out moves to
/// other datastores as soon as they are requested (moves cut short by a restart start over).
/// </summary>
internal sealed class StorageWorker(
    IDispatcher dispatcher,
    RepositoryMoves moves,
    TimeProvider timeProvider,
    ILogger<StorageWorker> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Moves that wait for a running backup are tried again this often.</summary>
    private static readonly TimeSpan MoveRetry = TimeSpan.FromMinutes(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(MaintainAsync(stoppingToken), MoveAsync(stoppingToken));

    private async Task MaintainAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                await RunAsync(new RunStorageMaintenanceCommand(), "Measuring the repositories", stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server stops.
        }
    }

    private async Task MoveAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Unfinished moves from before a restart first, then whatever is requested.
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunAsync(new RunRepositoryMovesCommand(), "Moving repositories", stoppingToken);
                await moves.WaitAsync(MoveRetry, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server stops.
        }
    }

    private async Task RunAsync(ICommand command, string what, CancellationToken stoppingToken)
    {
        try
        {
            await dispatcher.SendAsync(command, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "{What} failed.", what);
        }
    }
}
