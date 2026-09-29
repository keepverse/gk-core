using FusionRpg.Core.World.Loam;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>L8 acceptance (spec-loam-calc.md #5): fade is graded, and recovery is strictly slower than decay.</summary>
public class FadePolicyTests
{
    [Fact]
    public void A_surplus_recovers_stability_by_the_fixed_recovery_rate()
    {
        Assert.Equal(500 + LoamPolicy.RecoveryMilli, FadePolicy.Apply(500, balance: 10));
    }

    [Fact]
    public void A_shortfall_lowers_stability_by_at_least_the_base_decay()
    {
        var result = FadePolicy.Apply(500, balance: -1);
        Assert.Equal(500 - LoamPolicy.BaseDecayMilli, result);
    }

    [Fact]
    public void A_deeper_shortfall_decays_more_but_never_past_the_ceiling()
    {
        var shallow = FadePolicy.DecayFor(deficitMagnitude: 1);
        var deep = FadePolicy.DecayFor(deficitMagnitude: 10_000);

        Assert.True(deep > shallow, "a deeper shortfall must decay more");
        Assert.Equal(LoamPolicy.MaxDecayMilli, deep);
    }

    [Fact]
    public void Recovery_is_strictly_slower_than_decay_at_every_depth()
    {
        // The asymmetry the module exists to guarantee: even the shallowest possible shortfall
        // decays faster than any surplus recovers, so a sector oscillating on the boundary trends
        // downward rather than flickering forever.
        Assert.True(LoamPolicy.RecoveryMilli < FadePolicy.DecayFor(deficitMagnitude: 1));
    }

    [Fact]
    public void Stability_never_leaves_the_zero_to_one_thousand_band()
    {
        Assert.Equal(0, FadePolicy.Apply(currentStabilityMilli: 10, balance: -1_000_000));
        Assert.Equal(1000, FadePolicy.Apply(currentStabilityMilli: 995, balance: 1));
    }
}
