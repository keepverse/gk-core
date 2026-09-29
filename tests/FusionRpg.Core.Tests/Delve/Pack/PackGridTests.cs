using FusionRpg.Core.Delve.Pack;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>D3.18 (spec-loot-pack.md §1) — the grid data structure `PackArranger` (D3.20) places
/// items into. "4 × 10 = 40 cells in every mode" is a starting shape read externally by the caller
/// (`raid.modes.*.pack.{rows,cols}`); this file tests the shape-agnostic mechanism, not that one
/// specific tuning value.</summary>
public class PackGridTests
{
    static PackItem Item(int w, int h, int grantIndex = 0, string refId = "helm-a") =>
        new(Kind: "Equipment", RefId: refId, InstanceId: "inst-1", Qty: 1, W: w, H: h, GrantIndex: grantIndex, Origin: PackItemOrigin.Haul);

    [Fact]
    public void Rejects_non_positive_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackGrid(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackGrid(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackGrid(-1, 10));
    }

    [Fact]
    public void A_fresh_grid_is_free_everywhere_inside_its_own_bounds()
    {
        var grid = new PackGrid(4, 10);
        Assert.True(grid.IsFree(0, 0, 1, 1));
        Assert.True(grid.IsFree(3, 9, 1, 1)); // bottom-right corner
        Assert.True(grid.IsFree(0, 0, 10, 4)); // the whole 10-wide x 4-tall grid at once
    }

    [Fact]
    public void IsFree_respects_grid_bounds_on_every_edge()
    {
        var grid = new PackGrid(4, 10);
        Assert.True(grid.IsFree(0, 0, 10, 4));   // exactly fills the whole grid
        Assert.False(grid.IsFree(0, 0, 11, 4));  // one column too wide
        Assert.False(grid.IsFree(0, 0, 10, 5));  // one row too tall
        Assert.False(grid.IsFree(-1, 0, 1, 1));  // negative anchor
        Assert.False(grid.IsFree(0, -1, 1, 1));
    }

    [Fact]
    public void With_occupies_every_cell_of_the_footprint_not_just_the_anchor()
    {
        var grid = new PackGrid(4, 10);
        var placed = grid.With(Item(w: 2, h: 3), row: 0, col: 0);

        // Every one of the 2x3 = 6 covered cells must now read as occupied.
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 2; c++)
                Assert.False(placed.IsFree(r, c, 1, 1), $"cell ({r},{c}) should be occupied");

        // Just outside the footprint stays free.
        Assert.True(placed.IsFree(3, 0, 1, 1));
        Assert.True(placed.IsFree(0, 2, 1, 1));
    }

    [Fact]
    public void Two_non_overlapping_items_can_both_be_placed()
    {
        var grid = new PackGrid(4, 10);
        var g1 = grid.With(Item(w: 2, h: 2, refId: "a"), 0, 0);
        var g2 = g1.With(Item(w: 2, h: 2, refId: "b"), 0, 2);
        Assert.Equal(2, g2.Cells.Count);
        Assert.False(g2.IsFree(0, 0, 1, 1));
        Assert.False(g2.IsFree(0, 2, 1, 1));
    }

    [Fact]
    public void An_overlapping_placement_is_reported_as_not_free_before_placing()
    {
        var grid = new PackGrid(4, 10).With(Item(w: 2, h: 2, refId: "a"), 0, 0);
        Assert.False(grid.IsFree(1, 1, 2, 2)); // overlaps the (0,0)-(1,1) corner cell
        Assert.True(grid.IsFree(2, 2, 2, 2));  // clear of it entirely
    }

    [Fact]
    public void The_original_grid_is_unchanged_after_With_immutability()
    {
        var grid = new PackGrid(4, 10);
        var placed = grid.With(Item(w: 1, h: 1), 0, 0);
        Assert.Empty(grid.Cells);
        Assert.Single(placed.Cells);
        Assert.True(grid.IsFree(0, 0, 1, 1)); // the original never saw the placement
    }
}
