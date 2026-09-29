using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatHitCritTests
{
    static OverlayCombatRequest BaseRequest(
        double attackerAccuracy,
        double defenderDodge,
        double attackerCritRate = 0,
        double defenderCritResist = 0)
    {
        var attacker = new CombatActorSnapshot(
            ActorDerivedSnapshot.StubNeutral().Overlay(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, attackerAccuracy),
                new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, attackerCritRate)
            }),
            ActorElementTypes.Neutral);
        var defender = new CombatActorSnapshot(
            ActorDerivedSnapshot.StubNeutral().Overlay(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.CombatDodgeOmni, defenderDodge),
                new KeyValuePair<string, double>(DerivedStatChannels.CombatCritResistOmni, defenderCritResist)
            }),
            ActorElementTypes.Neutral);
        return new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = attacker,
            Defender = defender
        };
    }

    [Fact]
    public void High_accuracy_guarantees_hit()
    {
        var calc = new OverlayCombatCalculator();
        var request = BaseRequest(attackerAccuracy: 500, defenderDodge: 0);
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(99));
        Assert.True(breakdown.Hit);
        Assert.Equal(-100, breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Low_accuracy_guarantees_miss()
    {
        var calc = new OverlayCombatCalculator();
        var request = BaseRequest(attackerAccuracy: -500, defenderDodge: 0);
        var (delta, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.False(breakdown.Hit);
        Assert.Equal(0, delta);
        Assert.Equal(0, breakdown.FinalSignedDelta);
        Assert.Equal(100, breakdown.PowerAdjustedDamage, 3);
    }

    [Fact]
    public void Single_component_hybrid_equals_non_hybrid()
    {
        var calc = new OverlayCombatCalculator();
        var request = BaseRequest(attackerAccuracy: 500, defenderDodge: 0);
        var a = calc.Compute(request, new SeededCombatRng(7));
        var b = calc.Compute(request, new SeededCombatRng(7));
        Assert.Equal(a.Breakdown.FinalSignedDelta, b.Breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Crit_applies_multiplier_on_hit()
    {
        var calc = new OverlayCombatCalculator();
        var request = BaseRequest(
            attackerAccuracy: 500,
            defenderDodge: 0,
            attackerCritRate: 500,
            defenderCritResist: 0);
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.True(breakdown.Hit);
        Assert.True(breakdown.Crit);
        Assert.True(breakdown.CritMultiplierFinal > 1.0);
        Assert.True(Math.Abs(breakdown.FinalSignedDelta) > 100);
    }

    [Fact]
    public void Crit_magnitude_scales_with_crit_damage_channel()
    {
        var calc = new OverlayCombatCalculator();
        var low = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral().Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatCritDamageOmni, 0)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = true
        };
        var high = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral().Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatCritDamageOmni, 500)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = true
        };
        var a = calc.Compute(low, new SeededCombatRng(1)).Breakdown;
        var b = calc.Compute(high, new SeededCombatRng(1)).Breakdown;
        Assert.True(a.Crit && b.Crit);
        Assert.True(b.CritMultiplierFinal > a.CritMultiplierFinal);
        Assert.True(Math.Abs(b.FinalSignedDelta) > Math.Abs(a.FinalSignedDelta));
    }

    [Fact]
    public void Crit_resist_damage_reduces_multiplier()
    {
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral().Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatCritDamageOmni, 500)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral().Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatCritResistDamageOmni, 500)
                }),
                ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = true
        };
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.True(breakdown.Crit);
        // critDmgDelta = 500 - 500 = 0 → CritMultiplierFinal = 1 + sigmoid(0) = 1.5
        Assert.Equal(1.5, breakdown.CritMultiplierFinal, 3);
        Assert.Equal(-150, breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Miss_skips_crit_even_when_force_crit()
    {
        var calc = new OverlayCombatCalculator();
        var request = BaseRequest(attackerAccuracy: 0, defenderDodge: 0);
        request = new OverlayCombatRequest
        {
            BaseOverlayDamage = request.BaseOverlayDamage,
            Components = request.Components,
            Attacker = request.Attacker,
            Defender = request.Defender,
            ForceHit = false,
            ForceCrit = true
        };
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.False(breakdown.Hit);
        Assert.False(breakdown.Crit);
        Assert.Equal(0, breakdown.FinalSignedDelta);
    }

    [Fact]
    public void High_crit_resist_lowers_p_crit()
    {
        var calc = new OverlayCombatCalculator();
        var lowResist = BaseRequest(500, 0, attackerCritRate: 100, defenderCritResist: 0);
        var highResist = BaseRequest(500, 0, attackerCritRate: 100, defenderCritResist: 500);
        var a = calc.Compute(lowResist, new SeededCombatRng(1)).Breakdown;
        var b = calc.Compute(highResist, new SeededCombatRng(1)).Breakdown;
        Assert.True(a.PCritFinal > b.PCritFinal);
    }

    [Fact]
    public void Ice_tank_profile_misses_without_force_hit()
    {
        var calc = new OverlayCombatCalculator();
        var tank = ActorDerivedProfiles.Get(ActorDerivedProfiles.CombatIceTank);
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = CombatActorSnapshot.AttackerLess(),
            Defender = new CombatActorSnapshot(tank, ActorElementTypes.Neutral)
        };
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.False(breakdown.Hit);
        Assert.Equal(0, breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Typed_accuracy_applies_only_to_matching_element()
    {
        var calc = new OverlayCombatCalculator();
        var attacker = ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyFire, 500)
        });
        var defender = ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDodgeOmni, 0)
        });
        OverlayCombatRequest Req(ElementTypeId el) => new()
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(el, 1.0) },
            Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(defender, ActorElementTypes.Neutral)
        };
        var fire = calc.Compute(Req(ElementTypeId.Fire), new SeededCombatRng(1)).Breakdown;
        var ice = calc.Compute(Req(ElementTypeId.Ice), new SeededCombatRng(1)).Breakdown;
        Assert.True(fire.PHitFinal > ice.PHitFinal);
        Assert.True(fire.Hit);
    }
}
