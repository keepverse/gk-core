using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>Typed power/defense apply only to matching element payload.</summary>
public class OverlayCombatTypedChannelIsolationTests
{
    static OverlayCombatRequest Build(
        ElementTypeId payload,
        ActorDerivedSnapshot attacker,
        ActorDerivedSnapshot defender) =>
        new()
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(payload, 1.0) },
            Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(defender, ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = false
        };

    [Fact]
    public void Fire_power_applies_to_fire_payload_only()
    {
        var calc = new OverlayCombatCalculator();
        var attacker = ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerFire, 50)
        });
        var def = ActorDerivedSnapshot.StubNeutral();

        var fire = calc.Compute(Build(ElementTypeId.Fire, attacker, def), new SeededCombatRng(1));
        var ice = calc.Compute(Build(ElementTypeId.Ice, attacker, def), new SeededCombatRng(1));

        Assert.Equal(150, fire.Breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(-150, fire.Breakdown.FinalSignedDelta);
        Assert.Equal(100, ice.Breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(-100, ice.Breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Ice_defense_applies_to_ice_payload_only()
    {
        var calc = new OverlayCombatCalculator();
        var atk = ActorDerivedSnapshot.StubNeutral();
        var defender = ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseIce, 30)
        });

        var ice = calc.Compute(Build(ElementTypeId.Ice, atk, defender), new SeededCombatRng(1));
        var fire = calc.Compute(Build(ElementTypeId.Fire, atk, defender), new SeededCombatRng(1));

        // DefenseShape.Divisive (2026-08-25): offense 100, ladderScale 100, K = 0.45 x 100 = 45,
        // ice defense 30 -> 100 x 45/(45+30) = 60. Was 70 under the subtractive shape (100 - 30).
        // The fire case is unchanged at 125: matchup is excluded from ladderScale, and the ice
        // tank has no fire defense, so its divisor bites nothing.
        Assert.Equal(60, ice.Breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(-60, ice.Breakdown.FinalSignedDelta);
        Assert.Equal(100, fire.Breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(-100, fire.Breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Omni_power_applies_to_any_element()
    {
        var calc = new OverlayCombatCalculator();
        var attacker = ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 20)
        });
        var def = ActorDerivedSnapshot.StubNeutral();

        var fire = calc.Compute(Build(ElementTypeId.Fire, attacker, def), new SeededCombatRng(1));
        var air = calc.Compute(Build(ElementTypeId.Air, attacker, def), new SeededCombatRng(1));

        Assert.Equal(120, fire.Breakdown.PowerAdjustedDamage, 3);
        Assert.Equal(120, air.Breakdown.PowerAdjustedDamage, 3);
    }
}
