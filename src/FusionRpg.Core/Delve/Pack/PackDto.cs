namespace FusionRpg.Core.Delve.Pack;

/// <summary>One rendered cell (placed or floored) — no `PartyIndex` (spec-loot-pack.md §Interface:
/// "the stage names parties", never this DTO). <see cref="Movable"/> is the SAME value for every cell
/// of one party's pack: false while that party runs on autopilot, so a client never needs its own
/// rule to disable dragging — the model already says no (§Interface: "disabled at the model, never by
/// a client-side rule").</summary>
public sealed record PackCellDto(int Row, int Col, int W, int H, string Kind, string RefId, long Qty, string Origin, bool Movable);

/// <summary>D3.22 (spec-loot-pack.md §Interface) — the wave-5 layer's own read model: `rows, cols,
/// cells[], floor[], provisionCellsLeft`. `Floor` is supplied by the caller from
/// `rpg_delve_rooms.floor_json` (already `delve-scope`'s own column for the party's current room) —
/// this projection does not read a store itself.</summary>
public sealed record PackDto(int Rows, int Cols, IReadOnlyList<PackCellDto> Cells, IReadOnlyList<PackCellDto> Floor, int ProvisionCellsLeft);

public static class PackDtoProjection
{
    static PackCellDto ToDto(PackCell cell, bool movable) => new(
        cell.Row, cell.Col, cell.Item.W, cell.Item.H, cell.Item.Kind, cell.Item.RefId, cell.Item.Qty,
        cell.Item.Origin.ToString(), movable);

    static PackCellDto ToFloorDto(PackItem item, bool movable) => new(
        Row: -1, Col: -1, item.W, item.H, item.Kind, item.RefId, item.Qty, item.Origin.ToString(), movable);

    /// <summary>`provisionCellsLeft = provisionCells - Σ(carry-in footprints still carried)` — floor
    /// items were never re-provisioned, so only cells still IN the grid count against the allowance.</summary>
    public static PackDto Project(PackGrid grid, IReadOnlyList<PackItem> floor, int provisionCells, bool partySteered)
    {
        if (grid is null) throw new ArgumentNullException(nameof(grid));
        if (floor is null) throw new ArgumentNullException(nameof(floor));

        var carriedInCells = grid.Cells.Where(c => c.Item.Origin == PackItemOrigin.CarryIn).Sum(c => c.Item.W * c.Item.H);
        return new PackDto(
            grid.Rows, grid.Cols,
            grid.Cells.Select(c => ToDto(c, partySteered)).ToList(),
            floor.Select(f => ToFloorDto(f, partySteered)).ToList(),
            provisionCells - carriedInCells);
    }
}
