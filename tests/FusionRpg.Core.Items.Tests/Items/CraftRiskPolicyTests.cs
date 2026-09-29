using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T24 — the craft-risk ladder's policy. Stage 1 (Assured) and Stage 3 (the at-zero
/// filter) shipped with T10/T11; this covers the Stage 2 decision and its decrement, which are pure and
/// read no tuning of their own (the rate arrives as an argument, off the craft-wear key alone).
/// </summary>
public class CraftRiskPolicyTests
{
    [Theory]
    [InlineData(null, CraftRiskStage.Assured)]   // not yet derived is NEVER mistaken for exhausted
    [InlineData(1L, CraftRiskStage.Assured)]
    [InlineData(0L, CraftRiskStage.Exhausted)]
    public void StageFor_resolves_from_the_stored_pair(long? potentialCurrent, CraftRiskStage expected) =>
        Assert.Equal(expected, CraftRiskPolicy.StageFor(potentialCurrent));

    [Fact]
    public void CanDecay_is_true_only_for_an_exhausted_pair()
    {
        Assert.False(CraftRiskPolicy.CanDecay(null));
        Assert.False(CraftRiskPolicy.CanDecay(5));
        Assert.True(CraftRiskPolicy.CanDecay(0));
    }

    [Theory]
    [InlineData(1000L, 50L, 50L)]
    [InlineData(333L, 50L, 17L)]   // ceil(16.65) — a worn item never gets a free craft off rounding
    [InlineData(1L, 1L, 1L)]       // any wear at all is at least one unit
    [InlineData(1000L, 0L, 0L)]    // a zero rate is the documented no-op
    public void WearFor_is_the_ceiling_of_max_times_the_craft_wear_key(long max, long milli, long expected) =>
        Assert.Equal(expected, CraftRiskPolicy.WearFor(max, milli));

    [Fact]
    public void WearFor_throws_rather_than_wrapping() =>
        Assert.Throws<OverflowException>(() => CraftRiskPolicy.WearFor(long.MaxValue, 1000));

    [Theory]
    [InlineData(100L, 30L, 70L)]
    [InlineData(10L, 30L, 0L)]   // floors at zero: unusable, never destroyed by wear (D1)
    public void AfterWear_floors_at_zero(long current, long wear, long expected) =>
        Assert.Equal(expected, CraftRiskPolicy.AfterWear(current, wear));
}
