using FusionRpg.Core.Delve.Pack;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>D3.21 (spec-loot-pack.md §6) — `PackMoves.Apply{Move,Drop}`.</summary>
public class PackMovesTests
{
    static PackItem Item(string refId, int w = 1, int h = 1, PackItemOrigin origin = PackItemOrigin.Haul, long qty = 1, string? instanceId = "inst") =>
        new(Kind: "Equipment", RefId: refId, InstanceId: instanceId, Qty: qty, W: w, H: h, GrantIndex: 0, Origin: origin);

    static PackItem Stack(string refId, long qty) =>
        new(Kind: "Material", RefId: refId, InstanceId: null, Qty: qty, W: 1, H: 1, GrantIndex: 0, Origin: PackItemOrigin.Haul);

    // ---- ApplyMove: from grid ----

    [Fact]
    public void Move_from_grid_relocates_the_item_to_a_free_cell()
    {
        var grid = new PackGrid(4, 10).With(Item("a"), 0, 0);
        var (ok, reason, result, _) = PackMoves.ApplyMove(grid, Array.Empty<PackItem>(),
            new PackMoveDecision(PackMoves.FromGrid, "inst", 2, 3), partyIsInThisRoom: false);

        Assert.True(ok, reason);
        var cell = Assert.Single(result.Cells);
        Assert.Equal((2, 3), (cell.Row, cell.Col));
    }

    [Fact]
    public void Move_onto_the_items_own_current_cell_is_legal()
    {
        var grid = new PackGrid(4, 10).With(Item("a", w: 2, h: 2), 0, 0);
        var (ok, reason, result, _) = PackMoves.ApplyMove(grid, Array.Empty<PackItem>(),
            new PackMoveDecision(PackMoves.FromGrid, "inst", 0, 0), partyIsInThisRoom: false);
        Assert.True(ok, reason);
        Assert.Single(result.Cells);
    }

    [Fact]
    public void Move_from_grid_refuses_an_occupied_target()
    {
        var grid = new PackGrid(4, 10).With(Item("a"), 0, 0).With(Item("b", instanceId: "inst-b"), 0, 1);
        var (ok, reason, result, _) = PackMoves.ApplyMove(grid, Array.Empty<PackItem>(),
            new PackMoveDecision(PackMoves.FromGrid, "inst", 0, 1), partyIsInThisRoom: false);
        Assert.False(ok);
        Assert.Equal("pack.cell-occupied", reason);
        Assert.Equal(2, result.Cells.Count); // unchanged
    }

    [Fact]
    public void Move_from_grid_refuses_an_out_of_grid_target()
    {
        var grid = new PackGrid(4, 10).With(Item("a"), 0, 0);
        var (ok, reason, _, _) = PackMoves.ApplyMove(grid, Array.Empty<PackItem>(),
            new PackMoveDecision(PackMoves.FromGrid, "inst", 0, 10), partyIsInThisRoom: false);
        Assert.False(ok);
        Assert.Equal("pack.out-of-grid", reason);
    }

    [Fact]
    public void Move_from_grid_refuses_an_unknown_item_key()
    {
        var grid = new PackGrid(4, 10);
        var (ok, reason, _, _) = PackMoves.ApplyMove(grid, Array.Empty<PackItem>(),
            new PackMoveDecision(PackMoves.FromGrid, "ghost", 0, 0), partyIsInThisRoom: false);
        Assert.False(ok);
        Assert.Equal("pack.not-here", reason);
    }

    // ---- ApplyMove: from floor ----

    [Fact]
    public void Move_from_floor_requires_the_party_to_be_in_that_room()
    {
        var floor = new[] { Item("a") };
        var (ok, reason, _, _) = PackMoves.ApplyMove(new PackGrid(4, 10), floor,
            new PackMoveDecision(PackMoves.FromFloor, "inst", 0, 0), partyIsInThisRoom: false);
        Assert.False(ok);
        Assert.Equal("pack.not-here", reason);
    }

    [Fact]
    public void Move_from_floor_places_it_and_removes_it_from_the_floor_list()
    {
        var floor = new[] { Item("a") };
        var (ok, reason, grid, newFloor) = PackMoves.ApplyMove(new PackGrid(4, 10), floor,
            new PackMoveDecision(PackMoves.FromFloor, "inst", 1, 1), partyIsInThisRoom: true);
        Assert.True(ok, reason);
        Assert.Empty(newFloor);
        var cell = Assert.Single(grid.Cells);
        Assert.Equal((1, 1), (cell.Row, cell.Col));
    }

    // ---- ApplyDrop ----

    [Fact]
    public void Drop_the_whole_stack_frees_the_cell_and_a_haul_item_joins_the_floor()
    {
        var grid = new PackGrid(4, 10).With(Stack("ration", 3), 0, 0);
        var (ok, reason, result, floor) = PackMoves.ApplyDrop(grid, Array.Empty<PackItem>(),
            new PackDropDecision("ration", Qty: null, By: PackMoves.DropBy.Player, Rule: null));
        Assert.True(ok, reason);
        Assert.Empty(result.Cells);
        var floored = Assert.Single(floor);
        Assert.Equal("ration", floored.RefId);
    }

    [Fact]
    public void Drop_a_carry_in_item_frees_the_cell_but_does_not_join_the_floor()
    {
        var carryIn = Stack("ration", 3) with { Origin = PackItemOrigin.CarryIn };
        var grid = new PackGrid(4, 10).With(carryIn, 0, 0);
        var (ok, _, result, floor) = PackMoves.ApplyDrop(grid, Array.Empty<PackItem>(),
            new PackDropDecision("ration", null, PackMoves.DropBy.Player, null));
        Assert.True(ok);
        Assert.Empty(result.Cells);
        Assert.Empty(floor); // destroyed at settlement, not floored -- §6
    }

    [Fact]
    public void Drop_a_partial_quantity_shrinks_the_stack_and_keeps_its_cell()
    {
        var grid = new PackGrid(4, 10).With(Stack("ration", 5), 0, 0);
        var (ok, reason, result, floor) = PackMoves.ApplyDrop(grid, Array.Empty<PackItem>(),
            new PackDropDecision("ration", Qty: 2, By: PackMoves.DropBy.Use, Rule: null));
        Assert.True(ok, reason);
        Assert.Empty(floor); // still not fully consumed -- cell not freed
        var cell = Assert.Single(result.Cells);
        Assert.Equal(3, cell.Item.Qty);
        Assert.Equal((0, 0), (cell.Row, cell.Col)); // stays in its own cell
    }

    [Fact]
    public void Drop_exactly_the_full_quantity_via_use_frees_the_cell_at_zero()
    {
        var grid = new PackGrid(4, 10).With(Stack("ration", 2), 0, 0);
        var (ok, _, result, floor) = PackMoves.ApplyDrop(grid, Array.Empty<PackItem>(),
            new PackDropDecision("ration", Qty: 2, By: PackMoves.DropBy.Use, Rule: null));
        Assert.True(ok);
        Assert.Empty(result.Cells);
        Assert.Single(floor); // "use" only ever targets an already-carried item -- treated as a haul-shaped drop
    }

    [Fact]
    public void Drop_refuses_a_quantity_larger_than_the_stack()
    {
        var grid = new PackGrid(4, 10).With(Stack("ration", 2), 0, 0);
        var (ok, reason, _, _) = PackMoves.ApplyDrop(grid, Array.Empty<PackItem>(),
            new PackDropDecision("ration", Qty: 5, By: PackMoves.DropBy.Player, Rule: null));
        Assert.False(ok);
        Assert.Equal("pack.not-here", reason);
    }

    [Fact]
    public void Drop_refuses_an_unknown_item_key()
    {
        var (ok, reason, _, _) = PackMoves.ApplyDrop(new PackGrid(4, 10), Array.Empty<PackItem>(),
            new PackDropDecision("ghost", null, PackMoves.DropBy.Player, null));
        Assert.False(ok);
        Assert.Equal("pack.not-here", reason);
    }

    // ---- Replay: §10's own determinism claim, made executable ----

    [Fact]
    public void Replay_reproduces_the_same_end_state_as_applying_each_step_by_hand()
    {
        var grid0 = new PackGrid(4, 10).With(Item("a"), 0, 0);
        var entries = new[]
        {
            PackMoves.PackTraceEntry.OfMove(new PackMoveDecision(PackMoves.FromGrid, "inst", 1, 1)),
            PackMoves.PackTraceEntry.OfDrop(new PackDropDecision("inst", null, PackMoves.DropBy.Player, null)),
        };

        var (ok, reason, grid, floor) = PackMoves.Replay(grid0, Array.Empty<PackItem>(), entries);

        Assert.True(ok, reason);
        Assert.Empty(grid.Cells); // moved, then dropped -- ends up empty
        var floored = Assert.Single(floor);
        Assert.Equal("a", floored.RefId);
    }

    [Fact]
    public void Replay_stops_at_the_first_refusal_and_reports_it()
    {
        var grid0 = new PackGrid(1, 1).With(Item("a"), 0, 0);
        var entries = new[]
        {
            PackMoves.PackTraceEntry.OfDrop(new PackDropDecision("ghost-key", null, PackMoves.DropBy.Player, null)),
        };

        var (ok, reason, grid, floor) = PackMoves.Replay(grid0, Array.Empty<PackItem>(), entries);

        Assert.False(ok);
        Assert.Equal("pack.not-here", reason);
        Assert.Single(grid.Cells); // state as of the last GOOD entry -- untouched by the failed one
    }

    [Fact]
    public void Replay_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackMoves.Replay(null!, Array.Empty<PackItem>(), Array.Empty<PackMoves.PackTraceEntry>()));
        Assert.Throws<ArgumentNullException>(() => PackMoves.Replay(new PackGrid(1, 1), null!, Array.Empty<PackMoves.PackTraceEntry>()));
        Assert.Throws<ArgumentNullException>(() => PackMoves.Replay(new PackGrid(1, 1), Array.Empty<PackItem>(), null!));
    }
}
