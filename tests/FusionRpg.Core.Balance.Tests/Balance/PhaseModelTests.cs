using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.3 — <see cref="PhaseModel"/>'s three building blocks: shield
/// effective HP (class-analytic-balance-2026-08-25.md §6.1), the reflection HP-phase gate, and the
/// unmitigated reflection bounce (spec-deterministic-core.md §2.1 corrections 1 and 3). Same
/// snapshot-construction idiom as <c>StrikeMixtureTests.cs</c>.</summary>
public class PhaseModelTests
{
    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();
    static CombatActorSnapshot Snap(ActorDerivedSnapshot s) => new(s, ActorElementTypes.Neutral);

    static CombatActorSnapshot With(params (string Channel, double Value)[] overlays) =>
        Snap(NeutralCombat.Overlay(overlays.Select(o => new KeyValuePair<string, double>(o.Channel, o.Value))));

    // ---- ShieldEffectiveHp -------------------------------------------------------------------

    [Fact]
    public void ShieldEffectiveHp_nullArguments_reject()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentNullException>(() => PhaseModel.ShieldEffectiveHp(100, 50, null!, s));
        Assert.Throws<ArgumentNullException>(() => PhaseModel.ShieldEffectiveHp(100, 50, s, null!));
    }

    [Fact]
    public void ShieldEffectiveHp_negativeOrNanInput_throws()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.ShieldEffectiveHp(100, -1, s, s));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.ShieldEffectiveHp(100, double.NaN, s, s));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void ShieldEffectiveHp_nonPositiveShieldMaxHp_returnsZero_aShieldNeedsAGrant(long shieldMaxHp)
    {
        // "a shield needs a grant to exist" (spec-deterministic-core.md §2.1 correction 3's corollary):
        // a non-positive maxHp means no ShieldGrant ever triggered ShieldRuntime.Apply, so there is no
        // pool at all -- regardless of how large pen/toughness/input are.
        var attacker = With((DerivedStatChannels.CombatShieldPenOmni, 9000));
        var defender = With((DerivedStatChannels.CombatShieldToughnessOmni, 1));
        Assert.Equal(0.0, PhaseModel.ShieldEffectiveHp(shieldMaxHp, 500, attacker, defender));
    }

    [Fact]
    public void ShieldEffectiveHp_inputRoundsToZero_returnsZeroRatherThanDividingByZero()
    {
        var s = Snap(NeutralCombat);
        Assert.Equal(0.0, PhaseModel.ShieldEffectiveHp(1000, 0.3, s, s));
        Assert.Equal(0.0, PhaseModel.ShieldEffectiveHp(1000, 0.0, s, s));
    }

    [Fact]
    public void ShieldEffectiveHp_atPenEqualsToughness_oneShieldPointEqualsOneHpPointExactly()
    {
        // class-analytic-balance-2026-08-25.md §6.1: "At pen = toughness a shield point equals an HP
        // point." breakerDelta=0 and input=100 is comfortably inside [floor=10, cap=300] (shipped
        // chipFloorKPm=100, penCapKPm=3000 -- gk-core/data/tuning/shield.v1.json), so damageToShield==input
        // unclamped and the ratio collapses to exactly 1.
        var attacker = With((DerivedStatChannels.CombatShieldPenOmni, 500));
        var defender = With((DerivedStatChannels.CombatShieldToughnessOmni, 500));
        Assert.Equal(1000.0, PhaseModel.ShieldEffectiveHp(1000, 100, attacker, defender), 9);
    }

    [Fact]
    public void ShieldEffectiveHp_defenderVastlyOutToughensAttacker_shieldIsWorthTenTimesItsRawHp()
    {
        // §6.1: "out-toughness the attacker and it is worth up to 10x" -- the floor clamp
        // (chipFloorKPm=100‰=10%) is the source of the 10x: input/floor = input/(0.1*input) = 10.
        var attacker = With((DerivedStatChannels.CombatShieldPenOmni, 0));
        var defender = With((DerivedStatChannels.CombatShieldToughnessOmni, 100_000));
        Assert.Equal(10_000.0, PhaseModel.ShieldEffectiveHp(1000, 100, attacker, defender), 6);
    }

    [Fact]
    public void ShieldEffectiveHp_attackerVastlyOutPenetrates_shieldIsWorthOneThirdItsRawHp()
    {
        // §6.1: "get out-penetrated and it is worth 1/3" -- the cap clamp (penCapKPm=3000‰=3x) is the
        // source: input/cap = input/(3*input) = 1/3.
        var attacker = With((DerivedStatChannels.CombatShieldPenOmni, 100_000));
        var defender = With((DerivedStatChannels.CombatShieldToughnessOmni, 0));
        Assert.Equal(1000.0 / 3.0, PhaseModel.ShieldEffectiveHp(1000, 100, attacker, defender), 6);
    }

    [Fact]
    public void ShieldEffectiveHp_matchesTheShippedClampedContestDirectly_forAnArbitraryCase()
    {
        // Proves the "calls shipped functions, never re-derives them" boundary (spec-deterministic-
        // core.md §2, §7) the same way StrikeMixtureTests does: compute the expectation via the exact
        // same shipped ClampedContest.Apply call this module is documented to make, independently in
        // the test, and assert an exact match -- not a re-implementation living in the test either.
        var attacker = With((DerivedStatChannels.CombatShieldPenOmni, 730));
        var defender = With((DerivedStatChannels.CombatShieldToughnessOmni, 210));
        const long shieldMaxHp = 4200;
        const double input = 317;

        var inputLong = (long)Math.Round(input, MidpointRounding.AwayFromZero);
        var expectedDamageToShield = ClampedContest.Apply(
            deltaBase: inputLong, delta: 730 - 210, hitCount: 1, boundsBase: inputLong,
            floorKPm: ShieldPolicyChipFloorKPm(), capKPm: ShieldPolicyPenCapKPm());
        var expected = shieldMaxHp * (double)inputLong / expectedDamageToShield;

        Assert.Equal(expected, PhaseModel.ShieldEffectiveHp(shieldMaxHp, input, attacker, defender), 9);
    }

    static long ShieldPolicyChipFloorKPm() => FusionRpg.Core.Combat.Shield.ShieldPolicy.ChipFloorKPm;
    static long ShieldPolicyPenCapKPm() => FusionRpg.Core.Combat.Shield.ShieldPolicy.PenCapKPm;

    [Fact]
    public void ShieldEffectiveHp_increasingAttackerPen_monotonicallyDecreasesEffectiveHp()
    {
        var defender = With((DerivedStatChannels.CombatShieldToughnessOmni, 500));
        var weak = With((DerivedStatChannels.CombatShieldPenOmni, 100));
        var strong = With((DerivedStatChannels.CombatShieldPenOmni, 900));

        var weakEff = PhaseModel.ShieldEffectiveHp(1000, 200, weak, defender);
        var strongEff = PhaseModel.ShieldEffectiveHp(1000, 200, strong, defender);

        Assert.True(strongEff < weakEff, $"expected higher attacker pen ({strongEff}) to shrink effective HP below the weaker case ({weakEff})");
    }

    // ---- ReflectionHpPhaseShare ---------------------------------------------------------------

    [Theory]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    public void ReflectionHpPhaseShare_negativeOrNanHp_throws(double hp)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.ReflectionHpPhaseShare(hp, 50));
    }

    [Fact]
    public void ReflectionHpPhaseShare_bothZero_isOneNotNanOrDivideByZero()
    {
        // Revised 2026-08-27: matches gk-core/tools/CombatSim/Analytic.cs's own HpPhaseShare, which returns
        // 1.0 (not a throw) when hp + shieldEffectiveHp <= 0.
        Assert.Equal(1.0, PhaseModel.ReflectionHpPhaseShare(0.0, 0.0));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ReflectionHpPhaseShare_negativeOrNanShieldEffectiveHp_throws(double sEff)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.ReflectionHpPhaseShare(100, sEff));
    }

    [Fact]
    public void ReflectionHpPhaseShare_zeroShield_isExactlyOne()
    {
        Assert.Equal(1.0, PhaseModel.ReflectionHpPhaseShare(500, 0.0));
    }

    [Fact]
    public void ReflectionHpPhaseShare_handComputedCase()
    {
        Assert.Equal(0.75, PhaseModel.ReflectionHpPhaseShare(300, 100), 9);
    }

    [Fact]
    public void ReflectionHpPhaseShare_increasingShield_monotonicallyDecreasesShare()
    {
        var small = PhaseModel.ReflectionHpPhaseShare(300, 50);
        var large = PhaseModel.ReflectionHpPhaseShare(300, 500);
        Assert.True(large < small, $"expected a bigger shield ({large}) to shrink the HP-phase share below the smaller case ({small})");
    }

    // ---- Reflect ------------------------------------------------------------------------------

    [Fact]
    public void Reflect_nullArguments_reject()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentNullException>(() => PhaseModel.Reflect(100, null!, s));
        Assert.Throws<ArgumentNullException>(() => PhaseModel.Reflect(100, s, null!));
    }

    [Fact]
    public void Reflect_negativeOrNanIncomingAmount_throws()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.Reflect(-1, s, s));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.Reflect(double.NaN, s, s));
    }

    [Fact]
    public void Reflect_neutralVsNeutral_probabilityIsExactlyZero_linearFromZeroNotSigmoid()
    {
        // CombatDamageDispatcher.cs's own comment: linear from zero, not the spec's sigmoid sketch,
        // specifically because sigmoid(0)=0.5 would hand every actor a default reflect chance
        // (NoGoldensMoveAtZero). At delta=0 the linear form gives exactly 0, not 0.5.
        var s = Snap(NeutralCombat);
        var r = PhaseModel.Reflect(1000, s, s);
        Assert.Equal(0.0, r.Probability);
        Assert.Equal(0.0, r.MeanDamage);
    }

    [Fact]
    public void Reflect_handComputedCase_matchesTheLinearFormula()
    {
        // reflectRateScale=10.0, reflectShareScale=100.0 (gk-core/data/tuning/combat.v1.json, live).
        var reflector = With(
            (DerivedStatChannels.CombatReflectRateOmni, 3.0),
            (DerivedStatChannels.CombatReflectDamageOmni, 40.0));
        var reflectedUpon = With(
            (DerivedStatChannels.CombatReflectResistRateOmni, 1.0),
            (DerivedStatChannels.CombatReflectResistDamageOmni, 15.0));

        // pReflect = clamp(max(0, 3-1)/10, 0, 1) = 0.2
        // reflectShare = clamp(max(0, 40-15)/100, 0, 1) = 0.25
        var r = PhaseModel.Reflect(200, reflector, reflectedUpon);
        Assert.Equal(0.2, r.Probability, 9);
        Assert.Equal(50.0, r.MeanDamage, 9); // 200 * 0.25
    }

    [Fact]
    public void Reflect_unmitigated_hugeDefenseOnReflectedUpon_doesNotChangeMeanDamage()
    {
        // spec-deterministic-core.md §2.1 correction 1 / CombatDamageDispatcher.cs:81-82: the bounce
        // carries no ElementPayload, so it skips mitigation entirely -- cranking a stat that would
        // matter enormously for a normal hit (defense/absorption) must change nothing here, because
        // Reflect never reads those channels at all.
        var reflector = With((DerivedStatChannels.CombatReflectDamageOmni, 80.0));
        var plainReflectedUpon = Snap(NeutralCombat);
        var heavilyDefendedReflectedUpon = With(
            (DerivedStatChannels.CombatDefenseOmni, 1_000_000),
            (DerivedStatChannels.CombatAbsorptionOmni, 1_000_000));

        var plain = PhaseModel.Reflect(500, reflector, plainReflectedUpon);
        var defended = PhaseModel.Reflect(500, reflector, heavilyDefendedReflectedUpon);

        Assert.Equal(plain.MeanDamage, defended.MeanDamage, 9);
        Assert.Equal(plain.Probability, defended.Probability, 9);
    }

    [Fact]
    public void Reflect_probabilityAndShare_clampAtOne_whenDeltaFarExceedsScale()
    {
        var reflector = With(
            (DerivedStatChannels.CombatReflectRateOmni, 100_000.0),
            (DerivedStatChannels.CombatReflectDamageOmni, 100_000.0));
        var reflectedUpon = Snap(NeutralCombat);

        var r = PhaseModel.Reflect(10, reflector, reflectedUpon);
        Assert.Equal(1.0, r.Probability);
        Assert.Equal(10.0, r.MeanDamage, 9); // incomingAmount * 1.0
    }

    [Fact]
    public void Reflect_reflectedUponOutresistsReflector_probabilityClampsAtZeroNotNegative()
    {
        var reflector = With((DerivedStatChannels.CombatReflectRateOmni, 1.0));
        var reflectedUpon = With((DerivedStatChannels.CombatReflectResistRateOmni, 500.0));

        var r = PhaseModel.Reflect(100, reflector, reflectedUpon);
        Assert.Equal(0.0, r.Probability);
    }

    // ---- JointReflect ---------------------------------------------------------------------------

    static readonly CombatActorSnapshot JointReflector = With(
        (DerivedStatChannels.CombatReflectRateOmni, 3.0),
        (DerivedStatChannels.CombatReflectDamageOmni, 40.0));
    static readonly CombatActorSnapshot JointReflectedUpon = With(
        (DerivedStatChannels.CombatReflectResistRateOmni, 1.0),
        (DerivedStatChannels.CombatReflectResistDamageOmni, 15.0));

    [Fact]
    public void JointReflect_handComputedThreeAtomCase()
    {
        // pReflect=(3-1)/10=0.2, share=(40-15)/100=0.25 -- same shipped scales as
        // Reflect_handComputedCase_matchesTheLinearFormula. Three nonzero atoms, chosen so every
        // per-atom rounded bounce is exact (25, 50) and the arithmetic is checkable by hand:
        //   Miss      P=0.5 D=0    -> bounced=round(0*0.25)=0     (contributes nothing)
        //   Clean     P=0.3 D=100  -> bounced=round(100*0.25)=25
        //   CleanCrit P=0.2 D=200  -> bounced=round(200*0.25)=50
        // dealtMean = 0.5*0 + 0.3*100 + 0.2*200 = 70
        // backMean       = 0.3*0.2*25 + 0.2*0.2*50           = 1.5 + 2.0   = 3.5
        // backSecondMom  = 0.3*0.2*25*25 + 0.2*0.2*50*50      = 37.5 + 100 = 137.5
        // backVar        = 137.5 - 3.5^2                                   = 125.25
        // dealtTimesBack = 0.3*0.2*100*25 + 0.2*0.2*200*50    = 150 + 400  = 550
        // cov            = 550 - 70*3.5                                    = 305
        var strike = new StrikeMixture.Result(
            Miss: new StrikeAtom(0.5, 0.0),
            Parried: new StrikeAtom(0.0, 0.0),
            Blocked: new StrikeAtom(0.0, 0.0),
            Clean: new StrikeAtom(0.3, 100.0),
            CleanCrit: new StrikeAtom(0.2, 200.0));

        var j = PhaseModel.JointReflect(strike, JointReflector, JointReflectedUpon);

        Assert.Equal(3.5, j.BackMean, 9);
        Assert.Equal(125.25, j.BackVariance, 9);
        Assert.Equal(305.0, j.CovDealtBack, 9);
    }

    [Fact]
    public void JointReflect_zeroReflectProbability_isAllZero()
    {
        var strike = new StrikeMixture.Result(
            new StrikeAtom(0.5, 0.0), new StrikeAtom(0.0, 0.0), new StrikeAtom(0.0, 0.0),
            new StrikeAtom(0.3, 100.0), new StrikeAtom(0.2, 200.0));
        var noReflect = Snap(NeutralCombat);

        var j = PhaseModel.JointReflect(strike, noReflect, noReflect);

        Assert.Equal(0.0, j.BackMean);
        Assert.Equal(0.0, j.BackVariance);
        Assert.Equal(0.0, j.CovDealtBack);
    }

    [Fact]
    public void JointReflect_backVarianceIsNeverNegative()
    {
        var strike = new StrikeMixture.Result(
            new StrikeAtom(0.1, 0.0), new StrikeAtom(0.2, 30.0), new StrikeAtom(0.2, 40.0),
            new StrikeAtom(0.3, 90.0), new StrikeAtom(0.2, 150.0));
        var j = PhaseModel.JointReflect(strike, JointReflector, JointReflectedUpon);
        Assert.True(j.BackVariance >= 0.0);
    }

    // ---- RecoveryPerRound -----------------------------------------------------------------------

    [Fact]
    public void RecoveryPerRound_handComputedCase()
    {
        // pen=toughness=500 (breakerDelta=0), input=100 -> damageToShield=100 unclamped (same setup as
        // ShieldEffectiveHp_atPenEqualsToughness), so the shield-regen ratio is exactly 1:
        // hpRegen(50) + shieldRegen(30) * 100/100 = 80.
        var attacker = With((DerivedStatChannels.CombatShieldPenOmni, 500));
        var defender = With((DerivedStatChannels.CombatShieldToughnessOmni, 500));
        Assert.Equal(80.0, PhaseModel.RecoveryPerRound(50, 30, 1000, 100, attacker, defender), 9);
    }

    [Fact]
    public void RecoveryPerRound_noShieldRegen_isHpRegenOnly()
    {
        var s = Snap(NeutralCombat);
        Assert.Equal(50.0, PhaseModel.RecoveryPerRound(50, 0, 1000, 100, s, s));
    }

    [Fact]
    public void RecoveryPerRound_noShieldGrant_isHpRegenOnly_aShieldNeedsAGrant()
    {
        var s = Snap(NeutralCombat);
        Assert.Equal(50.0, PhaseModel.RecoveryPerRound(50, 30, shieldMaxHp: 0, 100, s, s));
    }

    [Fact]
    public void RecoveryPerRound_zeroIncoming_isHpRegenOnly()
    {
        var s = Snap(NeutralCombat);
        Assert.Equal(50.0, PhaseModel.RecoveryPerRound(50, 30, 1000, 0, s, s));
    }

    [Fact]
    public void RecoveryPerRound_nullArguments_reject()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentNullException>(() => PhaseModel.RecoveryPerRound(50, 30, 1000, 100, null!, s));
        Assert.Throws<ArgumentNullException>(() => PhaseModel.RecoveryPerRound(50, 30, 1000, 100, s, null!));
    }

    [Fact]
    public void RecoveryPerRound_negativeShieldRegenOrInput_throws()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.RecoveryPerRound(50, -1, 1000, 100, s, s));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseModel.RecoveryPerRound(50, 30, 1000, -1, s, s));
    }
}
