using FusionRpg.Core.Delve.Pack;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>D3.21 (spec-loot-pack.md §4) — `PackProvisioning`.</summary>
public class PackProvisioningTests
{
    static PackItem Stack(string refId, long qty, int w = 1, int h = 1) =>
        new(Kind: "Material", RefId: refId, InstanceId: null, Qty: qty, W: w, H: h, GrantIndex: 0, Origin: PackItemOrigin.CarryIn);

    static PackItem Gear(string refId) =>
        new(Kind: "Equipment", RefId: refId, InstanceId: "inst-" + refId, Qty: 1, W: 1, H: 1, GrantIndex: 0, Origin: PackItemOrigin.CarryIn);

    // ---- ProvisionCells ----

    [Theory]
    [InlineData(16, 0, 16)]   // hard -- the identity row, delta 0
    [InlineData(16, 4, 20)]   // very-easy-shaped: a positive delta
    [InlineData(16, -6, 10)]  // impossible-shaped: a negative delta
    public void ProvisionCells_adds_the_rung_delta_to_the_base(int baseCells, int delta, int expected)
    {
        Assert.Equal(expected, PackProvisioning.ProvisionCells(baseCells, delta, gridRows: 4, gridCols: 10));
    }

    [Fact]
    public void ProvisionCells_refuses_a_negative_result()
    {
        Assert.Throws<PackRejection>(() => PackProvisioning.ProvisionCells(baseCells: 5, provisionCellsDelta: -10, gridRows: 4, gridCols: 10));
    }

    [Fact]
    public void ProvisionCells_refuses_a_result_exceeding_the_grid()
    {
        Assert.Throws<PackRejection>(() => PackProvisioning.ProvisionCells(baseCells: 40, provisionCellsDelta: 1, gridRows: 4, gridCols: 10));
    }

    [Fact]
    public void ProvisionCells_accepts_the_exact_grid_size()
    {
        Assert.Equal(40, PackProvisioning.ProvisionCells(baseCells: 40, provisionCellsDelta: 0, gridRows: 4, gridCols: 10));
    }

    // ---- Validate ----

    [Fact]
    public void Validate_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackProvisioning.Validate(null!, 16, _ => 0));
        Assert.Throws<ArgumentNullException>(() => PackProvisioning.Validate(Array.Empty<PackItem>(), 16, null!));
    }

    [Fact]
    public void Sixteen_cells_at_a_sixteen_cell_allowance_is_accepted()
    {
        var carryIn = Enumerable.Range(0, 16).Select(i => Stack($"item-{i}", 1)).ToList();
        var (ok, reason) = PackProvisioning.Validate(carryIn, provisionCells: 16, _ => 100);
        Assert.True(ok, reason);
    }

    [Fact]
    public void Seventeen_cells_at_a_sixteen_cell_allowance_refuses_over_provisioned()
    {
        var carryIn = Enumerable.Range(0, 17).Select(i => Stack($"item-{i}", 1)).ToList();
        var (ok, reason) = PackProvisioning.Validate(carryIn, provisionCells: 16, _ => 100);
        Assert.False(ok);
        Assert.Contains("pack.over-provisioned", reason);
    }

    [Fact]
    public void A_qty_above_stock_refuses_stock_short_and_nothing_else_is_checked_first()
    {
        var carryIn = new[] { Stack("ration", qty: 10) };
        var (ok, reason) = PackProvisioning.Validate(carryIn, provisionCells: 16, _ => 3);
        Assert.False(ok);
        Assert.Contains("pack.stock-short", reason);
    }

    [Fact]
    public void A_qty_at_exactly_stock_is_accepted()
    {
        var carryIn = new[] { Stack("ration", qty: 3) };
        var (ok, reason) = PackProvisioning.Validate(carryIn, provisionCells: 16, _ => 3);
        Assert.True(ok, reason);
    }

    [Fact]
    public void Gear_carry_in_never_checks_stock_it_has_no_stock_row()
    {
        var carryIn = new[] { Gear("sword") };
        var (ok, reason) = PackProvisioning.Validate(carryIn, provisionCells: 16, _ => throw new InvalidOperationException("stockOf must not be called for gear"));
        Assert.True(ok, reason);
    }
}
