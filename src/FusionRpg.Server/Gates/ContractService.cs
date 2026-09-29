using FusionRpg.Contracts;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server.Gates;

/// <summary>
/// build-preset (BP1.2, spec-gate-services.md): the whole of `POST /api/contracts/bind` and
/// `/release` after their request-shape validation, lifted out of `ContractEndpoints.cs`'s
/// lambdas so a build-preset applier (the field piece) can call exactly what the player's own
/// click calls -- store write plus the `ContractsUpdated`/`SoulsUpdated` broadcast -- with one
/// implementation. `ritual` and `slots/buy` are unchanged: this module lifts only bind/release,
/// the two the field applier needs (spec-gate-services.md's own gate table). Behaviour is
/// byte-identical to the routes it replaces: reason strings and status-code mapping stay the
/// caller's job.
/// </summary>
public sealed class ContractService
{
    readonly RpgStore _store;
    readonly IHubContext<RpgHub> _hub;

    public ContractService(RpgStore store, IHubContext<RpgHub> hub)
    {
        _store = store;
        _hub = hub;
    }

    public async Task<ContractOutcome> BindAsync(long playerId, string instanceId)
    {
        var (ok, reason, contract) = _store.BindContract(playerId, instanceId);
        if (!ok) return new ContractOutcome(false, reason, null);
        await NotifyAsync(playerId);
        return new ContractOutcome(true, reason, contract);
    }

    public async Task<ContractOutcome> ReleaseAsync(long playerId, string instanceId)
    {
        var (ok, reason, contract) = _store.ReleaseContract(playerId, instanceId);
        if (!ok) return new ContractOutcome(false, reason, null);
        await NotifyAsync(playerId);
        return new ContractOutcome(true, reason, contract);
    }

    async Task NotifyAsync(long playerId)
    {
        try
        {
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("ContractsUpdated", new { playerId });
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("SoulsUpdated", new { playerId });
        }
        catch
        {
            // best-effort: the write is durable, the next read reconciles
        }
    }
}

public sealed record ContractOutcome(bool Ok, string Reason, ContractRow? Contract);
