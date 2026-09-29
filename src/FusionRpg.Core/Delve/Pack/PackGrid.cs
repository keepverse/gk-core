namespace FusionRpg.Core.Delve.Pack;

/// <summary>Where a placed cell came from — the persistence shape's own closed pair
/// (spec-loot-pack.md's own "Persistence" section: <c>origin: carryIn|haul</c>).</summary>
public enum PackItemOrigin
{
    CarryIn,
    Haul,
}

/// <summary>
/// One item occupying a footprint in a <see cref="PackGrid"/> — the element of the persistence
/// shape's own <c>cells: [{r, c, w, h, kind, refId, qty, origin}]</c> array (spec-loot-pack.md
/// §Structure). <see cref="GrantIndex"/>/<see cref="RefId"/> are what <c>PackArranger.Arrange</c>
/// (D3.20) sorts by; carried here too since a placed item keeps its own identity regardless of which
/// module last touched the grid. <see cref="RarityOrdinal"/>/<see cref="ItemLevel"/> are `null` for
/// anything without a rarity axis (materials, currency, keys) — `PackAutopilot`'s own
/// `valuePerCellMilli` (D3.21, spec §6) is the only reader.
/// </summary>
public sealed record PackItem(
    string Kind, string RefId, string? InstanceId, long Qty, int W, int H, int GrantIndex, PackItemOrigin Origin,
    int? RarityOrdinal = null, int? ItemLevel = null);

/// <summary>One placed occupant of a grid cell (row, col) plus the <see cref="PackItem"/> anchored
/// there — only the anchor cell (its own top-left corner) carries the item; the remaining cells of a
/// multi-cell footprint are occupied but keyed to the SAME anchor, so a lookup never double-counts an
/// item once per cell it covers.</summary>
public sealed record PackCell(int Row, int Col, PackItem Item);

/// <summary>
/// spec-loot-pack.md §1 — one <c>rows × cols</c> grid per <c>(delveId, partyIndex)</c>. Starting shape
/// 4×10 in every raid mode (owner: `raid.modes.{solo,pair,quad}.pack.{rows,cols}`, `dungeon-registries`)
/// — <paramref name="rows"/>/<paramref name="cols"/> are supplied by the CALLER (that tuning read), never
/// hardcoded here.
///
/// <para><b>STRUCTURAL, not a magnitude — the exemption comment spec's own §1 asks for on this
/// constructor:</b> "a structural per-run limit, not a progression ceiling; the stash is uncapped." The
/// grid never reads Θ and never grows with content depth (§8's own count-pin ruling) — <c>ssot-power-
/// scale.md</c> §11 PS-8's cap register does not list it because it is not a magnitude cap at all.</para>
///
/// <para>Immutable: <see cref="With"/> returns a new grid with one more occupied rectangle, matching
/// <c>PackArranger.Arrange</c>'s own cited usage (`grid = grid.With(item, r, c)`) — the arranger owns
/// iterating candidate anchors, this type only ever answers "is this rectangle free" and "place one
/// here."</para>
/// </summary>
public sealed class PackGrid
{
    readonly IReadOnlyList<PackCell> _cells;

    public int Rows { get; }
    public int Cols { get; }
    public IReadOnlyList<PackCell> Cells => _cells;

    public PackGrid(int rows, int cols)
    {
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
        Rows = rows;
        Cols = cols;
        _cells = Array.Empty<PackCell>();
    }

    PackGrid(int rows, int cols, IReadOnlyList<PackCell> cells)
    {
        Rows = rows;
        Cols = cols;
        _cells = cells;
    }

    /// <summary>Every cell the footprint anchored at <paramref name="item"/>'s own placements
    /// currently covers, one entry per covered (row, col) — not just the anchor.</summary>
    IEnumerable<(int R, int C)> Footprint(int anchorRow, int anchorCol, int w, int h)
    {
        for (var dr = 0; dr < h; dr++)
            for (var dc = 0; dc < w; dc++)
                yield return (anchorRow + dr, anchorCol + dc);
    }

    /// <summary>True when every cell of the <paramref name="w"/>×<paramref name="h"/> rectangle
    /// anchored at (<paramref name="row"/>, <paramref name="col"/>) is inside the grid and unoccupied.</summary>
    public bool IsFree(int row, int col, int w, int h)
    {
        if (row < 0 || col < 0 || row + h > Rows || col + w > Cols) return false;
        var occupied = new HashSet<(int, int)>();
        foreach (var cell in _cells)
            foreach (var covered in Footprint(cell.Row, cell.Col, cell.Item.W, cell.Item.H))
                occupied.Add(covered);
        foreach (var covered in Footprint(row, col, w, h))
            if (occupied.Contains(covered)) return false;
        return true;
    }

    /// <summary>Places <paramref name="item"/> anchored at (<paramref name="row"/>, <paramref name="col"/>)
    /// — the caller (`PackArranger`) has already proven the rectangle is free via <see cref="IsFree"/>;
    /// this method does not re-check, matching the arranger's own pure, single-pass scan.</summary>
    public PackGrid With(PackItem item, int row, int col) =>
        new(Rows, Cols, _cells.Append(new PackCell(row, col, item)).ToList());

    /// <summary>The `itemKey` identity a `pack.move`/`pack.drop` decision names — an instance for
    /// equipment, the shared container id for a fungible stack (§3: "a cell holds one stack of one
    /// fungible container_id", so a stack has no per-unit instance to key on).</summary>
    public static string KeyOf(PackItem item) => item.InstanceId ?? item.RefId;

    /// <summary>The anchor cell currently holding `itemKey`, or null if this grid does not carry it —
    /// D3.21 (`PackMoves`) uses this to find what a move/drop decision refers to before touching it.</summary>
    public PackCell? Find(string itemKey) => _cells.FirstOrDefault(c => KeyOf(c.Item) == itemKey);

    /// <summary>Removes the cell keyed by <paramref name="itemKey"/> — the other half of a move (pick
    /// up, then place elsewhere via <see cref="With"/>). A no-op grid-shape-wise if the key is not
    /// present; the caller (`PackMoves`) is the one that knows whether that should refuse.</summary>
    public PackGrid Remove(string itemKey) =>
        new(Rows, Cols, _cells.Where(c => KeyOf(c.Item) != itemKey).ToList());
}
