using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Balance.Analytic;

/// <summary>
/// class-system-todo.md P4.3 — the shield phase boundary and the reflection gate
/// (class-analytic-balance-2026-08-25.md §6.1). Three independent pure building blocks; composing
/// them into a race is <c>Predictor</c>'s job (P4.6), not this one's.
///
/// <para><b>1. The shield phase.</b> While a shield stands, <see cref="ShieldMath.AbsorbLayer"/> takes
/// the whole hit and spends <c>damageToShield</c> from the pool, so one shield HP is worth
/// <c>input/damageToShield</c> raw incoming HP — <see cref="ShieldEffectiveHp"/> computes that ratio.
/// Because both phases (shield-active and HP-only) face the identical incoming distribution
/// (mitigation runs before the shield gate — only the target changes), the two phases' first-passage
/// times sum to exactly what one pool of size <c>hp + S_eff</c> already gives
/// (research §6.1: "One extra term, no second solve") — so the caller adds <see cref="ShieldEffectiveHp"/>
/// straight onto <c>hp</c> before calling <see cref="FirstPassage.Compute"/>; nothing here re-derives
/// first-passage math.</para>
///
/// <para><b>2. "A shield needs a grant."</b> <see cref="ShieldRuntime.Apply"/> computes
/// <c>maxHp = grant.BaseHp + capacity</c> and only ever forms a pool when some
/// <see cref="ShieldGrant"/> triggered that call — there is no shipped path where a capacity stat
/// alone spontaneously creates HP. <see cref="ShieldEffectiveHp"/> mirrors that exactly: it takes the
/// shield's already-resolved <c>maxHp</c> as an opaque parameter (never re-derives it from a capacity
/// channel) and returns 0 when it is non-positive — "no grant" and "zero effective HP" are the same
/// state here, by construction, not by a special case.</para>
///
/// <para><b>3. The reflection gate.</b> <c>CombatDamageDispatcher.cs:65-69</c>: with the shipped
/// <c>reflectReadsPostShield: true</c> (gk-core/data/tuning/combat.v1.json:26 — confirmed live, not assumed),
/// the bounce reads <c>applied.AppliedAmount</c> — the amount that reached HP — so a fully-absorbed
/// hit reflects nothing. First-order treatment (research §6.1): reflection fires only during the HP
/// phase, whose share of the total expected fight is <c>hp/(hp+S_eff)</c> —
/// <see cref="ReflectionHpPhaseShare"/> computes that scale factor; <see cref="Reflect"/> computes the
/// (unscaled) bounce itself, and the caller multiplies the two together.</para>
///
/// <para><b>4. "Reflected damage is unmitigated."</b> <c>CombatDamageDispatcher.cs:81-82</c>: the
/// bounce packet carries no <c>ElementPayload</c>, so <c>OverlayCombatMath.Finalize</c> passes it
/// through unchanged. <see cref="Reflect"/> mirrors this by calling no mitigation function at all —
/// on purpose, not an oversight.</para>
/// </summary>
public static class PhaseModel
{
    /// <param name="shieldMaxHp">The shield's already-resolved full pool (<c>grant.BaseHp + capacity</c>,
    /// <see cref="ShieldRuntime.Apply"/>) — 0 (or negative) means no active grant, and this function
    /// then returns 0 without reading any stat. Never call this with a raw capacity value standing in
    /// for a grant that was never made.</param>
    /// <param name="input">Mean per-swing damage arriving at the shield gate, pre-shield
    /// (<see cref="StrikeMixture.Result.Mean"/>) — the seam where this closed form's <c>double</c>
    /// expectation becomes the shipped integer math's <c>long</c> "one hit", rounded once, here, and
    /// nowhere else in this function.</param>
    /// <param name="attacker">Reads <see cref="CombatDerivedReader.ShieldPen"/> — the shield is being
    /// broken into, not owned, by this side (mirrors <c>ShieldRuntime.Absorb</c>'s own
    /// <c>attackerSnapshot</c>).</param>
    /// <param name="defender">Reads <see cref="CombatDerivedReader.ShieldToughness"/> — the shield's
    /// owner (mirrors <c>ShieldRuntime.Absorb</c>'s own <c>ownerSnapshot</c>).</param>
    public static double ShieldEffectiveHp(long shieldMaxHp, double input, CombatActorSnapshot attacker, CombatActorSnapshot defender)
    {
        if (attacker is null) throw new ArgumentNullException(nameof(attacker));
        if (defender is null) throw new ArgumentNullException(nameof(defender));
        if (double.IsNaN(input) || input < 0)
            throw new ArgumentOutOfRangeException(nameof(input), input, "must be non-negative");
        if (shieldMaxHp <= 0)
            return 0.0; // no grant -> no shield -> no effective HP. See type doc, point 2.

        // The long seam: ClampedContest.Apply (and ShieldMath.AbsorbLayer, which this mirrors) is
        // exact 64-bit integer math over "one hit" -- evaluating it at the distribution's rounded
        // mean is the closed form's own approximation, budgeted into the residuals this whole
        // program measures against (spec-deterministic-core.md §1), not a new source of error.
        var inputLong = (long)Math.Round(input, MidpointRounding.AwayFromZero);
        if (inputLong <= 0)
            return 0.0; // a swing too small to round to a whole HP never engages the shield gate.

        var pen = (long)Math.Round(CombatDerivedReader.ShieldPen(attacker.Derived, null), MidpointRounding.AwayFromZero);
        var toughness = (long)Math.Round(CombatDerivedReader.ShieldToughness(defender.Derived, null), MidpointRounding.AwayFromZero);
        var breakerDelta = pen - toughness;

        // elemMod is omitted: Phase 4 is omni-only (StrikeMixture's own scoping), so
        // weightedRelationUnitPm is always 0 for the swings this module predicts -- baseValue ==
        // input exactly, matching ShieldMath.AbsorbLayer's own elemMod==0 case line for line.
        var damageToShield = ClampedContest.Apply(
            deltaBase: inputLong, delta: breakerDelta, hitCount: 1, boundsBase: inputLong,
            floorKPm: ShieldPolicy.ChipFloorKPm, capKPm: ShieldPolicy.PenCapKPm);
        if (damageToShield <= 0)
            return 0.0; // ChipFloorKPm>0 makes this unreachable for inputLong>=1, kept as a guard, not assumed.

        return shieldMaxHp * (double)inputLong / damageToShield;
    }

    /// <summary>The HP-phase's share of the total expected fight — <c>hp/(hp+S_eff)</c>
    /// (research §6.1's first-order reflection-gate scale). <paramref name="shieldEffectiveHp"/> may be
    /// 0 (no shield — the share is then exactly 1.0, reflection fully live from round one).
    ///
    /// <para><b>Revised 2026-08-27</b> against <c>gk-core/tools/CombatSim/Analytic.cs</c>'s own
    /// <c>HpPhaseShare</c> (the function this ports): the original version here required
    /// <c>hp &gt; 0</c> and threw otherwise. The reference instead returns <b>1.0</b> when
    /// <c>hp + shieldEffectiveHp &lt;= 0</c> — read that once P4.6 needed to cross-check this file
    /// line for line against the reference it was always meant to port, not assumed correct from an
    /// earlier, less-informed pass. Kept as the non-throwing form for fidelity to what it ports;
    /// <paramref name="hp"/> is still validated non-negative, since a negative HP pool is meaningless
    /// regardless of what the reference does with it (the reference never receives one either).</para>
    /// </summary>
    public static double ReflectionHpPhaseShare(double hp, double shieldEffectiveHp)
    {
        if (double.IsNaN(hp) || hp < 0)
            throw new ArgumentOutOfRangeException(nameof(hp), hp, "must be non-negative");
        if (double.IsNaN(shieldEffectiveHp) || shieldEffectiveHp < 0)
            throw new ArgumentOutOfRangeException(nameof(shieldEffectiveHp), shieldEffectiveHp, "must be non-negative");
        var total = hp + shieldEffectiveHp;
        return total <= 0 ? 1.0 : hp / total;
    }

    public readonly record struct ReflectOutcome(double Probability, double MeanDamage);

    /// <param name="incomingAmount">Mean damage the bounce is computed from —
    /// <c>reflectSource</c> in <c>CombatDamageDispatcher.DispatchInstant</c> (the shipped
    /// <c>reflectReadsPostShield: true</c> path reads this post-shield; the caller is responsible for
    /// passing the post-shield mean, this function only computes the bounce from whatever it is given).</param>
    /// <param name="reflector">The side whose reflect stats fire the bounce (<c>TryReflect</c>'s
    /// <c>reflector</c> — the one who was just hit).</param>
    /// <param name="reflectedUpon">The side the bounce lands on (<c>TryReflect</c>'s
    /// <c>reflectedUpon</c> — the original attacker).</param>
    public static ReflectOutcome Reflect(double incomingAmount, CombatActorSnapshot reflector, CombatActorSnapshot reflectedUpon)
    {
        if (double.IsNaN(incomingAmount) || incomingAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(incomingAmount), incomingAmount, "must be non-negative");

        var (pReflect, reflectShare) = ReflectRateAndShare(reflector, reflectedUpon);

        // Unmitigated, on purpose: CombatDamageDispatcher.cs:81-82 -- the bounce carries no
        // ElementPayload, so OverlayCombatMath.Finalize passes it through unchanged. No mitigation
        // function is called here for exactly that reason -- a raw multiply is the shipped behaviour,
        // not a shortcut. Not rounded either: this is the atom's mean, same "don't round an
        // expectation" discipline as StrikeMixture's own atoms (P4.1) -- only the shipped calculator's
        // single-hit output ever rounds.
        var meanBounce = incomingAmount * reflectShare;

        return new ReflectOutcome(pReflect, meanBounce);
    }

    static (double PReflect, double Share) ReflectRateAndShare(CombatActorSnapshot reflector, CombatActorSnapshot reflectedUpon)
    {
        if (reflector is null) throw new ArgumentNullException(nameof(reflector));
        if (reflectedUpon is null) throw new ArgumentNullException(nameof(reflectedUpon));

        var policy = CombatPolicy.Default;
        var refl = reflector.Derived;
        var upon = reflectedUpon.Derived;

        // D8 was "the reflect formula is written twice verbatim". This was the second copy, and it
        // is now one call to the shape's owner instead -- ElementalResolver.RateFromZero, which is
        // deliberately linear rather than a sigmoid for the reason this comment used to restate
        // second-hand (a sigmoid is 0.5 at parity, so an unstatted actor would reflect half the time).
        //
        // D14: this method models the UNTYPED case, like every other omni read around it, so the
        // element half is zero here. `CombatDerivedReader.Reflect*` now takes an element, and reading
        // the omni channel directly is what keeps this an omni model rather than silently picking one.
        var pReflect = ElementalResolver.RateFromZero(
            refl.Get(DerivedStatChannels.CombatReflectRateOmni),
            upon.Get(DerivedStatChannels.CombatReflectResistRateOmni),
            policy.ReflectRateScale);

        var reflectShare = ElementalResolver.RateFromZero(
            refl.Get(DerivedStatChannels.CombatReflectDamageOmni),
            upon.Get(DerivedStatChannels.CombatReflectResistDamageOmni),
            policy.ReflectShareScale);

        return (pReflect, reflectShare);
    }

    public readonly record struct ReflectJoint(double BackMean, double BackVariance, double CovDealtBack);

    /// <summary>
    /// The FULL joint distribution of (this swing's dealt damage, the bounce it may trigger) — not
    /// just their separate means. Kept joint, not two independent marginals, because dealt-and-bounced
    /// are the SAME event seen twice (the bounce is a share of what just landed), and <see cref="Race"/>
    /// needs exactly that covariance for its <c>ρ</c> term (P4.2; class-analytic-balance-2026-08-25.md
    /// §2: dropping it costs ~5 points of win rate on a reflect matchup). Ten terms (five atoms × {no
    /// bounce, bounce}), each exact — a finite mixture of finite outcomes stays exact however it is
    /// decomposed.
    /// </summary>
    /// <param name="strike">The swing's own 5-atom mixture (<see cref="StrikeMixture.Compute"/>) —
    /// this function reads its atoms, it does not recompute them.</param>
    /// <param name="reflector">The side whose reflect stats fire the bounce — the one THIS swing hits.</param>
    /// <param name="reflectedUpon">The side the bounce lands on — the one who threw this swing.</param>
    public static ReflectJoint JointReflect(StrikeMixture.Result strike, CombatActorSnapshot reflector, CombatActorSnapshot reflectedUpon) =>
        JointReflect(new[] { strike.Miss, strike.Parried, strike.Blocked, strike.Clean, strike.CleanCrit }, strike.Mean, reflector, reflectedUpon);

    /// <summary>The general form: any finite list of (probability, damage) atoms, not just one swing's
    /// own five. P4.6's action-economy mixture is a MIXTURE OF MIXTURES — a different 5-atom
    /// <see cref="StrikeMixture.Result"/> per possible action, weighted by how often the walk chose it
    /// (<c>Predictor</c>'s own <c>MixedStrike</c>) — flattened to one atom list before it reaches here,
    /// the same way <c>gk-core/tools/CombatSim/Analytic.cs</c>'s own <c>MixedStrike</c> flattens into one
    /// <c>List&lt;Atom&gt;</c> before <c>Swing</c>'s reflect-joint step reads it.</summary>
    /// <param name="dealtMean">The atoms' own probability-weighted mean — passed rather than
    /// recomputed, since the caller already has it (<see cref="StrikeMixture.Result.Mean"/>, or a
    /// weighted average across several results for the mixed-action case) and floating-point
    /// re-summation could disagree with it in the last bit.</param>
    public static ReflectJoint JointReflect(
        IReadOnlyList<StrikeAtom> atoms, double dealtMean, CombatActorSnapshot reflector, CombatActorSnapshot reflectedUpon)
    {
        if (atoms is null) throw new ArgumentNullException(nameof(atoms));
        var (pReflect, share) = ReflectRateAndShare(reflector, reflectedUpon);

        // Each atom splits into a "no bounce" half (Back=0) and a "bounce" half (Back = a rounded share
        // of THIS atom's own damage) -- the no-bounce half always contributes 0 to every Back-weighted
        // sum below, so only the bounce half needs accumulating. (Equivalent to collapsing the two
        // halves back into one term when the rounded bounce is 0, which is what avoids a special case
        // here: a zero Back contributes nothing to any sum either way.)
        double backMean = 0, backSecondMoment = 0, dealtTimesBack = 0;
        foreach (var atom in atoms)
        {
            if (atom.Probability <= 0) continue;
            // A discrete per-hit bounce, rounded the same way the shipped calculator rounds any single
            // hit -- this is a per-OUTCOME quantity (part of a finite enumeration), not an aggregate
            // expectation, so it rounds where Reflect()'s own aggregate mean deliberately does not.
            var bounced = Math.Round(atom.Damage * share, MidpointRounding.AwayFromZero);
            var pBounce = atom.Probability * pReflect;

            backMean += pBounce * bounced;
            backSecondMoment += pBounce * bounced * bounced;
            dealtTimesBack += pBounce * atom.Damage * bounced;
        }

        var backVar = Math.Max(0.0, backSecondMoment - backMean * backMean);
        // Cov(Dealt,Back) = E[Dealt*Back] - E[Dealt]*E[Back]; E[Dealt] over the joint space equals
        // strike.Mean because every outcome (bounced or not) carries the SAME Dealt = atom.Damage.
        var covDealtBack = dealtTimesBack - dealtMean * backMean;

        return new ReflectJoint(backMean, backVar, covDealtBack);
    }

    /// <summary>HP-equivalent recovered per round: direct HP regen, plus shield regen converted through
    /// the same <c>input/damageToShield</c> ratio a shield point is worth (<see cref="ShieldEffectiveHp"/>
    /// — mirrors its exact clamp math, since "how much of an incoming hit reaches the shield" is the
    /// identical question for a point of current HP and a point of incoming regen). Shield regen only
    /// counts while a shield can exist to receive it — no grant, no shield, no converted regen either
    /// (the same "a shield needs a grant" rule <see cref="ShieldEffectiveHp"/> observes).</summary>
    /// <summary><paramref name="poiseRegen"/>: class-system-todo.md P7.4, spec-guard-economy.md §9
    /// test 8 — "the termination invariant re-run and green with `poise` live." A THIRD, simple
    /// additive recovery source, deliberately NOT phase-decomposed the way <paramref name="shieldRegen"/>
    /// is: a shield absorbs automatically on every hit (so its own contribution depends on the
    /// pen/toughness phase split below), while `poise` pays for a deliberate "raise guard" ACTION
    /// (spec-guard-economy.md §3) with no automatic per-hit interaction to decompose — its own
    /// regen is just added directly, the same shape <paramref name="hpRegen"/> already has. Defaults
    /// to 0 so every EXISTING call site (and this file's own already-proven test suite) is
    /// byte-identical without passing it — matching what is actually true today: no aptitude edge
    /// feeds `resource.regen.poise` yet (P7.2's own named gap), so a caller reading that channel gets
    /// 0 regardless, and this parameter existing now means nothing else needs to change once one
    /// does.</summary>
    public static double RecoveryPerRound(
        double hpRegen, double shieldRegen, long shieldMaxHp, double input,
        CombatActorSnapshot attacker, CombatActorSnapshot defender, double poiseRegen = 0.0)
    {
        if (double.IsNaN(hpRegen)) throw new ArgumentOutOfRangeException(nameof(hpRegen), hpRegen, "must not be NaN");
        if (double.IsNaN(shieldRegen) || shieldRegen < 0)
            throw new ArgumentOutOfRangeException(nameof(shieldRegen), shieldRegen, "must be non-negative");
        if (double.IsNaN(poiseRegen) || poiseRegen < 0)
            throw new ArgumentOutOfRangeException(nameof(poiseRegen), poiseRegen, "must be non-negative");
        if (attacker is null) throw new ArgumentNullException(nameof(attacker));
        if (defender is null) throw new ArgumentNullException(nameof(defender));
        if (double.IsNaN(input) || input < 0)
            throw new ArgumentOutOfRangeException(nameof(input), input, "must be non-negative");

        var baseRecovery = hpRegen + poiseRegen;

        if (shieldRegen <= 0 || shieldMaxHp <= 0 || input <= 0) return baseRecovery;

        var inputLong = (long)Math.Round(input, MidpointRounding.AwayFromZero);
        if (inputLong <= 0) return baseRecovery;

        var pen = (long)Math.Round(CombatDerivedReader.ShieldPen(attacker.Derived, null), MidpointRounding.AwayFromZero);
        var toughness = (long)Math.Round(CombatDerivedReader.ShieldToughness(defender.Derived, null), MidpointRounding.AwayFromZero);
        var damageToShield = ClampedContest.Apply(
            deltaBase: inputLong, delta: pen - toughness, hitCount: 1, boundsBase: inputLong,
            floorKPm: ShieldPolicy.ChipFloorKPm, capKPm: ShieldPolicy.PenCapKPm);
        if (damageToShield <= 0) return baseRecovery;

        return baseRecovery + shieldRegen * (double)inputLong / damageToShield;
    }
}
