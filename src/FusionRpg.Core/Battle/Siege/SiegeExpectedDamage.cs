using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` (spec-siege-ai.md, `IsKillingBlow`): a reusable expected-damage estimate for
/// `AiCandidate.IsKillingBlow` — the same "read `OverlayCombatCalculator`, reuse its already-shipped
/// pieces, never modify it" discipline `SiegeHitChance.cs` established for `HitChanceMilli`.
///
/// <para><b>⚠️ Corrected 2026-09-17 (`solid-remediation` T4.11, D7).</b> This file previously computed
/// <c>power − effectiveDefense</c> and carried a comment claiming it was *"identical to
/// OverlayCombatCalculator.Compute's own Omni-fallback branch"*. It was not, and had not been since the
/// resolver moved to a divisive defense shape. The comment is the part worth dwelling on: it named a
/// specific file and line range, which is exactly what makes a stale claim survive review — a reader
/// checking the estimator found a citation and stopped. §6.3a records why subtractive was dropped:
/// **17.1% of landed hits dealt nothing**, the cliff where a defender simply becomes immune. An
/// estimator still on that shape does not merely disagree at the margin — it reports "cannot kill" for
/// every target whose defense exceeds the attacker's power, which is the same 17.1% of hits, and the
/// siege AI was choosing targets on it.</para>
///
/// <para><b>What it mirrors now</b>, term for term against the resolver's omni-fallback branch:
/// penetration/absorption scale defense inside the delta (pierce-scaled); offense is the authored base
/// damage plus omni power; mitigation is <see cref="OverlayCombatCalculator.DivisiveMitigation"/> with
/// the same <c>K</c> and the same ladder scale (base + power, never effectiveness or matchup); then
/// amplification, through whichever <see cref="AmpShape"/> the policy ships.</para>
///
/// <para><b>Omni-only, deliberately, for the identical reason `SiegeHitChance` is</b>: a targeting
/// decision does not yet know which action — and therefore which `ElementPayloadComponent`s — will be
/// used, so the typed-element branches are not reachable from here. Replicating them without that
/// context would risk a second, silently-diverging copy of a formula this program has already named a
/// recurring bug class.</para>
///
/// <para><b>Deterministic, not probabilistic</b> — this asks "if a hit lands squarely, does it end the
/// fight", never "what is the expected value across the hit/crit/miss distribution". `AiScoring` already
/// carries `HitChanceMilli` as its own separate, dominant term (§3: 70 vs `IsKillingBlow`'s 15), so
/// folding hit chance in here would double-count it. <b>Crit is the same argument</b>: it is a
/// probabilistic branch in the resolver (`crit` is rolled), so including its multiplier unconditionally
/// would make every marginal target look lethal. <see cref="ExpectedDamage"/> therefore reports the
/// clean-hit damage, and <see cref="CritMultiplier"/> exposes the crit term separately for a caller that
/// wants to score the upside without the estimate claiming it.</para>
/// </summary>
public static class SiegeExpectedDamage
{
    /// <summary>
    /// Damage a clean (non-crit, non-parried, non-blocked) omni hit deals, on the shipped defense shape.
    /// </summary>
    /// <param name="baseOverlayDamage">The authored hit the action carries. The resolver's offense and
    /// its ladder scale both include it, and a divisive shape is <b>not</b> scale-free in it — omitting
    /// it does not merely shift the estimate, it changes the mitigated fraction.</param>
    public static double ExpectedDamage(
        ActorDerivedSnapshot attacker, ActorDerivedSnapshot defender, double baseOverlayDamage = 0.0)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(defender);

        var pierceScale = CombatPolicy.Default.PierceScale;

        // Penetration/absorption scale defense INSIDE the delta (spec-mitigation-chain.md §2), exactly
        // as the resolver's omni branch does.
        var penDelta = attacker.Get(DerivedStatChannels.CombatPenetrationOmni)
                       - defender.Get(DerivedStatChannels.CombatAbsorptionOmni);
        var effectiveDefense = defender.Get(DerivedStatChannels.CombatDefenseOmni)
                               * OverlayCombatCalculator.PierceFactor(penDelta, pierceScale);

        var power = attacker.Get(DerivedStatChannels.CombatPowerOmni);

        // A targeting estimate has no skill effectiveness and no matchup bonus, so offense and the
        // ladder scale coincide here — the two differ in the resolver only by terms this caller cannot
        // know yet. Stated rather than silently collapsed, because they are NOT the same quantity.
        var offense = baseOverlayDamage + power;
        var ladderScale = baseOverlayDamage + power;

        var powerAdjusted = CombatPolicy.Default.DefenseShape == DefenseShape.Divisive
            ? OverlayCombatCalculator.DivisiveMitigation(
                offense, effectiveDefense, CombatPolicy.Default.DefenseDivisorK, ladderScale)
            : baseOverlayDamage + (power - effectiveDefense);

        var damage = Math.Max(0.0, powerAdjusted);

        // Amplification lands after crit in the resolver; multiplication commutes, so applying it to
        // the clean-hit figure here is the same arithmetic.
        var ampDelta = attacker.Get(DerivedStatChannels.CombatAmplificationOmni)
                       - defender.Get(DerivedStatChannels.CombatReductionOmni);
        var ampScale = CombatPolicy.Default.AmpScale;

        damage *= CombatPolicy.Default.AmpShape == AmpShape.Reciprocal
            ? OverlayCombatCalculator.AmpFactorReciprocal(ampDelta, ampScale)
            : OverlayCombatCalculator.AmpFactor(ampDelta, ampScale);

        return damage;
    }

    /// <summary>
    /// The crit multiplier this matchup would apply <b>if</b> the crit roll landed — the resolver's own
    /// <c>1 + Contest(critDamage, critResistDamage)</c>. Exposed separately rather than folded into
    /// <see cref="ExpectedDamage"/>, so a caller that wants the upside asks for it explicitly and an
    /// estimate never claims a probabilistic branch as certain.
    /// </summary>
    public static double CritMultiplier(ActorDerivedSnapshot attacker, ActorDerivedSnapshot defender)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(defender);

        return 1.0 + Combat.Element.ElementalResolver.Contest(
            attacker.Get(DerivedStatChannels.CombatCritDamageOmni),
            defender.Get(DerivedStatChannels.CombatCritResistDamageOmni),
            CombatProbabilityPolicy.CritDamageScale);
    }

    /// <summary>Does a clean hit end the fight? Unchanged in shape — only the damage behind it is now
    /// the shipped one.</summary>
    public static bool IsKillingBlow(
        ActorDerivedSnapshot attacker, ActorDerivedSnapshot defender, long targetCurrentHp,
        double baseOverlayDamage = 0.0) =>
        ExpectedDamage(attacker, defender, baseOverlayDamage) >= targetCurrentHp;
}
