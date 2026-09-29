using FusionRpg.Core.Delve.Pack;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>D3.22 (spec-loot-pack.md §Interface) — `PackDtoProjection.Project`.</summary>
public class PackDtoTests
{
    static PackItem Gear(string refId, PackItemOrigin origin, int w = 1, int h = 1) =>
        new("Equipment", refId, "inst-" + refId, 1, w, h, 0, origin);
    static PackItem Stack(string refId, long qty, PackItemOrigin origin) =>
        new("Material", refId, null, qty, 1, 1, 0, origin);

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackDtoProjection.Project(null!, Array.Empty<PackItem>(), 16, true));
        Assert.Throws<ArgumentNullException>(() => PackDtoProjection.Project(new PackGrid(4, 10), null!, 16, true));
    }

    [Fact]
    public void Projects_rows_cols_and_every_placed_cell()
    {
        var grid = new PackGrid(4, 10).With(Gear("sword", PackItemOrigin.Haul, w: 2, h: 1), 0, 0);
        var dto = PackDtoProjection.Project(grid, Array.Empty<PackItem>(), provisionCells: 16, partySteered: true);

        Assert.Equal(4, dto.Rows);
        Assert.Equal(10, dto.Cols);
        var cell = Assert.Single(dto.Cells);
        Assert.Equal((0, 0, 2, 1), (cell.Row, cell.Col, cell.W, cell.H));
        Assert.Equal("sword", cell.RefId);
        Assert.Equal("Haul", cell.Origin);
    }

    [Fact]
    public void Movable_matches_the_partySteered_flag_for_every_cell_and_floor_item()
    {
        var grid = new PackGrid(4, 10).With(Gear("a", PackItemOrigin.Haul), 0, 0);
        var floor = new[] { Gear("b", PackItemOrigin.Haul) };

        var steered = PackDtoProjection.Project(grid, floor, 16, partySteered: true);
        Assert.True(steered.Cells.Single().Movable);
        Assert.True(steered.Floor.Single().Movable);

        var autopilot = PackDtoProjection.Project(grid, floor, 16, partySteered: false);
        Assert.False(autopilot.Cells.Single().Movable);
        Assert.False(autopilot.Floor.Single().Movable);
    }

    [Fact]
    public void No_PartyIndex_field_exists_anywhere_on_the_dto_or_its_cells()
    {
        // Structural guard for spec's own "no PartyIndex in any label" line -- a reflection check
        // survives a future field rename the way a string-literal grep would not.
        var dtoProps = typeof(PackDto).GetProperties().Select(p => p.Name);
        var cellProps = typeof(PackCellDto).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("PartyIndex", dtoProps);
        Assert.DoesNotContain("PartyIndex", cellProps);
    }

    [Fact]
    public void ProvisionCellsLeft_subtracts_only_carry_in_footprint_still_in_the_grid()
    {
        var grid = new PackGrid(4, 10)
            .With(Gear("carried", PackItemOrigin.CarryIn, w: 2, h: 1), 0, 0) // 2 cells, counts
            .With(Gear("looted", PackItemOrigin.Haul, w: 3, h: 1), 0, 2);    // 3 cells, haul -- does not count
        var dto = PackDtoProjection.Project(grid, Array.Empty<PackItem>(), provisionCells: 16, partySteered: true);
        Assert.Equal(14, dto.ProvisionCellsLeft); // 16 - 2, the haul item never touches the allowance
    }

    [Fact]
    public void ProvisionCellsLeft_ignores_floored_carry_in_it_already_left_the_grid()
    {
        var grid = new PackGrid(4, 10);
        var floor = new[] { Stack("dropped-ration", 5, PackItemOrigin.CarryIn) };
        var dto = PackDtoProjection.Project(grid, floor, provisionCells: 16, partySteered: true);
        Assert.Equal(16, dto.ProvisionCellsLeft); // nothing carried IN the grid right now
    }
}
