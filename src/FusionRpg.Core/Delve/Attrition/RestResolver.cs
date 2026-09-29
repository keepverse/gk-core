using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Attrition;

/// <summary>
/// `delve-attrition` D2.20 (spec-delve-attrition.md §5) — one member's outcome at a `rest` room:
/// pools healed, nerve stacks relieved, camp-action activations granted.
///
/// <para><b>Ambushed is a seam, not a decision.</b> An ambush is an `event-deck` row drawn at
/// `rest.ambushMilli` on the room's own reserved stream (`spec-delve-graph-roll.md:130-132`) — the
/// deck owns the draw, this module only owns the field a caller writes that draw's result into, so a
/// heal/relief/activations result and an ambush outcome always travel together in one value rather
/// than two parallel returns a caller could merge inconsistently.</para>
/// </summary>
public sealed record RestOutcome(int Activations, bool Ambushed = false);

/// <summary>The full result of one member resting: healed pools (all six, `SettleAll`'s own shape)
/// plus the relieved nerve stack count. Re-projecting stacks to a live `nerve.*` status (`NerveLadder`
/// / `NervePolicy`) is the next room's battle setup's job, not this one's — this record is party
/// state, not runtime state.</summary>
public sealed record RestResult(IReadOnlyDictionary<string, long> Pools, int NerveStacks, RestOutcome Outcome);

public static class RestResolver
{
    /// <summary>
    /// Heals every pool named in <paramref name="healsPools"/> by
    /// <c>max(pool) × attritionRestHealMilli × rung.RestHealMultMilli / 1_000_000</c> — the same
    /// two-‰-factors-then-divide-once shape as <see cref="HungerCharge.ForRoom"/>, added rather than
    /// set, through <see cref="ActorResourcePools.Add"/> so the pool's own <c>[0, max]</c> rail is
    /// what stops it, never a second clamp here. A rung with a reduced <c>RestHealMultMilli</c> (e.g.
    /// `750` on `very-hard`+) means the heal itself comes back smaller — rest never refills to max
    /// what the rung reduced.
    /// </summary>
    public static IReadOnlyDictionary<string, long> Heal(
        IReadOnlyDictionary<string, long> pools,
        IReadOnlyList<string> healsPools,
        long attritionRestHealMilli,
        DifficultyRungTuning rung,
        ActorDerivedSnapshot derived,
        long atTick)
    {
        if (pools is null) throw new ArgumentNullException(nameof(pools));
        if (healsPools is null) throw new ArgumentNullException(nameof(healsPools));
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (derived is null) throw new ArgumentNullException(nameof(derived));

        var actorPools = ActorResourcePools.FromStored(pools, atTick);
        foreach (var id in healsPools)
        {
            var max = ResourceChannelReader.Max(derived, id);
            long healAmount;
            checked { healAmount = max * attritionRestHealMilli * rung.RestHealMultMilli / 1_000_000; }
            actorPools.Add(id, healAmount, atTick, derived);
        }

        return actorPools.SettleAll(atTick, derived);
    }

    /// <summary>Nerve stacks drop by <paramref name="restRelief"/>, floored at zero — never negative,
    /// never a separate "no stacks" sentinel. Pure arithmetic; the resulting stage is whatever
    /// <see cref="NerveLadder.StageFor"/> resolves the NEXT time anything asks.</summary>
    public static int StacksAfterRelief(int currentStacks, long restRelief)
    {
        if (restRelief < 0) throw new ArgumentOutOfRangeException(nameof(restRelief), restRelief, "relief is never negative");
        return (int)Math.Max(0L, currentStacks - restRelief);
    }

    /// <summary>The whole rest room for one member: heal, relieve, and hand back the activation
    /// count verbatim (no camp system exists yet to spend it against — spec §5: "this module builds
    /// the activation counter and the heal, no camp system"). <paramref name="ambushed"/> defaults to
    /// false; a caller that also drew from the event deck passes its own result through so both halves
    /// of one rest resolve into one value.</summary>
    public static RestResult Resolve(
        IReadOnlyDictionary<string, long> pools,
        int nerveStacks,
        IReadOnlyList<string> healsPools,
        long attritionRestHealMilli,
        DifficultyRungTuning rung,
        long nerveRestRelief,
        int activations,
        ActorDerivedSnapshot derived,
        long atTick,
        bool ambushed = false)
    {
        var healed = Heal(pools, healsPools, attritionRestHealMilli, rung, derived, atTick);
        var stacks = StacksAfterRelief(nerveStacks, nerveRestRelief);
        return new RestResult(healed, stacks, new RestOutcome(activations, ambushed));
    }
}
