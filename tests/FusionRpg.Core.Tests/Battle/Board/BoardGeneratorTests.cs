using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>
/// A10 `battle-board` (spec-battle-board.md §1) — `BoardGenerator`'s own test table, per the spec's
/// own testing strategy: "same seed, same board" and "bounded dimensions... over a large seed sweep",
/// not a handful of hand-picked cases.
/// </summary>
public class BoardGeneratorTests
{
    [Fact]
    public void Same_seed_produces_the_same_board_across_two_independent_calls()
    {
        // Two independent calls, not one instance reused twice -- a shared instance would hide
        // state leakage the spec's own testing strategy calls out by name.
        var a = BoardGenerator.Generate(12345UL);
        var b = BoardGenerator.Generate(12345UL);

        Assert.Equal(a.Rows, b.Rows);
        Assert.Equal(a.Cols, b.Cols);
    }

    [Fact]
    public void Different_seeds_are_not_guaranteed_the_same_but_the_roll_is_not_constant()
    {
        var sizes = new HashSet<int>();
        for (ulong seed = 0; seed < 50; seed++)
            sizes.Add(BoardGenerator.Generate(seed).Rows);

        // Over 50 seeds in a [5,9] interval, seeing only one distinct value would mean the roll is
        // not actually reading the seed at all -- a real regression, not bad luck (5 possible values,
        // 50 draws).
        Assert.True(sizes.Count > 1, $"50 seeds all produced side length(s) {{{string.Join(",", sizes)}}} -- the roll ignores its seed");
    }

    [Fact]
    public void Every_rolled_side_stays_inside_the_tuned_bounded_interval_over_a_large_seed_sweep()
    {
        for (ulong seed = 0; seed < 2000; seed++)
        {
            var spec = BoardGenerator.Generate(seed);
            Assert.InRange(spec.Rows, BattleBoardTuningPolicy.MinSide, BattleBoardTuningPolicy.MaxSide);
            Assert.Equal(spec.Rows, spec.Cols); // square, per the spec's own two worked examples
        }
    }

    [Fact]
    public void A_minSide_floor_widens_a_board_that_would_otherwise_roll_too_small_to_seat_a_real_roster()
    {
        // minSide above the tuned MaxSide forces every seed to widen -- proves the floor actually
        // overrides the roll rather than merely coexisting with it by coincidence.
        var floor = BattleBoardTuningPolicy.MaxSide + 5;
        for (ulong seed = 0; seed < 20; seed++)
        {
            var spec = BoardGenerator.Generate(seed, minSide: floor);
            Assert.Equal(floor, spec.Rows);
            Assert.Equal(floor, spec.Cols);
        }
    }

    [Fact]
    public void A_minSide_at_or_below_the_tuned_minimum_never_changes_the_roll()
    {
        var a = BoardGenerator.Generate(777UL);
        var b = BoardGenerator.Generate(777UL, minSide: 0);
        var c = BoardGenerator.Generate(777UL, minSide: BattleBoardTuningPolicy.MinSide);

        Assert.Equal(a.Rows, b.Rows);
        Assert.Equal(a.Rows, c.Rows);
    }
}
