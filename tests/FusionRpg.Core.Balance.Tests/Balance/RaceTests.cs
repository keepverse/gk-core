using FusionRpg.Core.Balance.Analytic;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.2 — <see cref="Race"/>'s normal-race win probability and its
/// <c>Φ</c> helper, verified against known standard-normal values, the infinite/degenerate edge cases
/// <see cref="FirstPassage"/> can hand it, and the "ρ is not optional" claim
/// (class-analytic-balance-2026-08-25.md §2: dropping it costs 5 points of win rate on a reflect
/// matchup) as a monotonicity property rather than a single hand-computed number.</summary>
public class RaceTests
{
    static FirstPassage.Result Finite(double mean, double variance) => new(mean, variance);
    static readonly FirstPassage.Result NeverDies = new(double.PositiveInfinity, double.PositiveInfinity);

    [Fact]
    public void PWinsA_equalMeansZeroCorrelation_isEven()
    {
        var p = Race.PWinsA(Finite(10, 4), Finite(10, 4), rho: 0.0);
        Assert.Equal(0.5, p, 6);
    }

    [Fact]
    public void PWinsA_aSurvivesFarLonger_aWinsNearCertainly()
    {
        // A's own pool takes 30 rounds to die on average, B's takes 10 -- A outlives B overwhelmingly.
        var p = Race.PWinsA(Finite(30, 9), Finite(10, 9), rho: 0.0);
        Assert.True(p > 0.999, $"expected near-certain A win, got {p}");
    }

    [Fact]
    public void PWinsA_bSurvivesFarLonger_aLosesNearCertainly()
    {
        var p = Race.PWinsA(Finite(10, 9), Finite(30, 9), rho: 0.0);
        Assert.True(p < 0.001, $"expected near-certain A loss, got {p}");
    }

    [Fact]
    public void PWinsA_aNeverDies_bFinite_aWinsWithCertainty()
    {
        Assert.Equal(1.0, Race.PWinsA(NeverDies, Finite(10, 4)));
    }

    [Fact]
    public void PWinsA_bNeverDies_aFinite_aLosesWithCertainty()
    {
        Assert.Equal(0.0, Race.PWinsA(Finite(10, 4), NeverDies));
    }

    [Fact]
    public void PWinsA_neitherSideEverDies_isNaN_notAGuessedCoinFlip()
    {
        // The termination invariant's own failure state (class-system-todo.md P5.1). Race reports it
        // honestly as undefined rather than silently returning 0.5, which could be mistaken for a
        // genuinely balanced matchup instead of a broken one.
        Assert.True(double.IsNaN(Race.PWinsA(NeverDies, NeverDies)));
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    public void PWinsA_rhoOutOfRange_throws(double rho)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Race.PWinsA(Finite(10, 4), Finite(10, 4), rho));
    }

    [Fact]
    public void PWinsA_negativeVarianceArgument_throwsEvenWhenNotBuiltByFirstPassage()
    {
        // FirstPassage.Result is a public record struct -- a caller can construct an invalid one
        // directly, bypassing FirstPassage.Compute's own validation. Race must not trust its inputs.
        var bad = new FirstPassage.Result(10, -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => Race.PWinsA(bad, Finite(10, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Race.PWinsA(Finite(10, 4), bad));
    }

    [Fact]
    public void PWinsA_nanMeanArgument_throws()
    {
        var bad = new FirstPassage.Result(double.NaN, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => Race.PWinsA(bad, Finite(10, 4)));
    }

    [Fact]
    public void PWinsA_higherCorrelation_narrowsTheOutcomeTowardTheLeadingSide()
    {
        // The reflect correction (spec-deterministic-core.md §2.1): a positive rho shrinks the combined
        // variance of the race, pushing the win probability further from 0.5 for the side already ahead
        // on mean. Asserted as a monotonic ordering, not a single hand-derived Phi value -- the shape of
        // the effect is the thing §2.1 says is not optional, not one specific number.
        var a = Finite(15, 4);
        var b = Finite(10, 4);
        var pNoCorrelation = Race.PWinsA(a, b, rho: 0.0);
        var pHighCorrelation = Race.PWinsA(a, b, rho: 0.9);
        Assert.True(pHighCorrelation > pNoCorrelation,
            $"expected rho=0.9 ({pHighCorrelation}) to push further above 0.5 than rho=0 ({pNoCorrelation})");
    }

    [Fact]
    public void PWinsA_degenerateZeroCombinedVariance_higherMeanWinsWithCertainty()
    {
        // Equal SDs (=2) with rho=1 zeroes the combined variance exactly: 4+4-2*1*2*2=0.
        var p = Race.PWinsA(Finite(20, 4), Finite(10, 4), rho: 1.0);
        Assert.Equal(1.0, p);
    }

    [Fact]
    public void PWinsA_degenerateZeroCombinedVariance_tiedMeansIsACoinFlip()
    {
        var p = Race.PWinsA(Finite(10, 4), Finite(10, 4), rho: 1.0);
        Assert.Equal(0.5, p);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.8413447460685429)]
    [InlineData(-1.0, 0.15865525393145705)]
    [InlineData(1.9599639845400545, 0.975)] // the familiar 95% two-sided bound
    [InlineData(2.0, 0.9772498680518208)]
    [InlineData(-2.0, 0.02275013194817921)]
    public void Phi_matchesKnownStandardNormalValues(double x, double expected)
    {
        Assert.Equal(expected, Race.Phi(x), 6);
    }

    [Fact]
    public void Phi_positiveAndNegativeInfinity_areExactlyOneAndZero()
    {
        Assert.Equal(1.0, Race.Phi(double.PositiveInfinity));
        Assert.Equal(0.0, Race.Phi(double.NegativeInfinity));
    }

    [Fact]
    public void Phi_nan_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Race.Phi(double.NaN));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.3)]
    [InlineData(2.7)]
    [InlineData(4.0)]
    public void Phi_isSymmetricAroundOneHalf(double x)
    {
        Assert.Equal(1.0, Race.Phi(x) + Race.Phi(-x), 6);
    }
}
