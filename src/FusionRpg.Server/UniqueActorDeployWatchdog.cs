using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>W5-D: fail-closed Deploying timeout tick (no Hot coupling).</summary>
public sealed class UniqueActorDeployWatchdog : BackgroundService
{
    readonly UniqueActorService _unique;
    readonly ILogger<UniqueActorDeployWatchdog> _log;
    readonly TimeSpan _interval;
    readonly TimeSpan _timeout;

    public UniqueActorDeployWatchdog(
        UniqueActorService unique,
        ILogger<UniqueActorDeployWatchdog> log,
        IConfiguration? config = null)
    {
        _unique = unique;
        _log = log;
        var seconds = 5;
        var timeoutSec = 30;
        if (config != null)
        {
            seconds = config.GetValue("UniqueActor:DeployWatchIntervalSeconds", 5);
            timeoutSec = config.GetValue("UniqueActor:DeployTimeoutSeconds", 30);
        }
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
        _timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = _unique.FailExpiredDeploys(_timeout);
                if (n > 0)
                    _log.LogInformation("UniqueActor deploy timeout: failed {Count} Deploying → Roster", n);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "UniqueActor deploy watchdog tick failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
