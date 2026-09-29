using FusionRpg.Contracts;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

public sealed class SimHeartbeatHost : BackgroundService
{
    private readonly SimService _sim;
    private readonly RpgStore _store;
    private readonly IHubContext<RpgHub> _hub;
    private readonly ILogger<SimHeartbeatHost> _log;

    public SimHeartbeatHost(SimService sim, RpgStore store, IHubContext<RpgHub> hub, ILogger<SimHeartbeatHost> log)
    {
        _sim = sim;
        _store = store;
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_sim.Helloed) continue;
            _store.Heartbeat(RpgConstants.SourceSim);
            try
            {
                await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("Health", _store.ToHealth(true), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "sim heartbeat broadcast");
            }
        }
    }
}
