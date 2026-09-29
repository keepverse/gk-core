using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Defence;

/// <summary>
/// T25/T26 (spec-defence-actions.md §3): `poise`'s three-part cost. A flat commit at raise, an
/// absorb drain proportional to what the guard stopped, and a per-tick hold while held — the third
/// component beyond spec-guard-economy.md's flat-commit-plus-absorb-drain, which priced guard as a
/// proc; a STANCE needs the per-tick hold specifically to satisfy the termination invariant (§2.2:
/// "no later layer can repair a pool that refills faster than it drains").
///
/// <para>A thin wrapper over T15's <see cref="ActorResourcePools.TrySpend"/> — never a second pool
/// mechanism. "A8 authors no damage math" (spec §0): <see cref="AbsorbDrainAmount"/> takes the
/// already-computed absorbed amount as an input; it does not compute what a guard stops, only what
/// stopping it costs.</para>
///
/// <para><b>battle-tempo `poise-unification` (2026-09-05):</b> this is now the ONLY `poise` cost
/// path — <c>Combat/Guard/PoiseRuntime.cs</c>, a second, private-pool implementation of the same
/// three-part cost built under class-system P7.1–P7.3, is deleted. Its own doc justified floor-at-zero
/// commits as a PS-8 requirement: <i>"a 'cannot afford to guard' refusal would be exactly [a hard
/// cap] in a different shape."</i> That is a misapplication — PS-8 forbids progression CEILINGS, not
/// affordability. <see cref="TryCommit"/>'s refuse below is the SAME contract `stamina` and `qi`
/// already use through this same <see cref="ActorResourcePools.TrySpend"/>, and nobody calls those
/// PS-8 violations. Refusing to pay is not a ceiling on a magnitude; it is what "cannot afford it"
/// means for every other resource in the game.</para>
/// </summary>
public static class PoiseLedger
{
    public const string ResourceId = "poise";

    /// <summary>Flat commit, paid once at raise. Ordinary <see cref="ActorResourcePools.TrySpend"/>
    /// semantics: false and no state change on insufficient poise — the caller refuses the raise via
    /// the normal affordability path, not a silent no-op (spec testing strategy: "raising with zero
    /// poise is refused by affordability, not by silence"). Unconditional across repeats: nothing
    /// about a prior commit's outcome changes what the next one costs.</summary>
    public static bool TryCommit(ActorResourcePools pools, long flatCommitAmount, long nowTick, ActorDerivedSnapshot derived) =>
        pools.TrySpend(ResourceId, flatCommitAmount, nowTick, derived);

    /// <summary>Per-tick hold, paid every tick while the stance is held. A failed pay here IS the
    /// termination brake (§2.2) — the caller ends the stance through the interrupt path exactly like
    /// any other `perTick` cost (T17's `CostLedger`), no special case for guard.</summary>
    public static bool TryPayHoldTick(ActorResourcePools pools, long perTickAmount, long nowTick, ActorDerivedSnapshot derived) =>
        pools.TrySpend(ResourceId, perTickAmount, nowTick, derived);

    /// <summary><c>absorbedAmount × drainRatioMilli / 1000</c> — widened before multiplying, divided
    /// by 1000 exactly once (docs/architecture/numeric-types.md "Numeric overflow"). <paramref name="drainRatioMilli"/> is a
    /// bounded ratio, never a magnitude a balance pass calibrates as a flat number.</summary>
    public static long AbsorbDrainAmount(long absorbedAmount, int drainRatioMilli)
    {
        if (absorbedAmount < 0) throw new ArgumentOutOfRangeException(nameof(absorbedAmount), absorbedAmount, "an absorbed amount is never negative");
        if (drainRatioMilli < 0) throw new ArgumentOutOfRangeException(nameof(drainRatioMilli), drainRatioMilli, "a drain ratio is never negative");
        return checked(absorbedAmount * drainRatioMilli / 1000);
    }

    /// <summary>
    /// Pays the absorb drain for one already-absorbed hit. <b>NOT all-or-nothing</b> — deliberately
    /// different from <see cref="TryCommit"/> and <see cref="TryPayHoldTick"/>, and this is a
    /// `poise-unification` correction (2026-09-05), not the original T25/T26 shape: paying for damage
    /// that has ALREADY happened is not a request that can be declined the way committing to guard or
    /// holding it is. Mirrors <c>ShieldRuntime.Absorb</c>'s own "never spend more than is there"
    /// contract exactly — drains up to what remains and returns the actual amount drained, which is
    /// less than the ideal share once the pool runs dry. The deleted <c>PoiseRuntime.Absorb</c> had
    /// this same graceful contract; routing it through <see cref="ActorResourcePools.TrySpend"/>'s
    /// all-or-nothing semantics (the pre-unification shape of this method) would have silently made
    /// every absorb free once poise ran low — a real defect this correction removes before anything
    /// called it.</summary>
    public static long PayAbsorbDrain(ActorResourcePools pools, long absorbedAmount, int drainRatioMilli, long nowTick, ActorDerivedSnapshot derived)
    {
        var ideal = AbsorbDrainAmount(absorbedAmount, drainRatioMilli);
        var current = pools.Resolve(ResourceId, nowTick, derived);
        var actual = Math.Min(ideal, current);
        if (actual > 0)
        {
            var spent = pools.TrySpend(ResourceId, actual, nowTick, derived);
            if (!spent)
                throw new InvalidOperationException("actual <= current by construction -- TrySpend must succeed");
        }
        return actual;
    }

    /// <summary>Guard broken — resource-hub-ssot.md §10: every resource except `hp` gets exhaustion on
    /// empty, never death. Delegates to <see cref="ExhaustionPolicy.IsExhausted"/>, the one place that
    /// question is answered for every resource, so `poise` reads the same rule `stamina`/`qi`/etc. do
    /// rather than a private threshold of its own.</summary>
    public static bool IsExhausted(ActorResourcePools pools, long nowTick, ActorDerivedSnapshot derived) =>
        ExhaustionPolicy.IsExhausted(pools.Resolve(ResourceId, nowTick, derived));
}
