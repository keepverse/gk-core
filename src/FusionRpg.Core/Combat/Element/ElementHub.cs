using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Element;

/// <summary>
/// combat-numerics (lawn-combat-wire T4) numeric scope: <see cref="IElementHub"/> (a separate,
/// out-of-scope file this task may not edit) fixes both <c>baseOverlayDamage</c> and this class's own
/// return type to <c>double</c> — every real caller (<c>OverlayCombatCalculator</c>) immediately folds
/// the result back into its own double-typed mitigation chain, an already-accepted design (see that
/// file's own class doc). So the public boundary in and out stays <c>double</c> by contract; what
/// moved to <c>long</c>/per-mille is the interior share arithmetic this class actually owns — the
/// STR/WEK/NEU multiplier, composed from a closed per-mille share
/// (<c>ElementMatchupPolicy.MatchupShareK</c>, shipped <c>0.25</c> == exactly <c>250</c>‰).
/// </summary>
public sealed class ElementHub : IElementHub
{
    public static ElementHub Default { get; } = new();

    public double ResolveComponentBonus(
        ElementTypeId attackerElement,
        ActorElementTypes defenderTypes,
        double baseOverlayDamage)
    {
        if (defenderTypes.IsNeutral)
            return 0.0;

        // combinedMult composed as an exact integer fraction numPm/denomPm (denomPm = 1000^slotCount)
        // rather than dividing by 1000 after each slot: two per-mille shares multiplied together can
        // land on a value that is not itself a multiple of 1000 (1250 x 750 = 937_500), so dividing
        // after each slot would truncate a remainder the single final division is meant to carry.
        // Widen (both slots folded into the numerator/denominator, still exact integers) before
        // multiplying by baseOverlayDamage, divide by the combined scale LAST, exactly once —
        // verified byte-identical against every shipped single- and dual-type matchup golden
        // (ElementHubSingleTypeTests, ElementHubDualTypeTests, ElementHubHybridTests).
        long numPm = 1;
        long denomPm = 1;
        if (defenderTypes.Primary is { } primary)
        {
            numPm *= SlotMultiplierPm(attackerElement, primary);
            denomPm *= 1000;
        }
        if (defenderTypes.Secondary is { } secondary)
        {
            numPm *= SlotMultiplierPm(attackerElement, secondary);
            denomPm *= 1000;
        }

        // The one unavoidable double touchpoint: IElementHub fixes baseOverlayDamage (and this
        // method's own return) to double, because the real caller reads it straight back into the
        // double-typed mitigation chain (see the class doc above). Everything before this line is
        // long; this is the single, final, once-only division.
        return (numPm - denomPm) * baseOverlayDamage / denomPm;
    }

    public double ResolvePayloadBonus(
        IReadOnlyList<ElementPayloadComponent> components,
        ActorElementTypes defenderTypes,
        double baseOverlayDamage)
    {
        // Omni fallback: empty payloads are legal untyped attacks — zero matchup by rule.
        if (components == null || components.Count == 0)
            return 0.0;
        ElementPayload.Validate(components);
        var total = 0.0;
        foreach (var c in components)
        {
            // c.Weight is `double` on ElementPayloadComponent (a separate, out-of-scope record) —
            // an authored fractional share (e.g. 0.7/0.3), not a magnitude this task's numeric rules
            // govern.
            total += c.Weight * ResolveComponentBonus(c.Element, defenderTypes, baseOverlayDamage);
        }

        return total;
    }

    /// <summary>Per-mille slot multiplier: 1000 == identity (1.0x), 1000±250 == STR/WEK.</summary>
    static long SlotMultiplierPm(ElementTypeId attacker, ElementTypeId defenderSlot)
    {
        var relation = ElementRingMatrix.GetRelation(attacker, defenderSlot);
        // ElementRingMatrix.RelationShare returns `double` (a separate, out-of-scope file) but is
        // itself a closed per-mille share authored in gk-core/data/tuning/stats.v1.json
        // (matchupShareK: 0.25 == 250 per-mille exactly; Strong/Weak are its +/- values, Same/Neutral
        // are exactly 0) — rounding once at this boundary is exact for the shipped value, not an
        // approximation.
        var sharePm = (long)Math.Round(ElementRingMatrix.RelationShare(relation) * 1000.0, MidpointRounding.AwayFromZero);
        return 1000 + sharePm;
    }
}
