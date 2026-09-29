namespace FusionRpg.Core.Battle;

/// <summary>
/// `battle-tempo` `tempo-content` (spec-tempo-content.md §2.1) — `turn.speed` from a species' own
/// attack interval. A PROJECTION of a number the corpus already authors
/// (`attackTempo` -> `attackTempoIntervalMs`, `ConcreteSpecies.AttackIntervalMs`), never a second
/// table over the same five labels: `turn.speed = TurnDefaultSpeed × referenceIntervalMs /
/// attackIntervalMs`. A formula, not a lookup, is what keeps this a projection rather than a private
/// curve — `ssot-power-scale.md`'s "one ladder" rule holds by construction because the only new
/// number is `referenceIntervalMs` itself.
/// </summary>
public static class SpeciesTempoProjection
{
    /// <summary>`turn.speed` for one species. `attackIntervalMs &lt;= 0` yields
    /// <paramref name="defaultSpeed"/> and never throws — the structural floor
    /// <see cref="Timeline.TurnReadiness.EffectiveRate"/> requires (it DIVIDES by speed and throws on
    /// <c>&lt;= 0</c>), and the honest default for a species this projection has never heard of
    /// (`CreatureSpeciesDef.AttackIntervalMs` defaults to 0 for exactly this reason). PS-8 exempt
    /// (`ssot-power-scale.md` §11.4: "for a denominator the overflow risk inverts to small values") —
    /// this is a termination guard on a divisor, not a progression ceiling.</summary>
    public static long SpeedFor(long attackIntervalMs, long referenceIntervalMs, long defaultSpeed)
    {
        if (referenceIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(referenceIntervalMs), referenceIntervalMs, "referenceIntervalMs must be > 0 (it is a divisor's numerator scale)");
        if (defaultSpeed <= 0) throw new ArgumentOutOfRangeException(nameof(defaultSpeed), defaultSpeed, "defaultSpeed must be > 0 -- it is itself a floor value EffectiveRate divides by");
        if (attackIntervalMs <= 0) return defaultSpeed;

        // Widen before multiplying, divide by 1000-scale operations last -- here there is no /1000
        // step (this is not a per-mille ratio), but the same "widen before multiply" discipline
        // applies: both operands are already `long` and the product is computed before any division.
        return Math.Max(1, checked(defaultSpeed * referenceIntervalMs) / attackIntervalMs);
    }
}
