using System.Linq;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// `solid-remediation` T4.11/T4.12 (D7) — the siege AI's damage estimator agrees with the shipped
/// resolver.
///
/// <para><b>What D7 was.</b> `SiegeExpectedDamage` computed <c>power − effectiveDefense</c> and carried
/// a comment claiming it was identical to the resolver's omni branch. It had not been since the
/// resolver moved to a divisive defense shape. §6.3a records why that shape was dropped: <b>17.1% of
/// landed hits dealt nothing</b>. An estimator still on it reports "cannot kill" for exactly that band
/// of targets, and the siege AI chose targets on it.</para>
///
/// <para><b>The tolerance, and why it is what it is.</b> Agreement is asserted to <b>half a point of
/// damage</b>, plus a last-bits epsilon. The reason is one specific, unavoidable step: the resolver
/// returns an integer <c>SignedDelta</c>, rounding once at its <c>long</c> boundary, while the estimator
/// answers an unrounded <c>double</c> because a targeting score has no reason to quantise. Half a point
/// is exactly what that rounding can produce and nothing more. The epsilon on top covers association
/// order — floating-point addition is not associative, and the resolver accumulates through variables
/// the estimator computes inline.</para>
///
/// <para>The tolerance is sized to <i>rounding</i>, not to absorb a modelling difference: a missing term
/// costs a FRACTION of the damage, orders of magnitude more. The synthetic-divergence test below proves
/// that rather than asserting it.</para>
///
/// <para><b>Agreement, never a fixed expected number</b> (the task's own wording). Nothing here pins a
/// damage value: every assertion compares the two implementations against each other, so a balance
/// change to `DefenseDivisorK`, `PierceScale` or `AmpScale` moves both and turns nothing red.</para>
/// </summary>
[Trait("VerificationId", "core.siege-estimator-parity")]
public class SiegeEstimatorParityTests
{
    /// <summary>The resolver rounds once to `long`; the estimator does not round at all. Sized to that
    /// rounding, not to a modelling difference — see the class remarks.</summary>
    const double RoundingTolerance = 0.5;

    const double RelativeEpsilon = 1e-9;

    static ActorDerivedSnapshot Snapshot(params (string Channel, double Value)[] channels) =>
        ActorDerivedSnapshot.FromValues(
            channels.Select(c => new KeyValuePair<string, double>(c.Channel, c.Value)));

    /// <summary>Resolves a clean, non-crit omni hit through the REAL calculator, with hit and crit both
    /// forced so the comparison is about damage rather than about a band roll.</summary>
    static double ResolverCleanHit(ActorDerivedSnapshot attacker, ActorDerivedSnapshot defender, long baseDamage)
    {
        var calculator = new OverlayCombatCalculator();
        var (signedDelta, _) = calculator.Compute(
            new OverlayCombatRequest
            {
                Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
                Defender = new CombatActorSnapshot(defender, ActorElementTypes.Neutral),
                BaseOverlayDamage = baseDamage,
                Components = Array.Empty<ElementPayloadComponent>(),
                ForceHit = true,
                ForceCrit = false,
            },
            new UnusedRng());

        // SignedDelta is negative damage, rounded once at the long boundary.
        return -signedDelta;
    }

    /// <summary>Hit and crit are both forced, so nothing this test relies on is left to the rng.</summary>
    sealed class UnusedRng : ICombatRng
    {
        public int Next(int exclusiveMax) => 0;
    }

    public static IEnumerable<object[]> Matchups()
    {
        // Spread across the shape: no defense, defense far below power, defense ABOVE power (the band
        // the subtractive estimator got wrong), negative defense (the glass-cannon mirror), and
        // penetration/absorption/amplification all in play.
        yield return new object[] { 100.0, 0.0, 0.0, 0.0, 0.0, 0.0, 50L };
        yield return new object[] { 100.0, 30.0, 0.0, 0.0, 0.0, 0.0, 50L };
        yield return new object[] { 100.0, 400.0, 0.0, 0.0, 0.0, 0.0, 50L };
        yield return new object[] { 100.0, -50.0, 0.0, 0.0, 0.0, 0.0, 50L };
        yield return new object[] { 250.0, 180.0, 40.0, 15.0, 0.0, 0.0, 50L };
        yield return new object[] { 250.0, 180.0, 40.0, 15.0, 60.0, 25.0, 50L };
        yield return new object[] { 1000.0, 5000.0, 0.0, 0.0, 0.0, 0.0, 0L };
    }

    [Theory]
    [MemberData(nameof(Matchups))]
    public void The_estimator_agrees_with_the_resolver(
        double power, double defense, double penetration, double absorption,
        double amplification, double reduction, long baseDamage)
    {
        var attacker = Snapshot(
            (DerivedStatChannels.CombatPowerOmni, power),
            (DerivedStatChannels.CombatPenetrationOmni, penetration),
            (DerivedStatChannels.CombatAmplificationOmni, amplification));
        var defender = Snapshot(
            (DerivedStatChannels.CombatDefenseOmni, defense),
            (DerivedStatChannels.CombatAbsorptionOmni, absorption),
            (DerivedStatChannels.CombatReductionOmni, reduction));

        var estimated = SiegeExpectedDamage.ExpectedDamage(attacker, defender, baseDamage);
        var resolved = ResolverCleanHit(attacker, defender, baseDamage);

        AssertClose(resolved, estimated);
    }

    [Fact]
    public void A_synthetic_divergence_fails_the_same_comparison()
    {
        // The tolerance is only meaningful if it cannot absorb a real difference. Dropping the base
        // damage term is exactly the shape of defect D7 describes — one term missing — and it must land
        // far outside half a point.
        var attacker = Snapshot((DerivedStatChannels.CombatPowerOmni, 250.0));
        var defender = Snapshot((DerivedStatChannels.CombatDefenseOmni, 180.0));

        var resolved = ResolverCleanHit(attacker, defender, baseDamage: 50L);
        var divergent = SiegeExpectedDamage.ExpectedDamage(attacker, defender, baseOverlayDamage: 0.0);

        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertClose(resolved, divergent));
    }

    [Fact]
    public void The_estimator_is_no_longer_subtractive_where_defense_exceeds_power()
    {
        // D7's measured symptom, asserted as behaviour rather than as a formula: on the dropped
        // subtractive shape a defender with more defense than the attacker has power took NOTHING
        // (§6.3a: 17.1% of landed hits). The divisive curve is asymptotic to zero and never crosses it,
        // so such a target is still damaged — which is what makes "is this a killing blow" answerable
        // for it at all.
        var attacker = Snapshot((DerivedStatChannels.CombatPowerOmni, 100.0));
        var defender = Snapshot((DerivedStatChannels.CombatDefenseOmni, 400.0));

        var estimated = SiegeExpectedDamage.ExpectedDamage(attacker, defender, baseOverlayDamage: 25.0);

        Assert.True(estimated > 0.0,
            "a defender whose defense exceeds the attacker's power must still take damage — a zero here "
            + "is the subtractive cliff D7 was about");
    }

    [Fact]
    public void Crit_is_reported_separately_and_never_folded_into_the_estimate()
    {
        // Crit is a rolled branch in the resolver. An estimate that multiplied by it unconditionally
        // would call every marginal target lethal, which is the mirror of the defect being fixed.
        var attacker = Snapshot(
            (DerivedStatChannels.CombatPowerOmni, 100.0),
            (DerivedStatChannels.CombatCritDamageOmni, 500.0));
        var defender = Snapshot((DerivedStatChannels.CombatDefenseOmni, 10.0));

        var clean = SiegeExpectedDamage.ExpectedDamage(attacker, defender, baseOverlayDamage: 20.0);
        var resolved = ResolverCleanHit(attacker, defender, baseDamage: 20L);

        AssertClose(resolved, clean);
        Assert.True(SiegeExpectedDamage.CritMultiplier(attacker, defender) > 1.0);
    }

    static void AssertClose(double expected, double actual)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(expected), Math.Abs(actual)));
        var allowed = RoundingTolerance + RelativeEpsilon * scale;

        Assert.True(Math.Abs(expected - actual) <= allowed,
            $"estimator {actual} vs resolver {expected} — beyond the stated tolerance ({allowed}: half a "
            + "point for the one rounding the resolver performs, plus a last-bits epsilon)");
    }
}
