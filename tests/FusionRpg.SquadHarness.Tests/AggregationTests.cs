using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// docs/architecture/numeric-types.md's numeric-overflow rules, applied to the one aggregation this module owns:
/// <see cref="PairResult"/>'s counts. "506 pairs at 40,000 trials is inside int today and would not be
/// after one --trials change" is the exact defect these tests exist to keep out.
/// </summary>
public class AggregationTests
{
    [Fact]
    public void Counts_are_long_and_overflow_throws()
    {
        // A synthetic count near long.MaxValue throws rather than wrapping (checked, never unchecked).
        var big = long.MaxValue - 1;
        Assert.Throws<OverflowException>(() => checked(big + 5));
    }

    [Fact]
    public void Win_share_divides_by_a_thousand_exactly_once()
    {
        // Hand-computed per-mille table: 30 victories, 10 defeats -> 750 per mille, not 0.75 truncated
        // early and not re-divided.
        long victories = 30, defeats = 10;
        var decided = checked(victories + defeats);
        var winShareMilli = decided == 0 ? 0L : checked(victories * 1000L) / decided;
        Assert.Equal(750L, winShareMilli);
    }

    [Fact]
    public void Stalemates_leave_the_denominator()
    {
        // decisions.md:103 -- stalemates are excluded from the denominator and reported separately.
        // A 40/30/30 outcome split scores victories/(victories+defeats) = 40/70 = 571 per mille (not
        // 400, which is what dividing by the FULL 100 trials would give).
        long victories = 40, defeats = 30, stalemates = 30;
        var decided = checked(victories + defeats);
        var winShareMilli = checked(victories * 1000L) / decided;
        Assert.Equal(571L, winShareMilli);
        Assert.Equal(30L, stalemates); // reported, never folded into the denominator
    }

    [Theory]
    [InlineData(75, 5, 20, false)]  // 20% stalemates == the 20% bar exactly -> not OVER it, not flagged
    [InlineData(75, 4, 21, true)]   // 21% stalemates -> over the bar, flagged
    [InlineData(90, 5, 5, false)]   // 5% stalemates -> comfortably under
    public void A_high_stalemate_cell_is_refused_not_scored(long victories, long defeats, long stalemates, bool expectedLowConfidence)
    {
        var result = new PairResult("a", "b", victories, defeats, stalemates, 0);
        Assert.Equal(expectedLowConfidence, SquadMatch.IsLowConfidence(result));
    }

    [Fact]
    public void A_missing_theta_is_rejected_rather_than_defaulted_to_zero()
    {
        var spec = new RunSpec(Theta: 0, Trials: 1, RunSeed: 1);
        var entryA = new RosterEntry("a", new[] { BuildFactory.Build("Might") });
        var entryB = new RosterEntry("b", new[] { BuildFactory.Build("Agility") });
        Assert.Throws<ArgumentOutOfRangeException>(() => SquadMatch.Measure(entryA, entryB, spec));
    }

    [Fact]
    public void A_theta_above_intmaxvalue_is_rejected_loudly_rather_than_cast()
    {
        var spec = new RunSpec(Theta: (long)int.MaxValue + 1, Trials: 1, RunSeed: 1);
        var entryA = new RosterEntry("a", new[] { BuildFactory.Build("Might") });
        var entryB = new RosterEntry("b", new[] { BuildFactory.Build("Agility") });
        Assert.Throws<ArgumentOutOfRangeException>(() => SquadMatch.Measure(entryA, entryB, spec));
    }
}
