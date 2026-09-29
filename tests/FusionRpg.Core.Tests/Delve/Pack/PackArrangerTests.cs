using FusionRpg.Core.Delve.Pack;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>D3.20 (spec-loot-pack.md §5, its own literal `PackArranger.Arrange` code sample,
/// `:178-196`) — first-fit decreasing, pure. No RNG in the production code; the property tests below
/// use a seeded, test-local `System.Random` purely to GENERATE input sets, never inside `Arrange`
/// itself.</summary>
public class PackArrangerTests
{
    static PackItem Item(int w, int h, int grantIndex, string refId, PackItemOrigin origin = PackItemOrigin.Haul) =>
        new(Kind: "Equipment", RefId: refId, InstanceId: refId, Qty: 1, W: w, H: h, GrantIndex: grantIndex, Origin: origin);

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackArranger.Arrange(null!, Array.Empty<PackItem>()));
        Assert.Throws<ArgumentNullException>(() => PackArranger.Arrange(new PackGrid(4, 10), null!));
    }

    [Fact]
    public void An_item_larger_than_the_grid_in_either_dimension_refuses()
    {
        var grid = new PackGrid(4, 10);
        Assert.Throws<PackRejection>(() => PackArranger.Arrange(grid, new[] { Item(w: 11, h: 1, 0, "a") }));
        Assert.Throws<PackRejection>(() => PackArranger.Arrange(grid, new[] { Item(w: 1, h: 5, 0, "a") }));
    }

    [Fact]
    public void A_single_item_places_at_the_top_left_anchor()
    {
        var (grid, floor) = PackArranger.Arrange(new PackGrid(4, 10), new[] { Item(2, 2, 0, "a") });
        Assert.Empty(floor);
        var cell = Assert.Single(grid.Cells);
        Assert.Equal((0, 0), (cell.Row, cell.Col));
    }

    [Fact]
    public void Larger_items_place_before_smaller_ones_regardless_of_input_order()
    {
        // area-descending: the 2x2 (area 4) must land before the 1x1 (area 1) even though it is
        // listed second in the input.
        var (grid, floor) = PackArranger.Arrange(new PackGrid(4, 10), new[] { Item(1, 1, 1, "small"), Item(2, 2, 0, "big") });
        Assert.Empty(floor);
        var big = grid.Cells.Single(c => c.Item.RefId == "big");
        Assert.Equal((0, 0), (big.Row, big.Col)); // the big one claimed the first anchor
    }

    [Fact]
    public void Equal_area_ties_break_by_grant_index_ascending()
    {
        // Two 1x1s, equal area -- grantIndex ascending decides who gets the first anchor, even
        // though refId ordinal alone would have picked the other one ("a" < "z").
        var (grid, _) = PackArranger.Arrange(new PackGrid(1, 2),
            new[] { Item(1, 1, grantIndex: 1, "z"), Item(1, 1, grantIndex: 5, "a") });
        var first = grid.Cells.Single(c => c.Col == 0);
        Assert.Equal("z", first.Item.RefId); // grantIndex 1 beat grantIndex 5, despite refId ordering
    }

    [Fact]
    public void When_grant_index_ties_refId_ordinal_breaks_it()
    {
        var (grid, _) = PackArranger.Arrange(new PackGrid(1, 2),
            new[] { Item(1, 1, grantIndex: 0, "z"), Item(1, 1, grantIndex: 0, "a") });
        var first = grid.Cells.Single(c => c.Col == 0);
        Assert.Equal("a", first.Item.RefId); // same grantIndex -- "a" sorts before "z"
    }

    [Fact]
    public void An_item_with_no_free_anchor_goes_to_the_floor()
    {
        var grid = new PackGrid(1, 1).With(Item(1, 1, 0, "occupant"), 0, 0);
        var (result, floor) = PackArranger.Arrange(grid, new[] { Item(1, 1, 1, "homeless") });
        var floorItem = Assert.Single(floor);
        Assert.Equal("homeless", floorItem.RefId);
        Assert.Single(result.Cells); // grid unchanged -- still just the original occupant
    }

    [Fact]
    public void Scanning_is_row_major_not_column_major()
    {
        // Occupy (0,0) on a 2x2 grid: the free cells are (0,1), (1,0), (1,1). Row-major (outer row,
        // inner column) reaches (0,1) before (1,0); column-major would reach (1,0) first instead --
        // this scenario is the one place the two scan orders actually disagree.
        var grid = new PackGrid(2, 2)
            .With(Item(1, 1, 0, "occupant"), 0, 0);
        var (result, floor) = PackArranger.Arrange(grid, new[] { Item(1, 1, 0, "new") });
        Assert.Empty(floor);
        var placed = result.Cells.Single(c => c.Item.RefId == "new");
        Assert.Equal((0, 1), (placed.Row, placed.Col));
    }

    // ---- determinism: same items, same grid, same order -> byte-identical result ----

    [Fact]
    public void Arrange_is_deterministic_for_a_fixed_input_order()
    {
        var items = new[] { Item(2, 3, 0, "a"), Item(1, 1, 1, "b"), Item(3, 1, 2, "c") };
        var (g1, f1) = PackArranger.Arrange(new PackGrid(4, 10), items);
        var (g2, f2) = PackArranger.Arrange(new PackGrid(4, 10), items);
        Assert.Equal(g1.Cells, g2.Cells);
        Assert.Equal(f1, f2);
    }

    // ---- order-free arrangement: spec's own "Success looks like" line, 256 shuffles ----

    [Fact]
    public void The_same_grant_set_arranges_to_the_identical_grid_and_floor_across_256_shuffles()
    {
        var baseline = new[]
        {
            Item(2, 3, 0, "relic"), Item(1, 1, 1, "ring-a"), Item(3, 1, 2, "belt"), Item(2, 2, 3, "helm"),
            Item(1, 2, 4, "wand"), Item(4, 2, 5, "chest"), Item(1, 1, 6, "ring-b"), Item(2, 1, 7, "boots"),
        };
        var rng = new Random(12345);
        var (expectedGrid, expectedFloor) = PackArranger.Arrange(new PackGrid(4, 10), baseline);
        var expectedCells = expectedGrid.Cells.OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();

        for (var i = 0; i < 256; i++)
        {
            var shuffled = baseline.OrderBy(_ => rng.Next()).ToList();
            var (grid, floor) = PackArranger.Arrange(new PackGrid(4, 10), shuffled);
            var cells = grid.Cells.OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
            Assert.Equal(expectedCells.Count, cells.Count);
            for (var k = 0; k < cells.Count; k++)
                Assert.Equal((expectedCells[k].Row, expectedCells[k].Col, expectedCells[k].Item.RefId),
                    (cells[k].Row, cells[k].Col, cells[k].Item.RefId));
            Assert.Equal(expectedFloor.Select(x => x.RefId).OrderBy(x => x, StringComparer.Ordinal),
                floor.Select(x => x.RefId).OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    // ---- property test: 1,000 generated hauls, no overlap and no cell reuse ----

    static IReadOnlyList<PackItem> GenerateHaul(Random rng, int count)
    {
        // Every (W,H) drawn is a real Footprint.Orient shape (Tall or Broad tables), so the generator
        // never invents an impossible rectangle the production pipeline could not itself produce.
        (int W, int H)[] shapes =
        {
            (1, 1), (1, 2), (2, 1), (1, 3), (3, 1), (1, 4), (2, 2), (2, 3), (3, 2), (2, 4), (4, 2),
        };
        var items = new List<PackItem>();
        for (var i = 0; i < count; i++)
        {
            var (w, h) = shapes[rng.Next(shapes.Length)];
            items.Add(new PackItem("Equipment", $"item-{i}", $"inst-{i}", 1, w, h, i, PackItemOrigin.Haul));
        }
        return items;
    }

    static bool AnyOverlap(PackGrid grid)
    {
        var occupied = new HashSet<(int, int)>();
        foreach (var cell in grid.Cells)
            for (var dr = 0; dr < cell.Item.H; dr++)
                for (var dc = 0; dc < cell.Item.W; dc++)
                    if (!occupied.Add((cell.Row + dr, cell.Col + dc)))
                        return true; // this cell was already claimed by an earlier item
        return false;
    }

    [Fact]
    public void Property_1000_generated_hauls_never_overlap_and_never_reuse_a_cell()
    {
        var rng = new Random(20260906);
        for (var trial = 0; trial < 1000; trial++)
        {
            var grid = new PackGrid(4, 10);
            var haul = GenerateHaul(rng, count: rng.Next(1, 15));
            // Items too big for the grid would throw pack.footprint-exceeds-grid -- every generated
            // shape above is at most 4x2/2x4, always inside a 4x10 grid, so no filtering is needed.
            var (result, floor) = PackArranger.Arrange(grid, haul);

            Assert.False(AnyOverlap(result), $"trial {trial}: overlapping placement detected");
            Assert.Equal(haul.Count, result.Cells.Count + floor.Count); // every item is placed or floored, never both, never lost
        }
    }
}
