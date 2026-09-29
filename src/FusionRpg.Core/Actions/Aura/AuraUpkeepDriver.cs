using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions.Aura;

/// <summary>
/// aura-skill T14 (audit D4): the CALLER `CostLedger` never had. `CostLedger`/`ActorResourcePools`
/// already implement validate-all-then-consume-all `PerTick` payment correctly — D4's own framing is
/// that *"calls the existing mechanism" is true and useless* because nothing ever calls it. This is
/// that call, aura-scoped: charge one aura's `PerTick` cost list for one tick, and disable the aura
/// through the SAME typed, visible outcome shape eviction already uses (T13) when it cannot pay.
/// </summary>
public sealed record AuraUpkeepTickResult(bool Charged, bool Disabled, UsabilityReason? Reason, string? ResourceId)
{
    public static readonly AuraUpkeepTickResult Ok = new(true, false, null, null);

    public static AuraUpkeepTickResult Interrupted(string resourceId) =>
        new(false, true, UsabilityReason.CannotAfford, resourceId);
}

public sealed class AuraUpkeepDriver
{
    readonly CostLedger _ledger;

    /// <summary>An aura's upkeep rides the SAME `CostLedger` any other action's cost would — an aura
    /// id is simply the "action id" `CostLedger`'s own `costsByActionId` dictionary is keyed by. No
    /// second payment mechanism, no second validate-all-then-consume-all implementation.</summary>
    public AuraUpkeepDriver(CostLedger ledger) => _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <summary>Charges <paramref name="auraId"/>'s `PerTick` cost rows for <paramref name="actorKey"/>.
    /// On success, the aura stays active — the caller does nothing further. On a shortfall, the aura
    /// is disabled through <paramref name="runtime"/> (the SAME interrupt path a caller would use for
    /// any other typed refusal) and the result names which resource blocked it — never a silent
    /// deactivation. `CostLedger.TryPay` itself already enforces validate-all-then-consume-all and the
    /// hp-floors-at-1-unless-lethal rule (`ActionCostRow.AllowLethal`) — this method adds no payment
    /// logic of its own, only the react-to-the-outcome half.</summary>
    public AuraUpkeepTickResult ChargeTick(string actorKey, string auraId, AuraRuntime runtime, AtomRng? rng = null)
    {
        var result = _ledger.TryPay(actorKey, auraId, ActionCostTiming.PerTick, rng);
        if (result.Outcome == CostPayOutcome.Paid)
            return AuraUpkeepTickResult.Ok;

        runtime.Disable(auraId);
        return AuraUpkeepTickResult.Interrupted(result.ShortfallResourceId!);
    }
}
