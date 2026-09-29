using FusionRpg.Core.Actions;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, spec-profile-schema.md §2): per-resource-id reserve floor, a
/// fraction of the pool's own max. Declared HERE (module 1) rather than in module 2's profile file,
/// matching the same positional-projection precedent `ScoringWeights`/`SelectionPolicy` already
/// established: module 1's mechanism needs the type to compile before module 2 (which depends on
/// module 1) exists, and module 2 reuses this record verbatim rather than re-declaring it — one type,
/// not two.
/// </summary>
public sealed record AiReserveFloor(string ResourceId, int FloorMilliOfMax);

/// <summary>
/// combat-ai `core-scorer` (module 1, spec-core-scorer.md §5 step 3): a DECORATOR on the real
/// <see cref="IAffordabilityCheck"/> (gate 3 of <see cref="UsabilityEvaluator"/>), never a fourth gate
/// — refuses when a reserved pool is already at or below its floor. Structurally exempt:
/// <see cref="ActionKind.Basic"/> — starving the basic attack is the hoarding failure a reserve floor
/// exists to prevent, not to cause. Wrapping (rather than folding the floor into the authored cost
/// table) keeps ONE cost authority: this decorator never computes a cost itself, only reads pool
/// state through the same callbacks a real production caller already has.
///
/// <para><b>The gate is action-agnostic, deliberately.</b> It refuses ANY non-basic action once a
/// watched pool is at or below its floor, rather than asking "does THIS action spend that resource" —
/// answering that would need this decorator to read the action's own cost rows, which is `CostLedger`'s
/// private knowledge, not the AI's to re-derive (ideal §3 principle 6: one cost authority). A reserve
/// floor is a coarse guard against hoarding-adjacent starvation, not a precise per-action budget.</para>
///
/// <para><b>⚠ The rule, as RULED by CAI2.6 on 2026-09-23 — state it once, here and in the spec.</b>
/// The authority is <c>docs/architecture/combat-ai-ideal.md:318</c> (the ideal's §6.1 step 3):
/// *"the reserve floor: a pool may not drop below a fraction of its max <b>after paying</b>"* — the
/// post-payment reading, quoted verbatim by `spec-action-schedule-twin.md` §2. This class previously
/// implemented the PRE-condition reading (`current &lt;= floor`, checked before the action's own cost,
/// and therefore refusing a zero-cost action at or below the floor so the actor idled). That reading
/// was the defective side: a floor is a floor on what REMAINS, and a floor that can starve an actor's
/// free option is the hang the twin's own comment calls out. So the comparison is now
/// <c>currentBalanceOf(resourceId) - costOf(actorKey, actionId, resourceId) &lt; floor</c> — equal to the
/// floor is fine, because the pool does not drop BELOW it — and a zero-cost action is admitted
/// everywhere, which is what stops the idling.</para>
///
/// <para><b>The cost comes from the one cost authority, never from here.</b> The decorator still
/// computes no cost itself: <see cref="ActionCostOf"/> is a read the composer supplies from the same
/// <see cref="CostLedger"/>-shaped source it already wraps (ideal §3 principle 6). Absent, the cost is
/// <c>0</c>, which is the ruled rule with a cost-free action — never a silent fallback to
/// the old pre-condition, which no longer exists in this file.</para>
///
/// <para><b>This decorator is also the implementer of `intent-router`'s `poise` contract</b>
/// (module 4, spec-intent-router.md §4): the `poise` reserve floor is
/// <c>max(profileFloor, reactionPoiseSpend × reactionsPerRoundExpected)</c> — the engine counters
/// whenever `poise` pays, consulting no policy (`Battle/Timeline/ReactionCounter.cs`), so a policy
/// that spent its whole poise pool on its own turn would have nothing left when the engine counters.
/// Both reaction arguments default to 0, making the max a no-op and this decorator byte-identical
/// until module 4 supplies real values. Only the `poise` id is boosted this way; every other floor is
/// the authored value unchanged.</para>
/// </summary>
public sealed class ReserveFloorAffordability : IAffordabilityCheck
{
    /// <summary>
    /// One action's own cost in one resource's units, read from the ONE cost authority the composer
    /// already holds (a <c>CostLedger</c>-shaped read at the holder's effective rung). Deliberately a
    /// read and not a computation: this decorator must never grow a second pricing path.
    /// </summary>
    public delegate long ActionCostOf(string actorKey, string actionId, string resourceId);

    readonly IAffordabilityCheck _inner;
    readonly IReadOnlyDictionary<string, int> _authoredFloorMilliByResourceId;
    readonly Func<string, ActionKind> _kindOf;
    readonly Func<string, long> _currentBalanceOf;
    readonly Func<string, long> _maxOf;
    readonly long _reactionPoiseSpend;
    readonly int _reactionsPerRoundExpectedMilli;
    readonly ActionCostOf? _costOf;

    const string PoiseResourceId = "poise";

    public ReserveFloorAffordability(
        IAffordabilityCheck inner,
        IReadOnlyList<AiReserveFloor> floors,
        Func<string, ActionKind> kindOf,
        Func<string, long> currentBalanceOf,
        Func<string, long> maxOf,
        long reactionPoiseSpend = 0,
        int reactionsPerRoundExpectedMilli = 0,
        ActionCostOf? costOf = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _kindOf = kindOf ?? throw new ArgumentNullException(nameof(kindOf));
        _currentBalanceOf = currentBalanceOf ?? throw new ArgumentNullException(nameof(currentBalanceOf));
        _maxOf = maxOf ?? throw new ArgumentNullException(nameof(maxOf));
        _reactionPoiseSpend = reactionPoiseSpend;
        _reactionsPerRoundExpectedMilli = reactionsPerRoundExpectedMilli;
        _costOf = costOf;

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (floors is not null)
            foreach (var floor in floors)
                map[floor.ResourceId] = floor.FloorMilliOfMax;
        _authoredFloorMilliByResourceId = map;
    }

    /// <summary>The effective floor, in absolute units of the resource's own pool — the authored
    /// per-mille-of-max value converted against the actor's real max, then (for `poise` only) raised
    /// to the expected reaction spend if that is higher. `Both_reaction_arguments_zero_leaves_every_
    /// floor_at_its_authored_value`: at `reactionPoiseSpend`/`reactionsPerRoundExpectedMilli` both 0
    /// the expected spend is 0, so the max is always the authored value.</summary>
    public long EffectiveFloorAbsolute(string resourceId)
    {
        var authoredMilli = _authoredFloorMilliByResourceId.TryGetValue(resourceId, out var m) ? m : 0;
        var authoredAbsolute = checked(authoredMilli * _maxOf(resourceId)) / 1000;
        if (resourceId != PoiseResourceId) return authoredAbsolute;

        var expectedReactionSpend = checked(_reactionPoiseSpend * _reactionsPerRoundExpectedMilli) / 1000;
        return Math.Max(authoredAbsolute, expectedReactionSpend);
    }

    public UsabilityResult Check(string actorKey, string actionId)
    {
        var inner = _inner.Check(actorKey, actionId);
        if (!inner.IsUsable) return inner;
        if (_kindOf(actionId) == ActionKind.Basic) return inner; // structurally exempt

        // Walk the closed, deterministically-ordered six resource ids (never `_authoredFloorMilliByResourceId.Keys`
        // -- a Dictionary's own enumeration order is not a determinism-safe order, and the action
        // layer's own purity guard bans it outright). Poise is included even when unauthored: module
        // 4's reaction reservation is an engine-side fact, checkable whatever a profile says.
        // The RULED rule (CAI2.6, 2026-09-23; decided by combat-ai-ideal.md:318 §6.1 step 3): the floor
        // binds what REMAINS after paying. Equal to the floor is admitted -- the pool does not drop
        // BELOW it -- so a zero-cost action passes at the floor and the actor never idles.
        var resourceIds = DerivedStatChannels.ResourceIds;
        for (var i = 0; i < resourceIds.Count; i++)
        {
            var resourceId = resourceIds[i];
            var floor = EffectiveFloorAbsolute(resourceId);
            if (floor <= 0) continue; // no floor authored (and, for poise, no reaction boost either)
            var cost = _costOf?.Invoke(actorKey, actionId, resourceId) ?? 0L;
            if (_currentBalanceOf(resourceId) - cost < floor)
                return UsabilityResult.Refuse(UsabilityReason.CannotAfford, resourceId);
        }

        return inner;
    }
}
