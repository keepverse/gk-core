using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// `battle-tempo` `reaction-lane` RL2 (spec-reaction-lane.md §2.2a, decision 12 "Reading B"): the
/// counter's cost and payoff. **The spend IS the attack** — a counter commits `poise` through the
/// same <see cref="PoiseLedger"/> a guard would, and its damage is exactly
/// <see cref="Riposte.DamageFromSpentPoise"/> of what was spent. ⛔ **No fresh counter-damage path**
/// — `Riposte` ships, tested, and this module reuses it verbatim (spec: *"Riposte ships and is
/// tested"*).
///
/// <para>Deliberately NOT a method on <see cref="ReactionLane"/> itself — that class owns only the
/// slot/depth mechanism (its own header: *"never what a reaction does"*), and this is squarely what
/// a reaction DOES. A pure combining function over two already-shipped pieces
/// (<see cref="PoiseLedger"/>, <see cref="Riposte"/>), not a new mechanism of its own.</para>
/// </summary>
public static class ReactionCounter
{
    /// <summary>
    /// Attempts to commit <paramref name="poiseSpend"/> and, on success, converts it to damage via
    /// <see cref="Riposte.DamageFromSpentPoise"/>. All-or-nothing, matching
    /// <see cref="PoiseLedger.TryCommit"/>'s own contract: on refusal the pool is byte-for-byte
    /// unchanged and the returned damage is 0 — never a partial spend, never a partial hit.
    /// </summary>
    /// <param name="poiseSpend">How much `poise` this counter commits — the decision an actor (or its
    /// declaring policy) makes each time, not a fixed constant; sizing the range is RL3's own job.</param>
    /// <param name="riposteShareCapMilli">The bounded-ratio share <see cref="Riposte"/> converts at
    /// (PS-8 exempt — see that class's own comment).</param>
    /// <returns><c>(true, damage)</c> on a successful commit; <c>(false, 0)</c> when the actor cannot
    /// afford <paramref name="poiseSpend"/> — the caller's own typed `CannotAfford` refusal lives at
    /// the intent-source layer, per the spec's own "affordability is a selectability question and
    /// lives in the intent source" rule; this method only ever reports success or failure, it does
    /// not itself throw or refuse anything beyond what <see cref="PoiseLedger.TryCommit"/> already
    /// does.</returns>
    public static (bool Committed, long Damage) TryCounter(
        ActorResourcePools pools, long poiseSpend, int riposteShareCapMilli, long nowTick, ActorDerivedSnapshot derived)
    {
        if (!PoiseLedger.TryCommit(pools, poiseSpend, nowTick, derived))
            return (false, 0);

        return (true, Riposte.DamageFromSpentPoise(poiseSpend, riposteShareCapMilli));
    }
}
