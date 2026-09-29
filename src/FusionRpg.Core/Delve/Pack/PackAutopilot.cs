namespace FusionRpg.Core.Delve.Pack;

/// <summary>The closed autopilot rule-id list (spec-loot-pack.md §6, §9: "an unknown id refuses at load").</summary>
public static class PackAutopilotRules
{
    public const string ValuePerCell = "value-per-cell";
    public const string Leave = "leave";

    public static bool IsKnown(string ruleId) => ruleId is ValuePerCell or Leave;
}

/// <summary>
/// D3.21 (spec-loot-pack.md §6) — the un-steered party's own floor resolution (R9). Autopilot NEVER
/// moves an already-placed item and NEVER drops carry-in gear (§6) — it only ever places a floor item
/// directly, swaps out one lower-value placed HAUL item for a higher-value floor one past a margin, or
/// leaves both alone. Pure: no store, no clock, no RNG.
/// </summary>
public static class PackAutopilot
{
    /// <summary>`valuePerCellMilli = rarityOrdinal × 1000 / cells` (§6) — an ordering key over two
    /// ordinals, never a price, never `P(Θ)`. An item with no rarity axis (materials, currency, keys)
    /// values at 0, always the first candidate to swap out.</summary>
    public static long ValuePerCellMilli(PackItem item)
    {
        var cells = item.W * item.H;
        if (cells <= 0) throw new ArgumentOutOfRangeException(nameof(item), "a placed item always covers at least one cell");
        return (long)(item.RarityOrdinal ?? 0) * 1000 / cells;
    }

    /// <summary>
    /// One full pass over the floor list, in sorted order (area descending, then grant index, then
    /// `refId` ordinal — the same tie-break `PackArranger` uses, since this is the identical
    /// "which item first" question). `Applied` carries only the `pack.drop{by: autopilot, rule:
    /// value-per-cell}` decisions actually taken, in order — the caller logs exactly these, never one
    /// that was considered and not taken.
    /// </summary>
    public static (PackGrid Grid, IReadOnlyList<PackItem> Floor, IReadOnlyList<PackDropDecision> Applied) ResolveFloor(
        PackGrid grid, IReadOnlyList<PackItem> floor, string ruleId, long swapMarginMilli)
    {
        if (grid is null) throw new ArgumentNullException(nameof(grid));
        if (floor is null) throw new ArgumentNullException(nameof(floor));
        if (!PackAutopilotRules.IsKnown(ruleId))
            throw new PackRejection($"pack.autopilot.floorRule: '{ruleId}' is not one of value-per-cell, leave");

        var applied = new List<PackDropDecision>();
        if (ruleId == PackAutopilotRules.Leave) return (grid, floor, applied);

        var remainingFloor = new List<PackItem>();
        foreach (var floorItem in floor
                     .OrderByDescending(x => x.W * x.H).ThenBy(x => x.GrantIndex).ThenBy(x => x.RefId, StringComparer.Ordinal))
        {
            if (TryFindFit(grid, floorItem, out var r, out var c))
            {
                grid = grid.With(floorItem, r, c);
                continue;
            }

            var swapTarget = grid.Cells
                .Where(cell => cell.Item.Origin == PackItemOrigin.Haul)
                .OrderBy(cell => ValuePerCellMilli(cell.Item)).ThenBy(cell => cell.Item.ItemLevel ?? 0)
                .FirstOrDefault(cell => WouldFitAfterRemoving(grid, cell.Item, floorItem));

            if (swapTarget is not null && ValuePerCellMilli(floorItem) - ValuePerCellMilli(swapTarget.Item) >= swapMarginMilli)
            {
                var withoutSwapped = grid.Remove(PackGrid.KeyOf(swapTarget.Item));
                TryFindFit(withoutSwapped, floorItem, out var sr, out var sc);
                grid = withoutSwapped.With(floorItem, sr, sc);
                remainingFloor.Add(swapTarget.Item);
                applied.Add(new PackDropDecision(PackGrid.KeyOf(swapTarget.Item), null, PackMoves.DropBy.Autopilot, PackAutopilotRules.ValuePerCell));
                continue;
            }

            remainingFloor.Add(floorItem);
        }

        return (grid, remainingFloor, applied);
    }

    static bool TryFindFit(PackGrid grid, PackItem item, out int row, out int col)
    {
        for (var r = 0; r <= grid.Rows - item.H; r++)
            for (var c = 0; c <= grid.Cols - item.W; c++)
                if (grid.IsFree(r, c, item.W, item.H)) { row = r; col = c; return true; }
        row = col = 0;
        return false;
    }

    static bool WouldFitAfterRemoving(PackGrid grid, PackItem toRemove, PackItem candidate) =>
        TryFindFit(grid.Remove(PackGrid.KeyOf(toRemove)), candidate, out _, out _);
}
