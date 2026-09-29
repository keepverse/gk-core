using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Balance.Analytic;

/// <summary>One of the five per-swing outcome atoms — spec-deterministic-core.md §1/§2; the research
/// record's "five atoms, each with a probability and a damage the shipped formulas already compute
/// exactly" (class-analytic-balance-2026-08-25.md §2).</summary>
public readonly record struct StrikeAtom(double Probability, double Damage);

/// <summary>
/// class-system-todo.md P4.1 — the five-atom mixture for one swing (miss | parry | block | clean |
/// clean+crit), computed by calling the SAME shipped functions
/// <see cref="OverlayCombatCalculator"/>'s omni-fallback path calls
/// (<see cref="CombatProbability.Sigmoid"/>, <see cref="ClampedContest.Apply"/>,
/// <see cref="OverlayCombatCalculator.DivisiveMitigation"/>/<see cref="OverlayCombatCalculator.PierceFactor"/>/
/// <see cref="OverlayCombatCalculator.AmpFactor"/>/<see cref="OverlayCombatCalculator.AmpFactorReciprocal"/>/
/// <see cref="OverlayCombatCalculator.CapAvoidanceBand"/>) — never re-implemented
/// (spec-deterministic-core.md §2, §7 boundary "Never: re-implement a combat formula"). A change to
/// any of those shipped functions moves this module's prediction automatically.
///
/// <para><b>Omni only — elements are out of scope.</b> class-analytic-balance-2026-08-25.md §7: "no
/// elements (deliberately neutralised)". This mirrors <c>OverlayCombatCalculator</c>'s own
/// <c>Components.Count == 0</c> branch, the path every closed-form archetype resolves through.
/// <c>EffectivenessMultiplier</c> stays 1.0 (no action layer yet, same §7 scope note) and
/// <c>MinChipShareKPm</c> is the overlay profile's own 0 (dead branch there) — so this mirrors
/// <see cref="CombatProfile.Overlay"/> exactly, not a generic profile.</para>
/// </summary>
public static class StrikeMixture
{
    public readonly record struct Result(
        StrikeAtom Miss, StrikeAtom Parried, StrikeAtom Blocked, StrikeAtom Clean, StrikeAtom CleanCrit)
    {
        /// <summary>μ — the exact mean of the swing's damage distribution (finite sum over 5 atoms).</summary>
        public double Mean =>
            Miss.Probability * Miss.Damage + Parried.Probability * Parried.Damage
            + Blocked.Probability * Blocked.Damage + Clean.Probability * Clean.Damage
            + CleanCrit.Probability * CleanCrit.Damage;

        /// <summary>σ² — Var[D] = E[D²] − E[D]², both exact finite sums (research record §2:
        /// "E[f(D)] ≠ f(E[D])", so this must go through the second moment, not (E[D])² of anything else).</summary>
        public double Variance
        {
            get
            {
                double SecondMoment(StrikeAtom a) => a.Probability * a.Damage * a.Damage;
                var e2 = SecondMoment(Miss) + SecondMoment(Parried) + SecondMoment(Blocked)
                       + SecondMoment(Clean) + SecondMoment(CleanCrit);
                var mean = Mean;
                return e2 - mean * mean;
            }
        }
    }

    public static Result Compute(double baseOverlayDamage, CombatActorSnapshot attacker, CombatActorSnapshot defender)
    {
        if (baseOverlayDamage < 0)
            throw new ArgumentOutOfRangeException(nameof(baseOverlayDamage), baseOverlayDamage, "must not be negative");
        if (attacker is null) throw new ArgumentNullException(nameof(attacker));
        if (defender is null) throw new ArgumentNullException(nameof(defender));

        var atk = attacker.Derived;
        var def = defender.Derived;
        var pierceScale = CombatPolicy.Default.PierceScale;
        var ampScale = CombatPolicy.Default.AmpScale;

        // Mirrors OverlayCombatCalculator.Compute's omniFallback branch line for line.
        var penDeltaOmni = atk.Get(DerivedStatChannels.CombatPenetrationOmni) - def.Get(DerivedStatChannels.CombatAbsorptionOmni);
        var effectiveDefenseOmni = def.Get(DerivedStatChannels.CombatDefenseOmni) * OverlayCombatCalculator.PierceFactor(penDeltaOmni, pierceScale);
        var powerOmni = atk.Get(DerivedStatChannels.CombatPowerOmni);
        var ampDelta = atk.Get(DerivedStatChannels.CombatAmplificationOmni) - def.Get(DerivedStatChannels.CombatReductionOmni);

        var pHit = CombatProbability.Sigmoid(
            atk.Get(DerivedStatChannels.CombatAccuracyOmni) - def.Get(DerivedStatChannels.CombatDodgeOmni),
            CombatProbabilityPolicy.AccuracyScale);
        var pCrit = CombatProbability.Sigmoid(
            atk.Get(DerivedStatChannels.CombatCritRateOmni) - def.Get(DerivedStatChannels.CombatCritResistOmni),
            CombatProbabilityPolicy.CritRateScale);
        var critMult = 1.0 + CombatProbability.Sigmoid(
            atk.Get(DerivedStatChannels.CombatCritDamageOmni) - def.Get(DerivedStatChannels.CombatCritResistDamageOmni),
            CombatProbabilityPolicy.CritDamageScale);

        // D14: CombatDerivedReader's parry/block readers now take an element, like the other nine
        // families. This method models the UNTYPED case -- its own comment above says it mirrors
        // OverlayCombatCalculator's omniFallback branch line for line, and every other family here
        // already reads the omni channel directly. So these do too, and the mirror claim stays true.
        var pParryRaw = Math.Max(0.0, def.Get(DerivedStatChannels.CombatParryRateOmni) - atk.Get(DerivedStatChannels.CombatParryBreakOmni)) / 1000.0;
        var pBlockRaw = Math.Max(0.0, def.Get(DerivedStatChannels.CombatBlockRateOmni) - atk.Get(DerivedStatChannels.CombatBlockBreakOmni)) / 1000.0;
        var avoidanceBandCap = CombatPolicy.Default.AvoidanceBandCapPermille / 1000.0;
        var (pParry, pBlock) = OverlayCombatCalculator.CapAvoidanceBand(pHit, pParryRaw, pBlockRaw, avoidanceBandCap);
        // ResolveBand's own doc: miss/parry/block/clean partition [0,1) with no gap and no overlap —
        // pCleanHit is what is left after the other three, never independently computed.
        var pCleanHit = Math.Max(0.0, pHit - pParry - pBlock);

        // Clean-hit damage: DivisiveMitigation is the shipped, defense-shape-selected path. Omni
        // fallback has weightedOffense == weightedPowerOnly == powerOmni (no matchup bonus, no
        // effectiveness beyond the 1.0 this module holds fixed) — see OverlayCombatCalculator.cs:97-99.
        var powerAdjusted = CombatPolicy.Default.DefenseShape == DefenseShape.Divisive
            ? OverlayCombatCalculator.DivisiveMitigation(
                offense: baseOverlayDamage + powerOmni, defense: effectiveDefenseOmni,
                k: CombatPolicy.Default.DefenseDivisorK, ladderScale: baseOverlayDamage + powerOmni)
            : baseOverlayDamage + (powerOmni - effectiveDefenseOmni);
        var ampFactor = CombatPolicy.Default.AmpShape == AmpShape.Reciprocal
            ? OverlayCombatCalculator.AmpFactorReciprocal(ampDelta, ampScale)
            : OverlayCombatCalculator.AmpFactor(ampDelta, ampScale);
        var cleanDamage = Math.Max(0.0, powerAdjusted) * ampFactor;

        // Parry/block damage: ClampedContest.Apply, the exact shipped shape (spec-evasion-chain.md §3
        // — "no block, no mitigation": the mitigation chain above never runs for these two atoms).
        var baseLong = (long)Math.Round(baseOverlayDamage, MidpointRounding.AwayFromZero);
        var neutralBase = (long)Math.Round(baseOverlayDamage * (CombatPolicy.Default.ParryNeutralShareKPm / 1000.0), MidpointRounding.AwayFromZero);
        var parryRemoved = ClampedContest.Apply(
            deltaBase: neutralBase,
            delta: (long)Math.Round(def.Get(DerivedStatChannels.CombatParryStrengthOmni) - atk.Get(DerivedStatChannels.CombatParryShredOmni), MidpointRounding.AwayFromZero),
            hitCount: 1, boundsBase: baseLong, floorKPm: 0, capKPm: CombatPolicy.Default.ParryCapPermille);
        var blockRemoved = ClampedContest.Apply(
            deltaBase: neutralBase,
            delta: (long)Math.Round(def.Get(DerivedStatChannels.CombatBlockStrengthOmni) - atk.Get(DerivedStatChannels.CombatBlockShredOmni), MidpointRounding.AwayFromZero),
            hitCount: 1, boundsBase: baseLong, floorKPm: 0, capKPm: CombatPolicy.Default.BlockCapPermille);
        var parryDamage = Math.Max(0.0, baseOverlayDamage - parryRemoved);
        var blockDamage = Math.Max(0.0, baseOverlayDamage - blockRemoved);

        return new Result(
            Miss: new StrikeAtom(1.0 - pHit, 0.0),
            Parried: new StrikeAtom(pParry, parryDamage),
            Blocked: new StrikeAtom(pBlock, blockDamage),
            Clean: new StrikeAtom(pCleanHit * (1.0 - pCrit), cleanDamage),
            CleanCrit: new StrikeAtom(pCleanHit * pCrit, cleanDamage * critMult));
    }
}
