using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E37 (spec-projectile-control.md §2b/§3, criterion 8). The pure arithmetic behind a bound
/// <c>bullet.modify</c> grant, extracted from <c>CheatPrefixes.BulletInitCheat</c> specifically so the
/// overflow-throw contract is provable in CI without a live game host (see
/// <see cref="BulletModifyMath"/>'s own class doc).
/// </summary>
public class BulletModifyMathTests
{
    [Fact]
    public void Set_replaces_the_current_damage_outright()
    {
        Assert.Equal(500, BulletModifyMath.Apply(100, "set", 500));
    }

    [Fact]
    public void Add_is_whole_damage_units()
    {
        Assert.Equal(150, BulletModifyMath.Apply(100, "add", 50));
    }

    [Fact]
    public void Add_can_reduce_damage_with_a_negative_amount()
    {
        Assert.Equal(70, BulletModifyMath.Apply(100, "add", -30));
    }

    // scale is per-mille: amount 1500 = x1.5.
    [Fact]
    public void Scale_1500_is_one_point_five_times()
    {
        Assert.Equal(150, BulletModifyMath.Apply(100, "scale", 1500));
    }

    [Fact]
    public void Scale_500_halves_the_damage()
    {
        Assert.Equal(50, BulletModifyMath.Apply(100, "scale", 500));
    }

    [Fact]
    public void Scale_1000_is_identity()
    {
        Assert.Equal(100, BulletModifyMath.Apply(100, "scale", 1000));
    }

    // Criterion 8: op:scale over a damage near int.MaxValue throws — never wraps, never clamps.
    [Fact]
    public void Scale_over_a_damage_near_int_MaxValue_throws_rather_than_wrapping_or_clamping()
    {
        Assert.Throws<OverflowException>(() =>
            BulletModifyMath.Apply(int.MaxValue - 10, "scale", 2000));
    }

    [Fact]
    public void Set_of_a_value_outside_int_range_throws()
    {
        Assert.Throws<OverflowException>(() =>
            BulletModifyMath.Apply(100, "set", (long)int.MaxValue + 1));
    }

    [Fact]
    public void Add_that_overflows_int_throws()
    {
        Assert.Throws<OverflowException>(() =>
            BulletModifyMath.Apply(int.MaxValue, "add", int.MaxValue));
    }
}
