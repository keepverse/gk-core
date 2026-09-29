using FusionRpg.Contracts;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server.Gates;

/// <summary>
/// build-preset (BP1.1, spec-gate-services.md): the whole of `POST /api/patron/set` after its
/// request-shape validation, lifted out of `PatronEndpoints.cs`'s lambda so a build-preset
/// applier can call exactly what the player's own click calls -- store write, runtime refresh,
/// the web broadcasts, and the injector `patron.aura` push with its inbox fallback -- with one
/// implementation, not a second copy that could silently disagree (the 2026-09-07 equip-sync
/// class of defect this module exists to prevent). Behaviour is byte-identical to the endpoint
/// it replaces: reason strings and status-code mapping are the caller's job, not this service's.
/// </summary>
public sealed class PatronService
{
    readonly RpgStore _store;
    readonly IHubContext<RpgHub> _hub;
    readonly InjectorCommandInbox _inbox;

    public PatronService(RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox)
    {
        _store = store;
        _hub = hub;
        _inbox = inbox;
    }

    public async Task<PatronSetOutcome> SetAsync(long playerId, string instanceId, string correlationId)
    {
        var (ok, reason, patron) = _store.SetPatron(playerId, instanceId, correlationId);
        if (!ok) return new PatronSetOutcome(false, reason, null);

        PatronEndpoints.RefreshRuntimeState(_store);
        try
        {
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PatronUpdated", new { playerId });
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("SoulsUpdated", new { playerId });
            var cmd = PatronEndpoints.TryBuildPatronCommand(_store);
            if (cmd != null)
            {
                _inbox.Enqueue(cmd); // poll fallback -- the push may race a reconnect
                await _hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("Command", cmd);
            }
        }
        catch
        {
            // best-effort; the designation is durable and the injector inbox poll recovers
        }

        return new PatronSetOutcome(true, reason, patron);
    }
}

public sealed record PatronSetOutcome(bool Ok, string Reason, PatronRow? Patron);
