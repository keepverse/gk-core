using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §9.2's worked table: "1.8pp at 3,000 trials... ~1.0pp at ~9,600... ~0.5pp at
/// ~38,400" for <c>1.96 * sqrt(0.25/n)</c>. These pin <see cref="Resolution.HalfWidthMilli"/> against
/// that exact table so a change to the formula (or its constants) is caught immediately.
/// </summary>
public class ResolutionTests
{
    [Fact]
    public void HalfWidth_matches_the_specs_own_worked_table_at_3000_trials()
    {
        // 1.96 * sqrt(0.25/3000) = 0.017929... = 1.79pp, which rounds UP to 18 per-mille (1.8pp).
        Assert.Equal(18L, Resolution.HalfWidthMilli(3000));
    }

    [Fact]
    public void HalfWidth_matches_the_specs_own_worked_table_at_9600_trials()
    {
        // Spec writes this row as "~9,600" for a ~1.0pp target -- the trial count itself is
        // approximate (rounded to a nice number), and HalfWidthMilli rounds UP, so the exact value at
        // n=9600 lands just over 10 (10.002...pm -> ceiling 11). Asserted as a range against the exact
        // formula, not a brittle pinned integer that a rounding-direction artifact would break.
        var exactPermille = Resolution.Z95 * Math.Sqrt(Resolution.MaxBernoulliVariance / 9600.0) * 1000.0;
        Assert.InRange(exactPermille, 9.5, 10.5);
        Assert.Equal((long)Math.Ceiling(exactPermille), Resolution.HalfWidthMilli(9600));
    }

    [Fact]
    public void HalfWidth_matches_the_specs_own_worked_table_at_38400_trials()
    {
        var exactPermille = Resolution.Z95 * Math.Sqrt(Resolution.MaxBernoulliVariance / 38400.0) * 1000.0;
        Assert.InRange(exactPermille, 4.5, 5.5);
        Assert.Equal((long)Math.Ceiling(exactPermille), Resolution.HalfWidthMilli(38400));
    }

    [Fact]
    public void HalfWidth_at_zero_decided_trials_is_maximally_wide()
    {
        Assert.Equal(1000L, Resolution.HalfWidthMilli(0));
    }

    [Fact]
    public void HalfWidth_rounds_up_never_down()
    {
        // A half-width that rounded DOWN would understate its own uncertainty -- the exact failure
        // mode this module exists to avoid (spec §9.2's whole point).
        var exact = Resolution.Z95 * Math.Sqrt(Resolution.MaxBernoulliVariance / 5000.0) * 1000.0;
        var reported = Resolution.HalfWidthMilli(5000);
        Assert.True(reported >= exact, $"reported {reported} must be >= exact {exact}");
    }

    [Fact]
    public void CombinedHalfWidth_is_root_sum_square_not_sum_or_max()
    {
        // 3-4-5 triangle: sqrt(3^2 + 4^2) = 5, never 3+4=7 (double-counts) or max(3,4)=4 (ignores the
        // smaller estimate's own uncertainty).
        Assert.Equal(5L, Resolution.CombinedHalfWidthMilli(3, 4));
    }

    [Fact]
    public void CannotCallWinner_is_true_exactly_when_the_gap_to_500_permille_does_not_clear_the_half_width()
    {
        // decided=3000 -> half-width 18pm. 520 is 20pm from 500: clears it (not the coin-flip line).
        Assert.False(Resolution.CannotCallWinner(520, 3000));
        // 510 is 10pm from 500: inside the 18pm half-width -> cannot call a winner yet.
        Assert.True(Resolution.CannotCallWinner(510, 3000));
    }

    [Fact]
    public void CannotCallWinner_is_true_when_nothing_was_decided()
    {
        Assert.True(Resolution.CannotCallWinner(500, 0));
    }

    [Fact]
    public void GapIsInsideHalfWidth_is_true_only_when_the_gap_does_not_exceed_the_combined_half_width()
    {
        // gap = |600-590| = 10; combined half-width = sqrt(5^2+5^2) ~= 7.07 -> ceil 8. 10 > 8: resolved.
        Assert.False(Resolution.GapIsInsideHalfWidth(600, 5, 590, 5));
        // gap = |600-595| = 5 <= combined 8: NOT resolved -- "cannot separate".
        Assert.True(Resolution.GapIsInsideHalfWidth(600, 5, 595, 5));
    }

    [Fact]
    public void GapIsInsideHalfWidth_resting_exactly_on_the_boundary_is_still_inside_it()
    {
        // gap == combined half-width exactly -- spec's own wording is "rests ON a gap inside its own
        // half-width", so equality counts as unresolved, never a coin-flip toward "resolved".
        Assert.True(Resolution.GapIsInsideHalfWidth(104, 4, 100, 0)); // gap 4 == combined 4 -> unresolved
        Assert.False(Resolution.GapIsInsideHalfWidth(105, 4, 100, 0)); // gap 5 > combined 4 -> resolved
    }
}
