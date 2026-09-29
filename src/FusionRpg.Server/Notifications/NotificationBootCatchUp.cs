using FusionRpg.Data;
using Microsoft.Extensions.Hosting;

namespace FusionRpg.Server.Notifications;

/// <summary>notify-service spec §3 Triggers - closes the crash gap between a durable commit and its
/// notifications. Runs `WorldTurnNotificationPump.Run(..., Boot)` once, for each save's active map
/// world, so a reconnecting client is caught up (every batch it pushes is `CatchUp`, never toasted)
/// rather than silently missing whatever committed while the server was down.</summary>
public sealed class NotificationBootCatchUp : IHostedService
{
    readonly RpgStore _store;
    readonly WorldTurnNotificationPump _pump;

    public NotificationBootCatchUp(RpgStore store, WorldTurnNotificationPump pump)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var player in _store.ListPlayers())
        {
            var world = _store.GetActiveWorld(player.Id);
            if (world is null) continue;
            try
            {
                _pump.Run(world.WorldId, WorldTurnTrigger.Boot);
            }
            catch (Exception ex)
            {
                // A boot catch-up failure never blocks the server from starting.
                Console.Error.WriteLine($"[notify-boot] catch-up failed for world '{world.WorldId}': {ex.Message}");
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
