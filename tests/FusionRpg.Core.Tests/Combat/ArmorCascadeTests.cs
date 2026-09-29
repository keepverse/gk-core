using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// The bug these pin (2026-09-17): overlay damage reached `theHealth` directly and never spent armour,
/// so a Buckethead bled health with a full bucket. The order is the owner's ruling —
/// <c>armor2 → armor1 → hp</c> — and nothing bypasses a layer that still has points.
/// </summary>
public class ArmorCascadeTests
{
    [Fact]
    public void Damage_smaller_than_armor2_never_touches_armor1_or_hp()
    {
        var r = ArmorCascade.Apply(damage: 30, armor2: 100, armor1: 50, hp: 270);

        Assert.Equal(70, r.Armor2);
        Assert.Equal(50, r.Armor1);
        Assert.Equal(270, r.Hp);
        // The load-bearing assertion: health is untouched, so the caller must not write HP or run a
        // kill check at all.
        Assert.Equal(0, r.HpLost);
    }

    [Fact]
    public void Damage_spills_armor2_then_armor1_then_hp_in_that_order()
    {
        // 180 damage against 100/50/270: armour eats 150, exactly 30 reaches health.
        var r = ArmorCascade.Apply(damage: 180, armor2: 100, armor1: 50, hp: 270);

        Assert.Equal(0, r.Armor2);
        Assert.Equal(0, r.Armor1);
        Assert.Equal(240, r.Hp);
        Assert.Equal(30, r.HpLost);
    }

    [Fact]
    public void Armor1_absorbs_only_after_armor2_is_gone()
    {
        // Exactly enough to strip armor2 and no more — armor1 must be untouched, not merely reduced.
        var r = ArmorCascade.Apply(damage: 100, armor2: 100, armor1: 50, hp: 270);

        Assert.Equal(0, r.Armor2);
        Assert.Equal(50, r.Armor1);
        Assert.Equal(270, r.Hp);
        Assert.Equal(0, r.HpLost);
    }

    [Fact]
    public void A_zombie_with_no_armor_takes_the_damage_on_health_unchanged_from_before_the_fix()
    {
        // The no-regression case: an unarmoured zombie must behave exactly as it did when damage went
        // straight to health, or this fix would change every ordinary hit in the game.
        var r = ArmorCascade.Apply(damage: 45, armor2: 0, armor1: 0, hp: 270);

        Assert.Equal(0, r.Armor2);
        Assert.Equal(0, r.Armor1);
        Assert.Equal(225, r.Hp);
        Assert.Equal(45, r.HpLost);
    }

    [Fact]
    public void Lethal_damage_reports_a_non_positive_hp_rather_than_clamping_to_zero()
    {
        // Death is the caller's decision (the injector force-kills instead of writing a non-positive
        // health), so this function must hand back the real number rather than hide it at 0.
        var r = ArmorCascade.Apply(damage: 500, armor2: 10, armor1: 10, hp: 270);

        Assert.Equal(0, r.Armor2);
        Assert.Equal(0, r.Armor1);
        Assert.Equal(-210, r.Hp);
        Assert.Equal(480, r.HpLost);
    }

    [Fact]
    public void Zero_and_negative_damage_are_a_no_op()
    {
        var zero = ArmorCascade.Apply(damage: 0, armor2: 100, armor1: 50, hp: 270);
        Assert.Equal(100, zero.Armor2);
        Assert.Equal(50, zero.Armor1);
        Assert.Equal(270, zero.Hp);
        Assert.Equal(0, zero.HpLost);

        // A negative amount must not heal or restore armour by subtracting a negative.
        var negative = ArmorCascade.Apply(damage: -75, armor2: 100, armor1: 50, hp: 270);
        Assert.Equal(100, negative.Armor2);
        Assert.Equal(50, negative.Armor1);
        Assert.Equal(270, negative.Hp);
        Assert.Equal(0, negative.HpLost);
    }

    [Fact]
    public void A_negative_armor_layer_cannot_add_capacity()
    {
        // Nothing should ever write a negative armour value, but if one exists it must be floored at 0
        // rather than subtracting a negative and making the zombie absorb MORE than it has.
        var r = ArmorCascade.Apply(damage: 40, armor2: -100, armor1: 0, hp: 270);

        Assert.Equal(0, r.Armor2);
        Assert.Equal(0, r.Armor1);
        Assert.Equal(230, r.Hp);
        Assert.Equal(40, r.HpLost);
    }

    /// <summary>
    /// The falsifier for the bug itself. Under the old behaviour (damage straight to health) this
    /// zombie's health would drop by the full 80 and both armour layers would be untouched. If a future
    /// change reintroduces the bypass, this is the test that goes red.
    /// </summary>
    [Fact]
    public void Falsifier_armored_and_unarmored_zombies_do_not_lose_the_same_health()
    {
        const long damage = 80;
        const long hp = 270;

        var armored = ArmorCascade.Apply(damage, armor2: 60, armor1: 40, hp: hp);
        var bare = ArmorCascade.Apply(damage, armor2: 0, armor1: 0, hp: hp);

        Assert.Equal(hp, armored.Hp);          // 100 armour > 80 damage — health must not move at all
        Assert.Equal(hp - damage, bare.Hp);    // no armour — the full hit lands
        Assert.NotEqual(armored.Hp, bare.Hp);  // the bug made these equal
    }

    [Theory]
    [InlineData(1, 0, 0, 100)]
    [InlineData(1000, 500, 500, 100)]
    [InlineData(7, 3, 3, 50)]
    [InlineData(250, 100, 100, 270)]
    public void Total_absorbed_always_equals_the_damage_dealt(long damage, long armor2, long armor1, long hp)
    {
        var r = ArmorCascade.Apply(damage, armor2, armor1, hp);

        // Conservation: every point of damage is accounted for by exactly one layer. A cascade that
        // loses or duplicates damage is the defect class this whole function exists in.
        var spent = (armor2 - r.Armor2) + (armor1 - r.Armor1) + r.HpLost;
        Assert.Equal(damage, spent);
        Assert.Equal(hp - r.HpLost, r.Hp);
    }
}
