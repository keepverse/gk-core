using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Cost;

public enum CostPayOutcome
{
    Paid,
    InsufficientFunds
}

/// <summary>
/// One frozen single-resource charge — the shape item module 24's equipment maintenance pays
/// (item/spec-equipment-activation.md §"Upkeep schedule and ordering"). It is deliberately NOT an
/// <see cref="ActionCostRow"/>: it holds exactly one legal actor resource and frozen `long` values,
/// and it reads no action row, rung, Theta scaling or RNG. The profile's own `UpkeepClause` is the
/// only producer of these values, which is what keeps a maintenance price a content fact rather than
/// a private power curve.
/// </summary>
public sealed record FrozenResourceCharge(string ResourceId, long Cost, long Reserve);

/// <summary><see cref="ShortfallResourceId"/> is set only on <see cref="CostPayOutcome.InsufficientFunds"/>
/// — the FIRST resource (in row order) validation found unaffordable, matching
/// <see cref="UsabilityReason.CannotAfford"/>'s own single-detail shape.</summary>
public readonly record struct CostPayResult(CostPayOutcome Outcome, string? ShortfallResourceId)
{
    public static readonly CostPayResult Success = new(CostPayOutcome.Paid, null);
    public static CostPayResult Shortfall(string resourceId) => new(CostPayOutcome.InsufficientFunds, resourceId);
}

/// <summary>
/// T17 (action-todo.md, spec-action-costs.md §3): validate all, then consume all — never partially.
/// "Rollback" reduces to "nothing is ever spent until every row has already been peeked affordable",
/// so a failure never needs undoing (<see cref="ActorResourcePools.TrySpend"/> is itself all-or-
/// nothing per pool, and this ledger never calls it before every row has cleared validation).
///
/// <para><b>Committing is what costs, not landing</b> (§3) — this ledger has no notion of whether an
/// action hits; a caller pays at <c>onCommit</c>, and again per resolve tick for a <c>perTick</c> row,
/// regardless of outcome.</para>
///
/// <para><b>Cost rides the rung</b> (§5): <c>cost(rung, Θ) = anchorCost(Θ) × costMulti(rung)</c>.
/// <c>costMulti(rung)</c> is <see cref="RungTable"/>'s already-shipped, already-authorized per-mille
/// multiplier (`A12`, no new formula). <c>anchorCost(Θ)</c> has **no row yet** in
/// ssot-power-scale.md §10's closed inventory — inventing one here would be exactly the private
/// <c>f(level)</c> AGENTS.md bans. <paramref name="thetaScaleMilliOf"/> is therefore a seam, the same
/// shape as <see cref="IAffordabilityCheck"/> itself: <c>null</c> (the default) is inert (1000‰, no
/// scaling), and the real anchor formula is a follow-up decision this module does not make for
/// itself. Cooldown never reads this seam at all — `A12`'s <c>CdMulti</c> is rung-only, by design
/// (§5: "cooldown rides the rung alone... never `Θ`").</para>
/// </summary>
public sealed class CostLedger : IAffordabilityCheck
{
    readonly IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> _costsByActionId;

    /// <summary>
    /// combat-ai `decision-perf` CAI1.14 (spec-decision-perf.md site 1): the rows for one
    /// `(actionId, timing)` pair, filtered ONCE here at construction instead of on every
    /// <see cref="RowsFor"/> call. `Check` is gate 3 of `UsabilityEvaluator`, so it runs for every
    /// candidate action of every decision, and the old body allocated a `List&lt;ActionCostRow&gt;`
    /// each time. Order is the authored row order within each pair, exactly as the filtered list was.
    /// </summary>
    readonly Dictionary<(string ActionId, ActionCostTiming When), IReadOnlyList<ActionCostRow>> _rowsByActionAndTiming = new();

    /// <summary>CAI1.14: `TryPay`'s reusable per-row amounts buffer, grown to the widest row set this
    /// battle ever pays and never shrunk. See `TryPay`'s own guard for why a re-entrant call throws.
    /// </summary>
    long[] _payScratch = Array.Empty<long>();
    bool _payInProgress;
    readonly Func<string, ActorResourcePools> _poolsFor;
    readonly Func<string, ActorDerivedSnapshot> _derivedFor;
    readonly Func<string, string, int> _rungOf;
    readonly Func<long> _nowTick;
    readonly Func<double, int> _thetaScaleMilliOf;

    public CostLedger(
        IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> costsByActionId,
        Func<string, ActorResourcePools> poolsFor,
        Func<string, ActorDerivedSnapshot> derivedFor,
        Func<string, string, int> rungOf,
        Func<long> nowTick,
        Func<double, int>? thetaScaleMilliOf = null)
    {
        _costsByActionId = costsByActionId ?? throw new ArgumentNullException(nameof(costsByActionId));
        foreach (var pair in _costsByActionId)
        {
            var rows = pair.Value;
            for (var when = ActionCostTiming.OnCommit; when <= ActionCostTiming.PerTick; when++)
            {
                var matching = new List<ActionCostRow>(rows.Count);
                for (var i = 0; i < rows.Count; i++)
                    if (rows[i].When == when)
                        matching.Add(rows[i]);

                _rowsByActionAndTiming[(pair.Key, when)] = matching.Count == 0 ? Array.Empty<ActionCostRow>() : matching;
            }
        }

        _poolsFor = poolsFor ?? throw new ArgumentNullException(nameof(poolsFor));
        _derivedFor = derivedFor ?? throw new ArgumentNullException(nameof(derivedFor));
        _rungOf = rungOf ?? throw new ArgumentNullException(nameof(rungOf));
        _nowTick = nowTick ?? throw new ArgumentNullException(nameof(nowTick));
        _thetaScaleMilliOf = thetaScaleMilliOf ?? (_ => 1000);
    }

    IReadOnlyList<ActionCostRow> RowsFor(string actionId, ActionCostTiming when) =>
        _rowsByActionAndTiming.TryGetValue((actionId, when), out var rows) ? rows : Array.Empty<ActionCostRow>();

    /// <summary>
    /// Deterministic, no roll — a caller may poll this every frame (a greyed-out button) without
    /// burning the actor's cost-roll rng stream. Uses <see cref="ValueSpec.Max"/> as the affordability
    /// bound: a spread cost never resolves ABOVE its own Max, so "affordable at Max" is never a false
    /// positive against the real, later-rolled amount.
    /// </summary>
    public UsabilityResult Check(string actorKey, string actionId)
    {
        var pools = _poolsFor(actorKey);
        var derived = _derivedFor(actorKey);
        var nowTick = _nowTick();
        var rung = _rungOf(actorKey, actionId);

        // CAI1.14 site 1 (third part): indexed `for`, never `foreach` — `RowsFor` is typed
        // `IReadOnlyList<T>`, so a `foreach` boxes an `IEnumerator<T>` on EVERY gate-3 call, which is
        // the per-decision allocation the row's Code style names by hand. The enumeration order is the
        // list's own, unchanged.
        var onCommit = RowsFor(actionId, ActionCostTiming.OnCommit);
        for (var i = 0; i < onCommit.Count; i++)
        {
            var row = onCommit[i];
            var bound = ScaleCost(row.AmountSpec.Max, rung, derived);
            if (pools.Resolve(row.ResourceId, nowTick, derived) < HpFloorAdjustedBound(row, bound))
                return UsabilityResult.Refuse(UsabilityReason.CannotAfford, row.ResourceId);
        }

        return UsabilityResult.Usable;
    }

    /// <summary>
    /// Validate every row for <paramref name="when"/>, then consume every row — never partially. The
    /// FIRST unaffordable resource (row order) is reported and NOTHING is spent, for either row.
    /// </summary>
    public CostPayResult TryPay(string actorKey, string actionId, ActionCostTiming when, AtomRng? rng)
    {
        var rows = RowsFor(actionId, when);
        if (rows.Count == 0) return CostPayResult.Success;

        // combat-ai `decision-perf` CAI1.14 (site 1's second half): the per-row amounts live in a
        // scratch array reused across calls instead of a fresh `new long[rows.Count]` on every
        // payment. A reused buffer is only safe if it cannot be shared, and here sharing would be
        // silent corruption rather than a wrong number: an inner call would overwrite the outer
        // call's pass-1 amounts, so pass 2 would spend figures pass 1 never validated — exactly the
        // state the two-pass shape exists to make impossible. Throws; this ledger is single-threaded
        // by contract, so re-entry is a caller bug with no correct fallback.
        if (_payInProgress)
            throw new InvalidOperationException(
                "CostLedger.TryPay is already in progress: its scratch buffer is not re-entrant.");
        _payInProgress = true;
        try
        {
            var pools = _poolsFor(actorKey);
            var derived = _derivedFor(actorKey);
            var nowTick = _nowTick();
            var rung = _rungOf(actorKey, actionId);

            // Grown, never shrunk: the high-water mark per ledger (one per battle) is bounded by the
            // widest authored cost row set, and every later call reuses it.
            if (_payScratch.Length < rows.Count) _payScratch = new long[rows.Count];
            var amounts = _payScratch;

            // Pass 1 — validate ALL, spend NONE. Resolving here (not re-resolving in pass 2) is what
            // guarantees pass 2 pays the EXACT amount pass 1 validated against, even for an OnApply
            // spread cost that would otherwise roll a second, different number on a second Resolve.
            for (var i = 0; i < rows.Count; i++)
            {
                var amount = ScaleCost(rows[i].AmountSpec.Resolve(rng), rung, derived);
                amounts[i] = amount;
                if (pools.Resolve(rows[i].ResourceId, nowTick, derived) < HpFloorAdjustedBound(rows[i], amount))
                    return CostPayResult.Shortfall(rows[i].ResourceId);
            }

            // Pass 2 — consume ALL. Every row already cleared validation against the SAME
            // nowTick/derived snapshot, so this cannot fail — TrySpend's own bool is asserted, not
            // branched on, because a false here would mean pass 1 and pass 2 disagreed about the
            // actor's own state mid-call, which never happens in this single-threaded ledger.
            for (var i = 0; i < rows.Count; i++)
            {
                var spent = pools.TrySpend(rows[i].ResourceId, amounts[i], nowTick, derived);
                System.Diagnostics.Debug.Assert(spent, "pass 2 spend failed after pass 1 validated it");
            }

            return CostPayResult.Success;
        }
        finally
        {
            _payInProgress = false;
        }
    }

    /// <summary>
    /// Peek whether a maintenance charge can be paid while leaving the profile's reserve intact,
    /// without spending anything. Same arithmetic as <see cref="TryPayProfileMaintenance"/> — one
    /// private core, so a caller's "is it affordable?" can never disagree with the payment's own
    /// validation. Module 24 needs this for reactivation, which requires affordability without
    /// charging (`spec-equipment-activation.md` §"Reactivation and HP").
    /// </summary>
    public bool CanPayProfileMaintenance(string actorKey, FrozenResourceCharge charge)
    {
        ValidateMaintenance(charge);
        return CanPayMaintenanceCore(actorKey, charge);
    }

    /// <summary>
    /// The ONE entry point a sustained equipment profile pays through: validate
    /// `current - cost >= max(reserve, hpFloor)` in checked arithmetic, then spend `cost` — never a
    /// partial debit and never a negative pool. Returns the existing typed shortfall result, so a
    /// caller reacts to a maintenance failure through exactly the shape an action cost failure uses.
    ///
    /// <para>The `hp` floor is <see cref="HpFloorAdjustedBound"/>'s own 1: HP maintenance is
    /// non-lethal by contract, and a charge that would take the pool to zero reads as a shortfall
    /// rather than as a kill.</para>
    /// </summary>
    public CostPayResult TryPayProfileMaintenance(string actorKey, FrozenResourceCharge charge)
    {
        ValidateMaintenance(charge);
        if (!CanPayMaintenanceCore(actorKey, charge)) return CostPayResult.Shortfall(charge.ResourceId);

        var pools = _poolsFor(actorKey);
        var derived = _derivedFor(actorKey);
        var nowTick = _nowTick();
        var spent = pools.TrySpend(charge.ResourceId, charge.Cost, nowTick, derived);
        System.Diagnostics.Debug.Assert(spent,
            "a maintenance spend failed after the identical validation passed in the same call");
        return CostPayResult.Success;
    }

    bool CanPayMaintenanceCore(string actorKey, FrozenResourceCharge charge)
    {
        var pools = _poolsFor(actorKey);
        var derived = _derivedFor(actorKey);
        var nowTick = _nowTick();
        var current = pools.Resolve(charge.ResourceId, nowTick, derived);
        var bound = charge.ResourceId == "hp" ? Math.Max(charge.Reserve, 1) : charge.Reserve;
        checked { return current - charge.Cost >= bound; }
    }

    /// <summary>A maintenance charge names one of the six registered actor resources — the pool owner
    /// is the deployment's, never a second ledger. An unknown id is a caller bug with no correct
    /// fallback, so it throws here rather than being coerced to an arbitrary pool.</summary>
    static void ValidateMaintenance(FrozenResourceCharge charge)
    {
        if (charge is null) throw new ArgumentNullException(nameof(charge));
        var known = false;
        for (var i = 0; i < DerivedStatChannels.ResourceIds.Count; i++)
            if (DerivedStatChannels.ResourceIds[i] == charge.ResourceId) { known = true; break; }
        if (!known)
            throw new ArgumentOutOfRangeException(nameof(charge), charge.ResourceId,
                "not one of the six registered actor resource ids");
        if (charge.Cost < 0)
            throw new ArgumentOutOfRangeException(nameof(charge), charge.Cost, "a maintenance cost is never negative");
        if (charge.Reserve < 0)
            throw new ArgumentOutOfRangeException(nameof(charge), charge.Reserve, "a reserve is never negative");
    }

    /// <summary>aura-skill T14 (`resource-hub-ssot.md`): an `hp` cost floors at 1 by default — the
    /// affordability bound is raised by exactly 1 so a payment that would leave the actor at 0 or
    /// below reads as unaffordable (`CannotAfford("hp")`), the same typed refusal every other
    /// shortfall already uses. A row that opted into lethality (<see cref="ActionCostRow.AllowLethal"/>)
    /// is untouched — its bound is the raw amount, exactly like every non-hp resource.</summary>
    static long HpFloorAdjustedBound(ActionCostRow row, long bound) =>
        row.ResourceId == "hp" && !row.AllowLethal ? bound + 1 : bound;

    long ScaleCost(int baseAmount, int rung, ActorDerivedSnapshot derived)
    {
        if (!RungPolicy.Table.TryResolve(rung, out var multipliers))
            throw new ArgumentOutOfRangeException(nameof(rung), rung, "no rung row for this action's rung");

        var theta = derived.Get(DerivedStatChannels.ProgressionPower);
        var afterRung = CurveTable.ApplyMilli(baseAmount, multipliers.CostMulti);
        return CurveTable.ApplyMilli(afterRung, _thetaScaleMilliOf(theta));
    }
}
