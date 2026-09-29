using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>base-defense siege-board (spec-siege-board.md). `GridSpec`'s own test table.</summary>
public class GridSpecTests
{
    [Fact]
    public void Chebyshev_matches_the_shipped_square_shape()
    {
        // The existing invariant, proven against a real board rather than bare GridPos math: the
        // shipped `Square` area shape of size n is exactly the cells within Chebyshev radius (n-1)/2.
        var spec = new GridSpec(11, 11);
        var center = new GridPos(5, 5);
        const int size = 5;
        var radius = (size - 1) / 2;

        var square = GridDistance.Square(center, size).ToHashSet();

        for (var r = 0; r < spec.Rows; r++)
        for (var c = 0; c < spec.Cols; c++)
        {
            var p = new GridPos(r, c);
            var inChebyshevBall = GridDistance.Chebyshev(center, p) <= radius;
            Assert.Equal(inChebyshevBall, square.Contains(p));
        }
    }

    [Fact]
    public void Index_round_trips_for_every_cell()
    {
        // Non-square, both dimensions different from each other -- a square board hides a
        // row/col transposition bug.
        var spec = new GridSpec(4, 7);
        for (var r = 0; r < spec.Rows; r++)
        for (var c = 0; c < spec.Cols; c++)
        {
            var p = new GridPos(r, c);
            var index = spec.IndexOf(p);
            Assert.True(index >= 0 && index < spec.Rows * spec.Cols);
            Assert.Equal(CellTerrain.Open, spec.TerrainAt(p)); // default board is all Open
        }

        // Every index is unique -- no two cells collide.
        var seen = new HashSet<int>();
        for (var r = 0; r < spec.Rows; r++)
        for (var c = 0; c < spec.Cols; c++)
            Assert.True(seen.Add(spec.IndexOf(new GridPos(r, c))));
    }

    [Fact]
    public void Out_of_bounds_index_throws_rather_than_wrapping()
    {
        var spec = new GridSpec(3, 3);
        Assert.Throws<GridSpecRejection>(() => spec.IndexOf(new GridPos(-1, 0)));
        Assert.Throws<GridSpecRejection>(() => spec.IndexOf(new GridPos(0, 3)));
        Assert.False(spec.Contains(new GridPos(3, 0)));
    }

    [Fact]
    public void Max_cells_is_enforced_loudly()
    {
        // board.maxCells is 4096 in this test bootstrap's DefaultSiege (gk-core/data/tuning/siege.v1.json's
        // own shipped value). A 5000-cell spec must throw at construction, not at render.
        Assert.Throws<GridSpecRejection>(() => new GridSpec(100, 50)); // 5000 cells
    }

    [Fact]
    public void Cell_count_mismatch_throws()
    {
        Assert.Throws<GridSpecRejection>(() => new GridSpec(3, 3, new[] { CellTerrain.Open, CellTerrain.Open }));
    }

    [Fact]
    public void Nonpositive_dimensions_throw()
    {
        Assert.Throws<GridSpecRejection>(() => new GridSpec(0, 5));
        Assert.Throws<GridSpecRejection>(() => new GridSpec(5, 0));
        Assert.Throws<GridSpecRejection>(() => new GridSpec(-1, 5));
    }
}
