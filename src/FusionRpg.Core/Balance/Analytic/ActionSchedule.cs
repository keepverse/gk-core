using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Cost;

namespace FusionRpg.Core.Balance.Analytic;

/// <summary>
/// class-system-todo.md P4.5 — the deterministic per-round action-selection walk.
///
/// <para><b>The action-cost system ships now, and the pool advance calls it.</b> The action program
/// closed 2026-09-07: <c>Actions/Cost/ResourcePoolState.cs</c> is the real lazy-regen gate — per-mille
/// <c>long</c>, carry-corrected, <c>checked</c> — and <c>ActorResourcePools</c> is its per-actor shell.
/// This walk's own advance goes through <see cref="ResourcePoolState.Settle"/> (see
/// <see cref="Advance"/>), not a local double clamp, so a rate with a sub-unit remainder compounds
/// exactly as the runtime accrues it. The priority-ordered affordability walk and the pay-on-commit
/// shape remain this module's — there is no batch-walk resolver to defer to — and
/// <c>gk-core/tools/CombatSim/ActionEconomy.cs</c> now calls <see cref="Choose"/> rather than mirroring it.
/// </para>
///
/// <para><b>No RNG here either</b> (spec-deterministic-core.md §5): which action fires each round is
/// fully determined by starting pool state, costs and priorities — only the swing's OWN outcome (miss,
/// parry, block, clean, crit — <see cref="StrikeMixture"/>) is probabilistic. This is a bounded,
/// deterministic walk, not a simulation; <paramref name="rounds"/> is the same kind of integration
/// bound <c>RoundLimit</c> is (spec-deterministic-core.md §5) — a ceiling on the computation, never a
/// balance parameter.</para>
///
/// <para><b>Order per round: regen, then choose, then pay.</b> <c>spec-action-costs.md</c> §2 frames a
/// pool as a continuous lazy function of elapsed time (<c>value(now) = clamp(stored + rate×Δt, 0,
/// max)</c>), not a discrete "tick then act" step order — advancing exactly one round's worth of that
/// function immediately before choosing is the reading closest to "what can I afford right now,
/// having last acted one round ago". Round 1 starts from whatever <paramref name="initialPools"/>
/// says (<c>ActorPools</c>'s own "a run starts full" — the caller's responsibility, not this
/// function's), so a full pool is unaffected by that first regen step (clamped at max already).</para>
/// </summary>
public static class ActionSchedule
{
    /// <summary>
    /// How the walk chooses, modelled after the shipped combat-ai profile
    /// (`docs/architecture/combat-ai/spec-action-schedule-twin.md` §2). <see cref="Greedy"/> is the
    /// IDENTITY: it reproduces the pre-combat-ai walk byte-for-byte, so every existing caller, test and
    /// baseline is unchanged by this module alone — which is the point, because changing the policy AND
    /// the expressiveness in one commit would make a moved number unattributable.
    ///
    /// <para><see cref="Tier"/> is CARRIED AND VALIDATED, never branched on: in a duel the smart tier's
    /// extra work is target selection over a candidate set, and a duel has one candidate, so the two
    /// tiers provably collapse to what this walk already does (spec §4). Carrying it makes the collapse
    /// a stated finding instead of an unmodelled second path.</para>
    ///
    /// <para><see cref="ReserveFloorMilli"/> is a per-mille floor on what REMAINS after paying, so a
    /// full pool is still spendable; it is a bounded ratio of a pool rather than a progression cap
    /// (`ssot-power-scale.md` §11), and it cannot starve the walk because the free fallback
    /// short-circuits before it is consulted — at every floor value, 1000 included.</para>
    /// </summary>
    public sealed record SchedulePolicy(
        string ProfileId,          // provenance only -- which combat-ai profile these rows express
        AiTier Tier,               // module 3's closed vocabulary; validated, never branched on
        long ReserveFloorMilli,    // default per-mille of Max that must remain after paying
        bool SkipOverkill)         // waste guard: no costed action when the free option already ends it
    {
        public static readonly SchedulePolicy Greedy = new("greedy", AiTier.Performance, 0, false);
    }

    public readonly record struct ActionOption(
        string Id, int Priority, double DamageMultiplier,
        string? CostResourceId, long CostShareOfOutputMilli,
        long ReserveFloorMilli = -1,   // -1 = "use the policy's default"; a per-row override
        int MinTargets = 1);           // area waste guard the duel domain cannot express -- see Walk

    public readonly record struct PoolState(double Value, double Max, double Regen, long CarryMilli = 0);

    public readonly record struct RoundOutcome(string ActionId, double DamageMultiplier);

    /// <summary>Nominal output a cost is priced against — NOT damage dealt: committing is what costs,
    /// so a miss pays in full (spec-action-costs.md §3; <c>ActionEconomy.cs</c>'s own
    /// <c>NominalOutput</c>, mirrored exactly).</summary>
    public static double NominalOutput(ActionOption a, double baseDamage) => baseDamage * a.DamageMultiplier;

    public static double CostOf(ActionOption a, double baseDamage) =>
        a.CostResourceId is null ? 0.0 : NominalOutput(a, baseDamage) * (a.CostShareOfOutputMilli / 1000.0);

    /// <param name="options">The action set. Must contain at least one action with
    /// <c>CostResourceId == null</c> — a free fallback (<c>ActionSet.Load</c>'s own validation,
    /// mirrored: "a dry actor has nothing to do" otherwise).</param>
    /// <param name="initialPools">Starting pool state per resource id, keyed the same way
    /// <see cref="ActionOption.CostResourceId"/> values are. A resource with no entry here reads as
    /// always-zero (never affordable) — callers must supply every resource any costed action names.</param>
    /// <param name="baseDamage">Nominal per-swing base damage — the same value that would otherwise go
    /// into <see cref="StrikeMixture.Compute"/> before this round's chosen action's multiplier is
    /// applied to it.</param>
    /// <param name="rounds">How many rounds to walk.</param>
    /// <param name="policy">How to choose. <c>null</c> (the default, and every pre-CAI2.3 call site's
    /// shape) means <see cref="SchedulePolicy.Greedy"/> — the identity.</param>
    /// <param name="fightEndsThisRound">Round index -> "the free option already finishes it". Only
    /// consulted when the policy sets <see cref="SchedulePolicy.SkipOverkill"/>; <c>null</c> means
    /// "never", which is the identity. The twin is pure and knows no HP, so the predicate is the
    /// caller's — `Predictor.MixedStrike` supplies it from the cumulative mean it already computes.</param>
    public static IReadOnlyList<RoundOutcome> Walk(
        IReadOnlyList<ActionOption> options, IReadOnlyDictionary<string, PoolState> initialPools,
        double baseDamage, int rounds,
        SchedulePolicy? policy = null,
        Func<int, bool>? fightEndsThisRound = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (initialPools is null) throw new ArgumentNullException(nameof(initialPools));
        if (options.Count == 0)
            throw new ArgumentException("must contain at least one action", nameof(options));
        if (!options.Any(a => a.CostResourceId is null))
            throw new ArgumentException(
                "must contain at least one free action (CostResourceId == null) -- a dry actor has nothing to do",
                nameof(options));
        if (double.IsNaN(baseDamage) || baseDamage < 0.0)
            throw new ArgumentOutOfRangeException(nameof(baseDamage), baseDamage, "must be non-negative");
        if (rounds < 0)
            throw new ArgumentOutOfRangeException(nameof(rounds), rounds, "must be non-negative");

        var schedule = policy ?? SchedulePolicy.Greedy;

        // §4: the tier is a closed vocabulary (module 3's `AiTier`). An unknown value throws, naming
        // it and the profile it came from -- never a silent fall-through to one of the two known ones.
        if (!Enum.IsDefined(typeof(AiTier), schedule.Tier))
            throw new ArgumentOutOfRangeException(
                nameof(policy), schedule.Tier,
                $"schedule policy '{schedule.ProfileId}' carries an out-of-vocabulary AiTier");

        // §3: `MinTargets > 1` is an AREA waste guard and this is a 1v1 duel model, so it throws ONCE
        // here rather than being silently ignored round after round. "Declared, not deferred" is this
        // repo's own discipline for an absence; a silently-dropped guard is the M4 failure arriving
        // from the other direction.
        for (var i = 0; i < options.Count; i++)
            if (options[i].MinTargets > 1)
                throw new ArgumentException(
                    $"option '{options[i].Id}' is gated on minTargets={options[i].MinTargets}; " +
                    "ActionSchedule is a DUEL model and cannot express an area waste guard. Model it in " +
                    "a multi-target predictor or exclude the row.",
                    nameof(options));

        var ordered = options.OrderBy(a => a.Priority).ToArray();
        var pools = new Dictionary<string, PoolState>(initialPools, StringComparer.Ordinal);
        var result = new List<RoundOutcome>(rounds);

        for (var round = 0; round < rounds; round++)
        {
            foreach (var id in pools.Keys.ToArray())
                pools[id] = Advance(pools[id]);

            var chosen = Choose(ordered, pools, baseDamage, schedule, round, fightEndsThisRound);
            Pay(chosen, pools, baseDamage);
            result.Add(new RoundOutcome(chosen.Id, chosen.DamageMultiplier));
        }

        return result;
    }

    /// <summary>
    /// One round of lazy accrual, through the SHIPPED gate rather than a local double clamp.
    /// <see cref="ResourcePoolState.Settle"/> takes per-mille <c>long</c> and returns the sub-unit
    /// remainder in <see cref="ResourcePoolState.Carry"/>, so a rate with a fraction below one
    /// per-mille compounds instead of being truncated every round. Public so <c>gk-core/tools/CombatSim</c>'s
    /// own pool copy can call it instead of a third clamp.
    /// </summary>
    public static PoolState Advance(PoolState p)
    {
        // Stored/max are whole units in this twin's model; ResourcePoolState's Stored/max are whole
        // units too, while its rate and carry are per-mille. So only Regen converts — with the same
        // AwayFromZero rounding ResourceChannelReader.Max/RegenPerMilleTick use at the real boundary.
        var stored = checked((long)Math.Round(p.Value, MidpointRounding.AwayFromZero));
        var max = checked((long)Math.Round(p.Max, MidpointRounding.AwayFromZero));
        var rateMilli = checked((long)Math.Round(p.Regen * 1000.0, MidpointRounding.AwayFromZero));
        var settled = new ResourcePoolState(stored, 0, p.CarryMilli).Settle(1, rateMilli, max);
        return p with { Value = settled.Stored, CarryMilli = settled.Carry };
    }

    public static ActionOption Choose(
        IReadOnlyList<ActionOption> ordered, IReadOnlyDictionary<string, PoolState> pools,
        double baseDamage, SchedulePolicy policy, int round, Func<int, bool>? fightEndsThisRound)
    {
        // §2 waste guard: when the free option already ends the fight this round, committing to a
        // costed action buys nothing. `SkipOverkill == false` (Greedy) never reads the predicate.
        var skipCosted = policy.SkipOverkill && fightEndsThisRound is not null && fightEndsThisRound(round);

        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];

            // The free fallback is never floored and never skipped: that is what keeps Walk's own
            // "a dry actor has nothing to do" validation true for every floor value.
            if (a.CostResourceId is null) return a;
            if (skipCosted) continue;

            pools.TryGetValue(a.CostResourceId, out var p);
            var cost = CostOf(a, baseDamage);
            var floorMilli = a.ReserveFloorMilli >= 0 ? a.ReserveFloorMilli : policy.ReserveFloorMilli;

            // A floor on what REMAINS, not on what is spent. At `Greedy`'s 0 this is exactly the
            // shipped `have >= cost`, so the identity row is byte-for-byte.
            //
            // ⚠ Cross-reference: this is the ideal §6.1 step 3 reading ("may not drop below a fraction of
            // its max after paying"). The SHIPPED seam (`Actions/Ai/ReserveFloorAffordability`) implements
            // the other reading — a pre-condition on the balance, which also refuses a zero-cost action.
            // CAI2.6 holds the ruling; the witness test in `ActionScheduleMatchesCorePolicyTests` pins both
            // behaviours meanwhile.
            if (p.Value - cost >= p.Max * (floorMilli / 1000.0)) return a;
        }

        // Unreachable given the constructor-level validation above (a free action always short-
        // circuits the loop before it exhausts) -- kept because ActionPolicy.Choose, the thing this
        // ports, keeps the identical defensive line.
        return ordered[^1];
    }

    static void Pay(ActionOption chosen, Dictionary<string, PoolState> pools, double baseDamage)
    {
        if (chosen.CostResourceId is null) return;
        var p = pools[chosen.CostResourceId];
        pools[chosen.CostResourceId] = p with { Value = p.Value - CostOf(chosen, baseDamage) };
    }
}
