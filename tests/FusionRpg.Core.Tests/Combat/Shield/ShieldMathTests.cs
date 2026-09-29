using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>Math goldens for shield-system-spec.md §2.4 — these freeze the locked formula.</summary>
public class ShieldMathTests
{
    [Fact]
    public void ShieldGoldensByteIdentical()
    {
        // spec-evasion-chain.md §6.1 (T5.2) -- "extracting ClampedContest must not move a single
        // shield golden," run explicitly BEFORE parry/block exist (a two-step landing, not one). One
        // representative worked example per branch this file's other tests cover individually,
        // gathered under the name the spec asks for so the claim is traceable to one place: the
        // element-matchup branch (elemMod nonzero -- the exact case that caught T5.2's own extraction
        // bug, since bounds must scale against raw `input`, not `input + elemMod`), the chip floor,
        // the pen cap, and the plain no-modifier case.
        var neutral = ShieldMath.AbsorbLayer(input: 100, shieldHp: 500, weightedRelationUnitPm: 0, breakerDelta: 0, hitCount: 1);
        Assert.Equal(100, neutral.DamageToShield);

        var elemMatchup = ShieldMath.AbsorbLayer(240, 60, 1000, 0, 1);
        Assert.Equal(300, elemMatchup.DamageToShield);
        Assert.Equal(60, elemMatchup.Spent);
        Assert.Equal(192, elemMatchup.Remainder);

        var chipFloor = ShieldMath.AbsorbLayer(100, 50, 0, -120, 1);
        Assert.Equal(10, chipFloor.DamageToShield);
        Assert.Equal(10, chipFloor.Spent);

        var penCap = ShieldMath.AbsorbLayer(2, 200, 0, 200, 1);
        Assert.Equal(6, penCap.DamageToShield);

        var weakRelation = ShieldMath.AbsorbLayer(100, 500, -1000, 0, 1);
        Assert.Equal(75, weakRelation.DamageToShield);
    }

    [Fact]
    public void Neutral_hold_full_absorb_leaves_zero_remainder()
    {
        var r = ShieldMath.AbsorbLayer(input: 100, shieldHp: 500, weightedRelationUnitPm: 0, breakerDelta: 0, hitCount: 1);
        Assert.Equal(100, r.Spent);
        Assert.Equal(0, r.Remainder);
        Assert.Equal(100, r.DamageToShield);
    }

    [Fact]
    public void Exact_break_leaves_zero_remainder()
    {
        var r = ShieldMath.AbsorbLayer(100, 100, 0, 0, 1);
        Assert.Equal(100, r.Spent);
        Assert.Equal(0, r.Remainder);
    }

    [Fact]
    public void Overflow_remainder_is_proportional_worked_example_layer1()
    {
        // Spec §2.4 worked cascade, layer S1: 240 fire vs ice shield 60 HP, STR full weight.
        // elemMod = 1000 × 250 × 240 / 1e6 = +60 → d = 300; spent 60; remainder round(240×240/300) = 192.
        var r = ShieldMath.AbsorbLayer(240, 60, 1000, 0, 1);
        Assert.Equal(300, r.DamageToShield);
        Assert.Equal(60, r.Spent);
        Assert.Equal(192, r.Remainder);
    }

    [Fact]
    public void Chip_floor_engages_when_toughness_exceeds_hit_no_immunity()
    {
        // input 100, breaker −120 → raw = −20; floor = ceil(0.10 × 100) = 10.
        var r = ShieldMath.AbsorbLayer(100, 50, 0, -120, 1);
        Assert.Equal(10, r.DamageToShield);
        Assert.Equal(10, r.Spent);         // shield ALWAYS spends — immunity impossible
        Assert.Equal(0, r.Remainder);      // and holds → HP takes 0
    }

    [Fact]
    public void Chip_floor_rounds_up_for_tiny_hits()
    {
        // input 3 → floor = ceil(0.3) = 1 even with massive toughness.
        var r = ShieldMath.AbsorbLayer(3, 10, 0, -1000, 1);
        Assert.Equal(1, r.DamageToShield);
        Assert.Equal(1, r.Spent);
    }

    [Fact]
    public void Pen_cap_bounds_chip_hit_shield_stripping()
    {
        // input 2, pen +200 → raw = 202; cap = 3 × 2 = 6.
        var r = ShieldMath.AbsorbLayer(2, 200, 0, 200, 1);
        Assert.Equal(6, r.DamageToShield);
        Assert.Equal(6, r.Spent);
        Assert.Equal(0, r.Remainder);
    }

    [Fact]
    public void Toughness_saturates_at_ten_x_efficiency_exactly_at_boundary()
    {
        // input 100, breaker −90 → raw = 10 == floor: clamp is a no-op at the boundary.
        var r = ShieldMath.AbsorbLayer(100, 500, 0, -90, 1);
        Assert.Equal(10, r.DamageToShield);
    }

    [Fact]
    public void Coalesced_equals_n_times_uncoalesced()
    {
        // 5 same-key hits of 20, breaker +10: uncoalesced 5 × (20 + 10) = 150 shield damage.
        long uncoalescedSpent = 0;
        long shieldHp = 1000;
        for (var i = 0; i < 5; i++)
        {
            var r = ShieldMath.AbsorbLayer(20, shieldHp, 0, 10, 1);
            uncoalescedSpent += r.Spent;
            shieldHp -= r.Spent;
        }

        var coalesced = ShieldMath.AbsorbLayer(100, 1000, 0, 10, hitCount: 5);
        Assert.Equal(150, coalesced.DamageToShield);
        Assert.Equal(uncoalescedSpent, coalesced.Spent);
    }

    [Fact]
    public void Tie_golden_input1_d2_leaks_full_damage()
    {
        // Locked tie choice (spec §2.4): input 1, breaker +1 → d = 2; shield 1 spends and
        // remainder rounds half away from zero to 1 — chosen, not a surprise.
        var r = ShieldMath.AbsorbLayer(1, 1, 0, 1, 1);
        Assert.Equal(2, r.DamageToShield);
        Assert.Equal(1, r.Spent);
        Assert.Equal(1, r.Remainder);
    }

    [Fact]
    public void Zero_input_produces_zero_everything()
    {
        var r = ShieldMath.AbsorbLayer(0, 100, 1000, 500, 9);
        Assert.Equal(0, r.Spent);
        Assert.Equal(0, r.Remainder);
    }

    [Fact]
    public void Zero_shield_passes_input_through()
    {
        var r = ShieldMath.AbsorbLayer(100, 0, 0, 0, 1);
        Assert.Equal(0, r.Spent);
        Assert.Equal(100, r.Remainder);
    }

    [Fact]
    public void Weak_relation_reduces_shield_damage()
    {
        // WEK full weight: elemMod = −1000 × 250 × 100 / 1e6 = −25 → d = 75.
        var r = ShieldMath.AbsorbLayer(100, 500, -1000, 0, 1);
        Assert.Equal(75, r.DamageToShield);
        Assert.Equal(0, r.Remainder);
    }

    [Fact]
    public void Hybrid_weights_compose_relation_units()
    {
        // fire 0.7 STR + air 0.3 WEK vs ice shield: fire→ice +1, air→ice 0 ⇒ 700.
        var components = new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.7),
            new ElementPayloadComponent(ElementTypeId.Air, 0.3)
        };
        Assert.Equal(700, ShieldMath.WeightedRelationUnitPm(components, ElementTypeId.Ice));
        // Untyped shield → always 0.
        Assert.Equal(0, ShieldMath.WeightedRelationUnitPm(components, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(999)]
    [InlineData(123457)]
    public void Invariants_hold_across_grid(long input)
    {
        foreach (var shieldHp in new long[] { 0, 1, input / 2, input, input * 3, 1_000_000 })
        foreach (var rel in new long[] { -1000, -300, 0, 700, 1000 })
        foreach (var breaker in new long[] { -10 * input, -5, 0, 5, 10 * input })
        foreach (var hits in new long[] { 1, 3 })
        {
            var r = ShieldMath.AbsorbLayer(input, shieldHp, rel, breaker, hits);
            Assert.InRange(r.Remainder, 0, input);
            if (shieldHp <= 0)
            {
                // Depleted shield never engages: pure pass-through (the cascade skips it anyway).
                Assert.Equal(0, r.Spent);
                Assert.Equal(input, r.Remainder);
                continue;
            }

            Assert.True(r.Spent <= Math.Min(shieldHp, r.DamageToShield));
            Assert.InRange(r.DamageToShield, (100 * input + 999) / 1000, 3 * input);
            if (shieldHp >= r.DamageToShield)
                Assert.Equal(0, r.Remainder);
        }
    }

    [Fact]
    public void Remainder_monotone_non_increasing_in_shield_hp()
    {
        long prev = long.MaxValue;
        for (long hp = 0; hp <= 300; hp += 10)
        {
            var r = ShieldMath.AbsorbLayer(200, hp, 0, 50, 1);
            Assert.True(r.Remainder <= prev, $"remainder rose at hp={hp}");
            prev = r.Remainder;
        }
    }

    [Fact]
    public void Input_one_under_MaxInput_throws_nothing()
    {
        var r = ShieldMath.AbsorbLayer(ShieldMath.MaxInput - 1, 1000, 1000, 1000, 1);
        Assert.True(r.DamageToShield >= 0);
    }

    [Fact]
    public void Input_over_MaxInput_throws_naming_the_site_and_the_value()
    {
        // T3.5 (spec-caps-reconcile.md §2.1): the old clamp is gone -- an oversized input is refused
        // loudly, not silently pinned and allowed to compute a wrong (but plausible-looking) result.
        var over = ShieldMath.MaxInput + 1;
        var ex = Assert.Throws<ShieldInputOverflow>(() => ShieldMath.AbsorbLayer(over, 1000, 1000, 1000, 9));
        Assert.Equal(over, ex.Input);
        Assert.Equal(ShieldMath.MaxInput, ex.MaxInput);
    }

    [Fact]
    public void MaxInput_is_derived_from_the_loaded_ShieldPolicy_not_a_literal()
    {
        // F13: MaxInput reads MatchupShareKPm/ChipFloorKPm/PenCapKPm -- at the shipped values
        // (250/100/3000) the elemMod term (1000 * 250 = 250,000) is the tightest of the three, so
        // MaxInput == long.MaxValue / 250,000 exactly. Independently recomputed here, not read off
        // the implementation under test.
        Assert.Equal(long.MaxValue / 250_000, ShieldMath.MaxInput);
    }
}
