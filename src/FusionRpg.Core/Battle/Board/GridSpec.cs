using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// What a cell is made of. An ordinal, per the seedsmith rule that a model picks enums and
/// deterministic code picks magnitudes — every movement cost and cover value keyed off this lives in
/// tuning (<see cref="SiegeTuning"/>), never here.
/// </summary>
public enum CellTerrain
{
    /// <summary>Ordinary ground. The default, and index 0, so a zero-filled array is a plain board.</summary>
    Open,
    /// <summary>Costs more to cross. Not impassable — a slow route is a decision, a wall is not.</summary>
    Rough,
    /// <summary>Blocks movement and line of sight. The district's own walls and terrain.</summary>
    Blocking,
    /// <summary>Blocks movement, does NOT block line of sight — a chasm, a moat, a rampart edge.</summary>
    Gap
}

public sealed class GridSpecRejection : Exception
{
    public GridSpecRejection(string message) : base(message) { }
}

/// <summary>
/// A board's dimensions and its per-cell terrain (spec-siege-board.md). Immutable and value-equal: a
/// board is an input to a battle, never mutable state the battle edits — <see cref="BoardState"/> is
/// the mutable half (who is standing where).
/// </summary>
public sealed record GridSpec
{
    // Get-only, not `init` — deliberately: an `init` setter lets `new GridSpec { Rows = 5000, ... }`
    // bypass the constructor entirely, which would silently defeat "Max_cells_is_enforced_loudly."
    // The constructor below is the ONLY way to build one, so the maxCells check can never be skipped.
    public int Rows { get; }
    public int Cols { get; }

    /// <summary>
    /// Row-major, length Rows*Cols. One byte-sized ordinal per cell — NOT a per-cell object: a
    /// district board is small, but this is read on every pathing step and every area enumeration,
    /// and an array of records would allocate on each.
    /// </summary>
    public IReadOnlyList<CellTerrain> Cells { get; }

    /// <summary>Builds and validates. `Rows*Cols` past <see cref="SiegeTuningPolicy.MaxCells"/>
    /// throws here, at construction — a district-layout bug is loud immediately, not at render.</summary>
    public GridSpec(int rows, int cols, IReadOnlyList<CellTerrain>? cells = null)
    {
        if (rows <= 0) throw new GridSpecRejection($"GridSpec: rows must be > 0; got {rows}");
        if (cols <= 0) throw new GridSpecRejection($"GridSpec: cols must be > 0; got {cols}");

        var cellCount = checked(rows * cols);
        if (cellCount > SiegeTuningPolicy.MaxCells)
            throw new GridSpecRejection(
                $"GridSpec: {rows}x{cols} = {cellCount} cells exceeds board.maxCells " +
                $"({SiegeTuningPolicy.MaxCells}) — a district-layout defect, not a legal board.");

        if (cells is not null && cells.Count != cellCount)
            throw new GridSpecRejection(
                $"GridSpec: {rows}x{cols} needs {cellCount} cells; got {cells.Count}.");

        Rows = rows;
        Cols = cols;
        Cells = cells ?? Enumerable.Repeat(CellTerrain.Open, cellCount).ToArray();
    }

    public bool Contains(GridPos p) => p.Row >= 0 && p.Row < Rows && p.Col >= 0 && p.Col < Cols;

    public int IndexOf(GridPos p)
    {
        if (!Contains(p))
            throw new GridSpecRejection($"GridSpec.IndexOf: {p} is outside {Rows}x{Cols}.");
        return p.Row * Cols + p.Col;
    }

    public CellTerrain TerrainAt(GridPos p) => Cells[IndexOf(p)];
}
