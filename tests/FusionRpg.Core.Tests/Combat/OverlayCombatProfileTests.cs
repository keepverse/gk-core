using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatProfileTests
{
    [Fact]
    public void Combat_glass_vs_neutral_increases_damage()
    {
        var calc = new OverlayCombatCalculator();
        var glass = ActorDerivedProfiles.Get(ActorDerivedProfiles.CombatGlass);
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = CombatActorSnapshot.AttackerLess(),
            Defender = new CombatActorSnapshot(glass, ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = false
        };
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        // glass: defense.omni = -50, so NEGATIVE defense must still AMPLIFY (the property this test
        // is named for). DefenseShape.Divisive mirrors below zero — 2 − K/(K + |defense|) with
        // K = 0.45 × 100 = 45 → 2 − 45/95 = 1.5263 → 152.63. Was 150 under the subtractive shape
        // (100 − (−50)); the mechanic is preserved, the number is not.
        //
        // This test is why the mirrored branch exists at all. A first pass clamped defense at zero
        // (`Math.Max(0, defense)`), which silently deleted the glass-cannon mechanic — this
        // assertion caught it, and it would have shipped as "glass profiles quietly stopped being
        // glass" otherwise.
        Assert.Equal(152.632, breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(-153, breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Ice_tank_ice_defense_vs_ice_payload_not_fire()
    {
        var calc = new OverlayCombatCalculator();
        var tank = ActorDerivedProfiles.Get(ActorDerivedProfiles.CombatIceTank);
        OverlayCombatRequest Req(ElementTypeId el) => new()
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(el, 1.0) },
            Attacker = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral().Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(tank, ActorElementTypes.Create(ElementTypeId.Ice)),
            ForceHit = true,
            ForceCrit = false
        };

        var ice = calc.Compute(Req(ElementTypeId.Ice), new SeededCombatRng(1));
        var fire = calc.Compute(Req(ElementTypeId.Fire), new SeededCombatRng(1));

        // ice def 30 + fire STR vs ice (+25 matchup) → fire powerAdjusted = 100+25 = 125
        // DefenseShape.Divisive (2026-08-25): offense 100, ladderScale 100, K = 0.45 x 100 = 45,
        // ice defense 30 -> 100 x 45/(45+30) = 60. Was 70 under the subtractive shape (100 - 30).
        // The fire case is unchanged at 125: matchup is excluded from ladderScale, and the ice
        // tank has no fire defense, so its divisor bites nothing.
        Assert.Equal(60, ice.Breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(125, fire.Breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(25, fire.Breakdown.MatchupBonus, 3);
        Assert.Equal(0, ice.Breakdown.MatchupBonus, 3);
    }

    [Fact]
    public void Dual_type_calculator_product_rule_e2e()
    {
        // Fire vs Ice+Air: (1.25)*(0.75)-1 = -0.0625 → bonus -6.25
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = CombatActorSnapshot.AttackerLess(),
            Defender = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral(),
                ActorElementTypes.Create(ElementTypeId.Ice, ElementTypeId.Air)),
            ForceHit = true,
            ForceCrit = false
        };
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.Equal(-6.25, breakdown.MatchupBonus, 3);
        Assert.Equal(-94, breakdown.FinalSignedDelta);
    }
}
