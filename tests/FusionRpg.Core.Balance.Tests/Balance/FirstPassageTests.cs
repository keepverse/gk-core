using FusionRpg.Core.Balance.Analytic;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.2 — <see cref="FirstPassage"/>'s renewal mean/variance
/// (<c>E[T]=h/μ</c>, <c>Var[T]=h·σ²/μ³</c>), verified against hand-computed cases and the
/// Θ-invariance-by-homogeneity argument (spec-deterministic-core.md §6 test 2; the module-level
/// exact-invariance test belongs to <c>PredictorTests</c>, P4.6, but the property already holds at
/// this layer and is worth locking in here).</summary>
public class FirstPassageTests
{
    [Fact]
    public void Compute_handComputedCase_matchesExactly()
    {
        // h=1000, mu=25, sigma^2=100 (sd=10, CV=0.4 -- deliberately not 1, so Mean and Variance
        // formulas cannot coincidentally agree the way they would at CV=1).
        var r = FirstPassage.Compute(poolSize: 1000, mean: 25, variance: 100);
        Assert.Equal(40.0, r.Mean, 9);
        Assert.Equal(6.4, r.Variance, 9);
    }

    [Fact]
    public void Compute_zeroPoolSize_isInstantRegardlessOfRate()
    {
        var r = FirstPassage.Compute(poolSize: 0, mean: 25, variance: 100);
        Assert.Equal(0.0, r.Mean);
        Assert.Equal(0.0, r.Variance);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Compute_nonPositiveMean_neverDepletesThePool(double mean)
    {
        var r = FirstPassage.Compute(poolSize: 500, mean: mean, variance: 10);
        Assert.True(double.IsPositiveInfinity(r.Mean));
        Assert.True(double.IsPositiveInfinity(r.Variance));
    }

    [Fact]
    public void Compute_negativePoolSize_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FirstPassage.Compute(-1, 10, 10));
    }

    [Fact]
    public void Compute_negativeVariance_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FirstPassage.Compute(100, 10, -1));
    }

    [Fact]
    public void Compute_nanInputs_eachThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FirstPassage.Compute(double.NaN, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => FirstPassage.Compute(100, double.NaN, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => FirstPassage.Compute(100, 10, double.NaN));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(7.5)]
    [InlineData(0.1)]
    public void Compute_scalingPoolAndDamageByACommonFactor_leavesMeanAndVarianceExactlyUnchanged(double c)
    {
        // Homogeneity argument (ssot-power-scale.md's PS-3 reasoning applied here): if pool and mean
        // damage both scale linearly with a common factor (as they do under the power ladder) and
        // variance scales as its square, E[T] and Var[T] are exactly invariant. This is the unit-level
        // root of the module-level "Win_rate_is_exactly_theta_invariant" test spec-deterministic-core.md
        // §6 requires of Predictor (P4.6) -- if this identity ever breaks, that test breaks too, so it
        // is worth pinning here where the arithmetic is simplest to reason about.
        var baseline = FirstPassage.Compute(poolSize: 800, mean: 40, variance: 900);
        var scaled = FirstPassage.Compute(poolSize: 800 * c, mean: 40 * c, variance: 900 * c * c);

        Assert.Equal(baseline.Mean, scaled.Mean, 9);
        Assert.Equal(baseline.Variance, scaled.Variance, 9);
    }
}
