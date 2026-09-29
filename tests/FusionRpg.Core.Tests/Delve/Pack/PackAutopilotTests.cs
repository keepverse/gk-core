using FusionRpg.Core.Delve.Pack;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>D3.21 (spec-loot-pack.md §6) — `PackAutopilot.ResolveFloor`.</summary>
public class PackAutopilotTests
{
    static PackItem Haul(string refId, int rarityOrdinal, int itemLevel = 20, int w = 1, int h = 1) =>
        new(Kind: "Equipment", RefId: refId, InstanceId: refId, Qty: 1, W: w, H: h, GrantIndex: 0,
            Origin: PackItemOrigin.Haul, RarityOrdinal: rarityOrdinal, ItemLevel: itemLevel);

    static PackItem CarryIn(string refId, int rarityOrdinal) =>
        Haul(refId, rarityOrdinal) with { Origin = PackItemOrigin.CarryIn };

    [Fact]
    public void ValuePerCellMilli_scales_with_rarity_and_inversely_with_size()
    {
        Assert.Equal(3000, PackAutopilot.ValuePerCellMilli(Haul("a", rarityOrdinal: 3, w: 1, h: 1)));
        Assert.Equal(1500, PackAutopilot.ValuePerCellMilli(Haul("a", rarityOrdinal: 3, w: 2, h: 1)));
        Assert.Equal(0, PackAutopilot.ValuePerCellMilli(Haul("a", rarityOrdinal: 0)));
    }

    [Fact]
    public void Unknown_rule_id_refuses()
    {
        Assert.Throws<PackRejection>(() =>
            PackAutopilot.ResolveFloor(new PackGrid(4, 10), Array.Empty<PackItem>(), "highest-first", 250));
    }

    [Fact]
    public void The_leave_rule_never_touches_the_grid_or_floor()
    {
        var floor = new[] { Haul("a", 3) };
        var (grid, resultFloor, applied) = PackAutopilot.ResolveFloor(new PackGrid(4, 10), floor, PackAutopilotRules.Leave, 250);
        Assert.Empty(grid.Cells);
        Assert.Same(floor, resultFloor);
        Assert.Empty(applied);
    }

    [Fact]
    public void A_floor_item_with_a_direct_fit_places_without_any_swap()
    {
        var floor = new[] { Haul("a", 3) };
        var (grid, resultFloor, applied) = PackAutopilot.ResolveFloor(new PackGrid(4, 10), floor, PackAutopilotRules.ValuePerCell, 250);
        Assert.Single(grid.Cells);
        Assert.Empty(resultFloor);
        Assert.Empty(applied); // a direct placement is not a drop decision
    }

    [Fact]
    public void No_fit_and_no_worthwhile_swap_leaves_the_floor_item_on_the_floor()
    {
        var grid = new PackGrid(1, 1).With(Haul("occupant", rarityOrdinal: 5), 0, 0);
        var floor = new[] { Haul("challenger", rarityOrdinal: 5) }; // equal value -- never clears the margin
        var (result, resultFloor, applied) = PackAutopilot.ResolveFloor(grid, floor, PackAutopilotRules.ValuePerCell, swapMarginMilli: 250);
        Assert.Single(result.Cells);
        Assert.Equal("occupant", result.Cells.Single().Item.RefId);
        var left = Assert.Single(resultFloor);
        Assert.Equal("challenger", left.RefId);
        Assert.Empty(applied);
    }

    [Fact]
    public void A_floor_item_past_the_margin_swaps_out_the_lowest_value_placed_haul_item()
    {
        var grid = new PackGrid(1, 1).With(Haul("weak", rarityOrdinal: 1), 0, 0);
        var floor = new[] { Haul("strong", rarityOrdinal: 5) }; // 5000 - 1000 = 4000 >= 250 margin
        var (result, resultFloor, applied) = PackAutopilot.ResolveFloor(grid, floor, PackAutopilotRules.ValuePerCell, swapMarginMilli: 250);

        Assert.Single(result.Cells);
        Assert.Equal("strong", result.Cells.Single().Item.RefId);
        var floored = Assert.Single(resultFloor);
        Assert.Equal("weak", floored.RefId);
        var decision = Assert.Single(applied);
        Assert.Equal("weak", decision.ItemKey);
        Assert.Equal(PackMoves.DropBy.Autopilot, decision.By);
        Assert.Equal(PackAutopilotRules.ValuePerCell, decision.Rule);
    }

    [Fact]
    public void A_difference_exactly_at_the_margin_still_swaps_the_boundary_is_inclusive()
    {
        // rarity 1 -> 1000 milli, rarity 4 -> 4000 milli: a difference of exactly 3000, matching a
        // 3000 swapMarginMilli precisely -- proves ">= margin" swaps, not "> margin".
        var grid = new PackGrid(1, 1).With(Haul("weak", rarityOrdinal: 1), 0, 0);
        var floor = new[] { Haul("strong", rarityOrdinal: 4) };
        var (result, _, applied) = PackAutopilot.ResolveFloor(grid, floor, PackAutopilotRules.ValuePerCell, swapMarginMilli: 3000);

        Assert.Equal("strong", result.Cells.Single().Item.RefId);
        Assert.Single(applied);
    }

    [Fact]
    public void Autopilot_never_swaps_out_a_carry_in_item_even_when_it_is_the_weakest()
    {
        var grid = new PackGrid(1, 1).With(CarryIn("carried", rarityOrdinal: 0), 0, 0);
        var floor = new[] { Haul("strong", rarityOrdinal: 5) };
        var (result, resultFloor, applied) = PackAutopilot.ResolveFloor(grid, floor, PackAutopilotRules.ValuePerCell, swapMarginMilli: 0);

        Assert.Equal("carried", result.Cells.Single().Item.RefId); // untouched
        var left = Assert.Single(resultFloor);
        Assert.Equal("strong", left.RefId); // nowhere to go -- left on the floor
        Assert.Empty(applied);
    }

    [Fact]
    public void ItemLevel_tiebreaks_among_equal_value_swap_candidates()
    {
        var grid = new PackGrid(1, 2)
            .With(Haul("low-level", rarityOrdinal: 1, itemLevel: 10), 0, 0)
            .With(Haul("high-level", rarityOrdinal: 1, itemLevel: 90), 0, 1);
        var floor = new[] { Haul("challenger", rarityOrdinal: 5) };
        var (_, _, applied) = PackAutopilot.ResolveFloor(grid, floor, PackAutopilotRules.ValuePerCell, swapMarginMilli: 0);

        var decision = Assert.Single(applied);
        Assert.Equal("low-level", decision.ItemKey); // equal rarity -- the lower item level goes first
    }
}
