using FusionRpg.Core.Balance.Analytic;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.4 — <see cref="StatusUptime"/>'s refresh-not-stack uptime formula
/// and the "rides the action-multiplied hit" rule (spec-deterministic-core.md §2.1 correction 4,
/// §2.2's DoT over-count incident).</summary>
public class StatusUptimeTests
{
    // ---- Uptime -------------------------------------------------------------------------------

    [Fact]
    public void Uptime_zeroProbability_isZeroRegardlessOfDuration()
    {
        Assert.Equal(0.0, StatusUptime.Uptime(0.0, 50));
    }

    [Fact]
    public void Uptime_certainProbability_isOneForAnyPositiveDuration()
    {
        Assert.Equal(1.0, StatusUptime.Uptime(1.0, 1));
        Assert.Equal(1.0, StatusUptime.Uptime(1.0, 500));
    }

    [Fact]
    public void Uptime_zeroDuration_isZeroRegardlessOfProbability()
    {
        // No window to be active in, even at p=1 -- Math.Pow(0,0)=1 by IEEE convention, so
        // 1 - (1-1)^0 = 1 - 1 = 0. A status that instantly expires contributes no uptime.
        Assert.Equal(0.0, StatusUptime.Uptime(1.0, 0));
        Assert.Equal(0.0, StatusUptime.Uptime(0.5, 0));
    }

    [Fact]
    public void Uptime_handComputedCase()
    {
        // 1 - (1-0.5)^3 = 1 - 0.125 = 0.875
        Assert.Equal(0.875, StatusUptime.Uptime(0.5, 3), 12);
    }

    [Theory]
    [InlineData(0.1, 5)]
    [InlineData(0.5, 20)]
    [InlineData(0.9, 1000)]
    [InlineData(0.01, 3)]
    public void Uptime_isAlwaysBoundedInZeroToOne(double p, double duration)
    {
        var u = StatusUptime.Uptime(p, duration);
        Assert.InRange(u, 0.0, 1.0);
    }

    [Fact]
    public void Uptime_neverExceedsOne_evenWhereNaivePTimesDurationWouldByALargeMargin()
    {
        // The whole point of the fix (§2.2): p=0.9, duration=1000 -- naive p*duration = 900 (a DoT
        // "on" for 900 expected rounds out of a duration-1000 window is nonsensical under
        // refresh-not-stack). The uptime form saturates just under 1 instead.
        var naive = 0.9 * 1000;
        var uptime = StatusUptime.Uptime(0.9, 1000);
        Assert.True(naive > 1.0, "sanity: the naive form should indeed blow past 1 here");
        Assert.True(uptime <= 1.0);
        Assert.True(uptime > 0.99, $"expected near-saturation, got {uptime}");
    }

    [Fact]
    public void Uptime_increasingP_holdingDurationFixed_isMonotonicallyIncreasing()
    {
        var low = StatusUptime.Uptime(0.1, 10);
        var high = StatusUptime.Uptime(0.6, 10);
        Assert.True(high > low);
    }

    [Fact]
    public void Uptime_increasingDuration_holdingPFixed_isMonotonicallyIncreasing()
    {
        var shorter = StatusUptime.Uptime(0.3, 5);
        var longer = StatusUptime.Uptime(0.3, 50);
        Assert.True(longer > shorter);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Uptime_pOutOfRange_throws(double p)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusUptime.Uptime(p, 10));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Uptime_negativeOrNanDuration_throws(double duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusUptime.Uptime(0.5, duration));
    }

    // ---- EffectiveMagnitude ---------------------------------------------------------------------

    [Fact]
    public void EffectiveMagnitude_theDocumentedExample_skillStrikeAtOnePointEight()
    {
        // spec-deterministic-core.md §2.1: "a skill-strike at x1.8 applies a x1.8 status."
        Assert.Equal(45.0, StatusUptime.EffectiveMagnitude(25.0, 1.8), 9);
    }

    [Fact]
    public void EffectiveMagnitude_unmultipliedAction_returnsTheAuthoredBaseUnchanged()
    {
        Assert.Equal(25.0, StatusUptime.EffectiveMagnitude(25.0, 1.0), 9);
    }

    [Fact]
    public void EffectiveMagnitude_zeroMultiplierAction_nullifiesTheStatusToo()
    {
        Assert.Equal(0.0, StatusUptime.EffectiveMagnitude(25.0, 0.0));
    }

    [Fact]
    public void EffectiveMagnitude_nanBaseMagnitude_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusUptime.EffectiveMagnitude(double.NaN, 1.0));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void EffectiveMagnitude_negativeOrNanActionMultiplier_throws(double multiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusUptime.EffectiveMagnitude(25.0, multiplier));
    }
}
