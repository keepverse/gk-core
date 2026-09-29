using FusionRpg.Core.Delve.Difficulty;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Difficulty;

/// <summary>
/// D1.22 — spec-difficulty-ladder.md §7's contest table, reproduced through the shipped
/// <see cref="ActorThetaSeam"/> rather than a private formula, plus the property test the spec's
/// own Testing Strategy demands: "a difference in Θ moves the contest monotonically" and parity
/// holds at `0.900 ± 0.02` for any absolute θ.
/// </summary>
public class ActorThetaSeamTests
{
    // Every term is a function of gap alone (the absolute Θ cancels) -- fixing actorTheta at an
    // arbitrary baseline and varying contentTheta = actorTheta + gap exercises exactly what §7 tabulates.
    const int ActorBaseline = 20;

    [Theory]
    [InlineData(0, 0.900, 0.900, 0.076, 0.076)]
    [InlineData(5, 0.711, 0.971, 0.047, 0.119)]
    [InlineData(10, 0.401, 0.992, 0.029, 0.182)]
    [InlineData(15, 0.155, 0.998, 0.018, 0.269)]
    [InlineData(20, 0.047, 0.999, 0.011, 0.378)]
    [InlineData(35, 0.001, 1.000, 0.002, 0.731)]
    public void The_contest_table_reproduces_section_7_to_within_0_001(
        int gap, double expectedOurHit, double expectedTheirHit, double expectedOurCrit, double expectedTheirCrit)
    {
        var (ourHit, theirHit, ourCrit, theirCrit) = ActorThetaSeam.Contest(contentTheta: ActorBaseline + gap, actorTheta: ActorBaseline);

        Assert.Equal(expectedOurHit, ourHit, 0.001);
        Assert.Equal(expectedTheirHit, theirHit, 0.001);
        Assert.Equal(expectedOurCrit, ourCrit, 0.001);
        Assert.Equal(expectedTheirCrit, theirCrit, 0.001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(100_000)]
    public void Parity_stays_at_0_900_within_0_02_for_any_absolute_theta(int theta)
    {
        var (ourHit, theirHit, _, _) = ActorThetaSeam.Contest(contentTheta: theta, actorTheta: theta);
        Assert.Equal(0.900, ourHit, 0.02);
        Assert.Equal(0.900, theirHit, 0.02);
    }

    [Fact]
    public void A_difference_in_theta_moves_the_contest_monotonically()
    {
        var gaps = new[] { -20, -10, -5, 0, 5, 10, 15, 20, 35, 60, 100 };
        double? prevOurHit = null, prevTheirHit = null, prevOurCrit = null, prevTheirCrit = null;

        foreach (var gap in gaps)
        {
            var (ourHit, theirHit, ourCrit, theirCrit) = ActorThetaSeam.Contest(contentTheta: ActorBaseline + gap, actorTheta: ActorBaseline);

            // As the content side pulls further ahead (gap grows), our odds fall and theirs rise --
            // strictly, since every sample gap here is distinct and the sigmoid is strictly monotonic.
            if (prevOurHit is { } po) Assert.True(ourHit < po, $"ourHit must strictly decrease at gap {gap}");
            if (prevTheirHit is { } pt) Assert.True(theirHit > pt, $"theirHit must strictly increase at gap {gap}");
            if (prevOurCrit is { } poc) Assert.True(ourCrit < poc, $"ourCrit must strictly decrease at gap {gap}");
            if (prevTheirCrit is { } ptc) Assert.True(theirCrit > ptc, $"theirCrit must strictly increase at gap {gap}");

            prevOurHit = ourHit; prevTheirHit = theirHit; prevOurCrit = ourCrit; prevTheirCrit = theirCrit;
        }
    }

    [Fact]
    public void Every_probability_stays_within_the_legal_0_to_1_range()
    {
        foreach (var gap in new[] { -1000, -50, 0, 50, 1000 })
        {
            var (ourHit, theirHit, ourCrit, theirCrit) = ActorThetaSeam.Contest(contentTheta: ActorBaseline + gap, actorTheta: ActorBaseline);
            foreach (var p in new[] { ourHit, theirHit, ourCrit, theirCrit })
            {
                Assert.InRange(p, 0.0, 1.0);
            }
        }
    }
}
