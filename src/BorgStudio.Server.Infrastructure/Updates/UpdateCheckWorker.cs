using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Updates;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Infrastructure.Updates;

/// <summary>Looks for new releases shortly after start and then once a day.</summary>
internal sealed class UpdateCheckWorker(IDispatcher dispatcher, TimeProvider timeProvider, ILogger<UpdateCheckWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                try
                {
                    await dispatcher.SendAsync(new RunUpdateCheckCommand(), stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "The update check failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server stops.
        }
    }
}
