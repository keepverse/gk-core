namespace FusionRpg.Core.Delve.Pack;

/// <summary>
/// D3.21 (spec-loot-pack.md §4) — the carry-in allowance and its own validation. "Cannot bring a
/// whole empire": carried-in footprints must sum to at most the allowance, and every fungible's
/// requested quantity must not exceed its own current stock — checked here rather than left to
/// `AdjustStock`'s own `MAX(0, …)` floor, which would silently truncate a short stock instead of
/// refusing (§4).
/// </summary>
public static class PackProvisioning
{
    /// <summary>`provisionCells(rung) = pack.provision.baseCells + RungTable.Get(rungId).
    /// ProvisionCellsDelta` (§4), range-checked against the party's own grid — `hard` is the identity
    /// row (delta 0), so a domain at `hard` always gets exactly `baseCells`.</summary>
    public static int ProvisionCells(int baseCells, int provisionCellsDelta, int gridRows, int gridCols)
    {
        var cells = baseCells + provisionCellsDelta;
        if (cells < 0 || cells > gridRows * gridCols)
            throw new PackRejection($"pack.provision-cells-out-of-range: {cells} outside [0, {gridRows * gridCols}]");
        return cells;
    }

    /// <summary>
    /// Validates a carry-in request BEFORE any write (§4's own "Boundaries: Always validate before
    /// any write" discipline this whole program shares). A fungible stack has no
    /// <see cref="PackItem.InstanceId"/> — equipped/assigned gear never reaches the pack at all (§4:
    /// "equipped gear does not occupy the pack"), so every un-assigned <see cref="PackItem"/> here is
    /// either a stack (check stock) or a bare gear instance (nothing to check against stock).
    /// </summary>
    public static (bool Ok, string Reason) Validate(
        IReadOnlyList<PackItem> carryIn, int provisionCells, Func<string, long> stockOf)
    {
        if (carryIn is null) throw new ArgumentNullException(nameof(carryIn));
        if (stockOf is null) throw new ArgumentNullException(nameof(stockOf));

        var totalCells = carryIn.Sum(i => i.W * i.H);
        if (totalCells > provisionCells)
            return (false, $"pack.over-provisioned: {totalCells} cells requested, {provisionCells} allowed");

        foreach (var item in carryIn.Where(i => i.InstanceId is null))
            if (item.Qty > stockOf(item.RefId))
                return (false, $"pack.stock-short: '{item.RefId}' requested {item.Qty}, stock is short");

        return (true, "");
    }
}
