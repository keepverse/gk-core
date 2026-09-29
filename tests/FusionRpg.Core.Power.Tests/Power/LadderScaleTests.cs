using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// `species-progression` module 2, `ladder-scale-parity` (map C6) — `LadderScale.Micro` is the ONE
/// `k · P(Θ)` product for a per-million coefficient; `AptitudeReadFunctions.Magnitude` and
/// `AtomCompiler`'s projected-atom path both call it, so a projected 2b atom resolved at Θ equals the
/// live aptitude resolve at Θ by construction.
/// </summary>
public class LadderScaleTests
{
    [Theory]
    [InlineData(0L, 0L, 0L)]
    [InlineData(1_000_000L, 1L, 1L)]
    [InlineData(2_500_000L, 4L, 10L)]
    [InlineData(1L, 1_000_000L, 1L)]
    public void Exact_quotient_rounds_to_the_expected_long(long kMicro, long pTheta, long expected)
    {
        Assert.Equal(expected, LadderScale.Micro(kMicro, pTheta));
    }

    [Fact]
    public void Rounds_away_from_zero_at_the_half_step()
    {
        // 1_500_000 / 1_000_000 = 1.5 -> rounds to 2, never banker's-rounds to 2 by luck and never
        // truncates to 1.
        Assert.Equal(2L, LadderScale.Micro(1_500_000L, 1L));
        // 500_000 / 1_000_000 = 0.5 -> rounds to 1 (away from zero), never truncates to 0.
        Assert.Equal(1L, LadderScale.Micro(500_000L, 1L));
    }

    [Fact]
    public void A_negative_kMicro_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LadderScale.Micro(-1L, 100L));
    }

    [Fact]
    public void A_negative_pTheta_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LadderScale.Micro(100L, -1L));
    }

    [Fact]
    public void The_product_that_overflows_long_before_the_divide_still_resolves_correctly()
    {
        // The whole point of the decimal widening: kMicro * pTheta here is far past long.MaxValue
        // (checked long*long would throw spuriously), but the TRUE quotient fits comfortably.
        const long kMicro = 3_000_000_000L;   // 3000x scale
        const long pTheta = 2_000_000_000L;   // 2 billion
        // True product = 6e18, true quotient = 6e12 -- well within long range, but the raw product
        // itself (6e18) is close to long.MaxValue (~9.22e18); push further to prove the widening.
        Assert.Equal(6_000_000_000_000L, LadderScale.Micro(kMicro, pTheta));

        const long hugeKMicro = 9_000_000_000_000_000_000L; // ~9.2e18, itself near long.MaxValue
        const long smallPTheta = 500_000L; // 0.5 in per-million terms
        // hugeKMicro * smallPTheta overflows long many times over; the TRUE quotient
        // (hugeKMicro * 0.5) is still within long range.
        var expected = (long)Math.Round((decimal)hugeKMicro * smallPTheta / 1_000_000m, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, LadderScale.Micro(hugeKMicro, smallPTheta));
    }

    [Fact]
    public void Overflow_throws_never_wraps_or_clamps_when_the_true_quotient_does_not_fit()
    {
        Assert.Throws<OverflowException>(() => LadderScale.Micro(long.MaxValue, long.MaxValue));
    }

    // ---- parity with the live aptitude read (SP2.1's own acceptance line) -------------------------

    [Theory]
    [InlineData(0L, 0.0, 1000L, 0L)]
    [InlineData(100L, 0.5, 1000L, 1_000_000L)]
    [InlineData(1000L, 1.0, 2000L, 5_000_000_000L)]
    [InlineData(500L, 0.25, 500L, 9_223_372_036L)]                    // edge: pushes kMicro*pTheta near overflow
    [InlineData(long.MaxValue / 1_000_000, 1.0, 1000L, 123_456_789L)] // large kMilli, kMicro*pTheta > long.MaxValue, quotient fits
    public void Magnitude_equals_LadderScale_over_the_grid_including_the_overflow_edge(
        long kMilli, double share, long shareExponentMilli, long pTheta)
    {
        var gamma = shareExponentMilli / 1000.0;
        var sharePowMilli = (long)Math.Round(Math.Pow(share, gamma) * 1000.0, MidpointRounding.AwayFromZero);
        var kMicro = checked(kMilli * sharePowMilli);

        var viaAptitudeRead = AptitudeReadFunctions.Magnitude(kMilli, share, shareExponentMilli, pTheta);
        var viaLadderScale = LadderScale.Micro(kMicro, pTheta);

        Assert.Equal(viaLadderScale, viaAptitudeRead);
    }

    [Fact]
    public void Both_overflow_at_the_same_point()
    {
        // kMilli=2000, share=1.0 -> kMicro=2_000_000 (k=2 in true units); the TRUE quotient against
        // long.MaxValue is ~2x long.MaxValue -- a genuine "true answer does not fit a long" case, not
        // merely a raw-product overflow decimal already absorbs. Same true-overflow input from both
        // entry points -- the refactor did not change WHEN it throws.
        Assert.Throws<OverflowException>(() =>
            AptitudeReadFunctions.Magnitude(2000L, 1.0, 1000L, long.MaxValue));
        Assert.Throws<OverflowException>(() =>
            LadderScale.Micro(2_000_000L, long.MaxValue));
    }
}
