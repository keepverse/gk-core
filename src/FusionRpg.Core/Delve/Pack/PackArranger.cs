namespace FusionRpg.Core.Delve.Pack;

/// <summary>
/// D3.20 (spec-loot-pack.md §5) — first-fit decreasing placement, the exact algorithm and tie-break
/// order spec's own "Code style" section gives verbatim (`:178-196`): sort by footprint area
/// descending, then grant index ascending, then `refId` ordinal; scan candidate anchors row-major
/// (outer row, inner column); place at the first free rectangle; no fit places on the floor. Pure,
/// integer-only, no RNG, no store, no clock (§10) — "same grid + same items ⇒ same result on every
/// runtime."
/// </summary>
public static class PackArranger
{
    public static (PackGrid Grid, IReadOnlyList<PackItem> Floor) Arrange(PackGrid grid, IReadOnlyList<PackItem> items)
    {
        if (grid is null) throw new ArgumentNullException(nameof(grid));
        if (items is null) throw new ArgumentNullException(nameof(items));

        var floor = new List<PackItem>();
        foreach (var item in items.OrderByDescending(x => x.W * x.H).ThenBy(x => x.GrantIndex).ThenBy(x => x.RefId, StringComparer.Ordinal))
        {
            if (item.W > grid.Cols || item.H > grid.Rows)
                throw new PackRejection($"pack.footprint-exceeds-grid: '{item.RefId}' is {item.W}x{item.H} on {grid.Cols}x{grid.Rows}");

            var placed = false;
            for (var r = 0; r <= grid.Rows - item.H && !placed; r++)
                for (var c = 0; c <= grid.Cols - item.W && !placed; c++)
                    if (grid.IsFree(r, c, item.W, item.H)) { grid = grid.With(item, r, c); placed = true; }
            if (!placed) floor.Add(item);
        }
        return (grid, floor);
    }
}
