using FusionRpg.Contracts;
using FusionRpg.Core.Saves;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>player-routing spec §1 - the seam between a content producer and the per-player
/// transport. Deliberately a plain interface, not <see cref="IHubContext{THub}"/> of
/// <see cref="RpgHub"/> directly, matching this program's own <see cref="IDelveLivePush"/>
/// precedent: a fake can capture every push with no live SignalR server needed.</summary>
public interface IPlayerPush
{
    /// <param name="saveId">The save (players.id, R17). Routes to group RpgConstants.PlayerGroup(saveId.Value).</param>
    void Push(SaveId saveId, string eventName, object payload);
}

/// <summary>The one production implementation - routes to <see cref="RpgConstants.PlayerGroup"/>,
/// never <see cref="RpgConstants.WebGroup"/> or <c>Clients.All</c> (player-routing spec Boundaries:
/// "Never: a content push to WebGroup or Clients.All"). Fire-and-forget, matching
/// <see cref="HubDelveLivePush"/>: a push is a notice about an ALREADY-durable write (the catch-up
/// GET is the correctness path for notify-service), so a dropped frame costs a live update only.</summary>
public sealed class HubPlayerPush : IPlayerPush
{
    readonly IHubContext<RpgHub> _hub;

    public HubPlayerPush(IHubContext<RpgHub> hub) => _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    public void Push(SaveId saveId, string eventName, object payload) =>
        _ = _hub.Clients.Group(RpgConstants.PlayerGroup(saveId.Value)).SendAsync(eventName, payload);
}
