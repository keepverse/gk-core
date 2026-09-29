using System.Reflection;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// U5 — SSOT contracts locked as tests (spec-combat-resolver-core): RNG draw consumption,
/// crit-multiplier bounds, and the retired-symbol ban (arms itself when battle adoption bumps
/// RulesetVersion to 2 — until then the duplicates legitimately exist).
/// </summary>
public class CombatSsotContractTests
{
    sealed class CountingRng : ICombatRng
    {
        public int Draws;
        readonly int _value;
        public CountingRng(int value = 0) => _value = value;

        public int Next(int exclusiveMax)
        {
            Draws++;
            return _value;
        }
    }

    static OverlayCombatRequest Omni(double accuracy = 0, double dodge = 0, bool? forceHit = null,
        bool? forceCrit = null)
    {
        var composer = new DerivedComposer();
        var attacker = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatAccuracyOmni, DerivedModifierOp.Flat, accuracy)
        });
        var defender = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatDodgeOmni, DerivedModifierOp.Flat, dodge)
        });
        return new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = Array.Empty<ElementPayloadComponent>(),
            Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(defender, ActorElementTypes.Neutral),
            ForceHit = forceHit,
            ForceCrit = forceCrit
        };
    }

    [Fact]
    public void Normal_swing_draws_one_for_hit_plus_one_when_landed()
    {
        var calc = new OverlayCombatCalculator();
        var rng = new CountingRng(0);   // 0 < p rolls: always success at value 0
        calc.Compute(Omni(), rng);
        Assert.Equal(2, rng.Draws);     // hit draw + crit draw (hit landed)

        var missRng = new CountingRng(999_999);   // roll ≥ p → miss
        calc.Compute(Omni(), missRng);
        Assert.Equal(1, missRng.Draws); // miss → no crit draw
    }

    [Fact]
    public void Saturated_probabilities_consume_no_draw()
    {
        var calc = new OverlayCombatCalculator();
        // Sigmoid saturates to exactly 1.0 in double well below delta 5000 (scale 100).
        var certain = new CountingRng(0);
        var (_, sureHit) = calc.Compute(Omni(accuracy: 500_000), certain);
        Assert.True(sureHit.Hit);
        Assert.Equal(1, certain.Draws);   // only the crit draw — the hit was certain

        // e^(x) overflows to Infinity around delta/scale ≈ −710 → p = exactly 0.
        var hopeless = new CountingRng(0);
        var (_, sureMiss) = calc.Compute(Omni(dodge: 500_000), hopeless);
        Assert.False(sureMiss.Hit);
        Assert.Equal(0, hopeless.Draws);  // no hit draw, no crit draw
    }

    [Fact]
    public void Forced_rolls_consume_no_draws()
    {
        var calc = new OverlayCombatCalculator();
        var rng = new CountingRng();
        calc.Compute(Omni(forceHit: true, forceCrit: true), rng);
        Assert.Equal(0, rng.Draws);   // why battle goldens must be natural-roll only
    }

    [Theory]
    [InlineData(-1_000_000)]
    [InlineData(-800)]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(1_000_000)]
    public void Crit_multiplier_is_bounded_open_one_to_two(double critDamageDelta)
    {
        var composer = new DerivedComposer();
        var attacker = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatCritDamageOmni, DerivedModifierOp.Flat, critDamageDelta)
        });
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = Array.Empty<ElementPayloadComponent>(),
            Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = true
        };
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.InRange(breakdown.CritMultiplierFinal, 1.0, 2.0);
        if (critDamageDelta == 0)
            Assert.Equal(1.5, breakdown.CritMultiplierFinal, 9);   // the ×1.5 anchor
    }

    /// <summary>
    /// The ban test — arms itself at battle adoption (RulesetVersion 2). Until then the
    /// duplicates legitimately exist and this asserts the arming precondition instead.
    /// </summary>
    [Fact]
    public void Retired_battle_combat_symbols_are_gone_once_adoption_lands()
    {
        if (BattleRuleset.RulesetVersion < 2)
        {
            // Pre-adoption: the duplicates are still the shipping battle math.
            Assert.NotNull(typeof(BattleEngine).GetMethod("ShareMilli",
                BindingFlags.Public | BindingFlags.Static));
            return;
        }

        Assert.Null(typeof(BattleEngine).GetMethod("ShareMilli",
            BindingFlags.Public | BindingFlags.Static));
        foreach (var retired in new[]
                 {
                     "HitBaseMilli", "HitSlopeMilli", "HitFloorMilli", "HitCeilMilli",
                     "CritBaseMilli", "CritSlopeMilli", "CritCeilMilli",
                     "CritMultBaseMilli", "CritMultSlopeMilli", "CritMultFloorMilli", "CritMultCeilMilli"
                 })
        {
            Assert.Null(typeof(BattleRuleset).GetField(retired,
                BindingFlags.Public | BindingFlags.Static));
        }
    }
}
