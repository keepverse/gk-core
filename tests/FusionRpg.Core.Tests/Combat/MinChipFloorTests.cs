using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>U4 — per-profile min-chip floor (owner decision 6).</summary>
public class MinChipFloorTests
{
    static OverlayCombatRequest TankMatchup(CombatProfile profile, long baseDamage = 100,
        bool? forceHit = true, bool? forceCrit = false)
    {
        var composer = new DerivedComposer();
        var defender = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatDefenseOmni, DerivedModifierOp.Flat, 10_000.0)
        });
        return new OverlayCombatRequest
        {
            BaseOverlayDamage = baseDamage,
            Components = Array.Empty<ElementPayloadComponent>(),   // omni fallback path
            Attacker = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(defender, ActorElementTypes.Neutral),
            ForceHit = forceHit,
            ForceCrit = forceCrit,
            Profile = profile
        };
    }

    [Fact]
    public void Overlay_profile_keeps_zero_damage_outcomes()
    {
        var calc = new OverlayCombatCalculator();
        var (delta, _) = calc.Compute(TankMatchup(CombatProfile.Overlay), new SeededCombatRng(1));
        Assert.Equal(0, delta);   // byte-identical pre-unification behavior
    }

    [Fact]
    public void BattleSim_profile_floors_landed_hits_at_five_percent()
    {
        var calc = new OverlayCombatCalculator();
        var (delta, _) = calc.Compute(TankMatchup(CombatProfile.BattleSim), new SeededCombatRng(1));
        Assert.Equal(-5, delta);   // ceil(0.05 × 100)
    }

    [Fact]
    public void Chip_has_a_floor_of_one_for_tiny_bases()
    {
        var calc = new OverlayCombatCalculator();
        var (delta, _) = calc.Compute(TankMatchup(CombatProfile.BattleSim, baseDamage: 3), new SeededCombatRng(1));
        Assert.Equal(-1, delta);   // max(1, ceil(0.15))
    }

    [Fact]
    public void Chip_never_raises_ordinary_damage()
    {
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = Array.Empty<ElementPayloadComponent>(),
            ForceHit = true,
            ForceCrit = false,
            Profile = CombatProfile.BattleSim
        };
        var (delta, _) = calc.Compute(request, new SeededCombatRng(1));
        Assert.Equal(-100, delta);   // damage above the floor is untouched
    }

    [Fact]
    public void Misses_are_never_floored()
    {
        var calc = new OverlayCombatCalculator();
        var (delta, breakdown) = calc.Compute(TankMatchup(CombatProfile.BattleSim, forceHit: false),
            new SeededCombatRng(1));
        Assert.False(breakdown.Hit);
        Assert.Equal(0, delta);
    }

    [Fact]
    public void Chip_applies_after_crit_multiplication()
    {
        // Even a crit on a fully-absorbed-by-defense hit lands at the chip, not chip × mult.
        var calc = new OverlayCombatCalculator();
        var (delta, breakdown) = calc.Compute(TankMatchup(CombatProfile.BattleSim, forceCrit: true),
            new SeededCombatRng(1));
        Assert.True(breakdown.Crit);
        Assert.Equal(-5, delta);   // max(0,·)×1.5 = 0 → floored to 5
    }
}
