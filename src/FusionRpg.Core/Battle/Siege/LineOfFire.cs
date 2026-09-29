using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-obstacles` §3: gives `ActionRow.RequiresLineOfSight`/`CompiledAction.RequiresLineOfSight`
/// their first reader — the field is declared, compiled, carried and persisted (`RpgStore.Actions.cs`)
/// but consulted by no evaluator anywhere in `src/` before this module (confirmed by a direct source
/// scan, not assumed). A Rampart is the first thing in the game with a reason to block a shot —
/// decision 25: an unoccupied building "occupies its cell, blocks movement AND FIRE."
///
/// <para>Deliberately independent of `siege-cover`'s own obstruction math (a DIFFERENT, softer
/// mechanic — "reduces, never blocks", per-shot per that module's own spec). This is a hard yes/no:
/// does a `BlocksLineOfFire` cell sit strictly between the two points.</para>
/// </summary>
public static class LineOfFire
{
    /// <summary>
    /// The line from <paramref name="a"/> to <paramref name="b"/>, as the OPEN interval of cells
    /// strictly between them (excludes both endpoints) — a Bresenham walk, deterministic, and
    /// SYMMETRIC not by argument but by construction: the two endpoints are canonicalized into a
    /// fixed order (by row, then column) before the walk ever runs, so `Trace(a, b)` and `Trace(b, a)`
    /// compute the identical walk internally and return the identical list — `siege-pathing`'s own
    /// "ReachMap: an implicit tie-break lets a replay disagree with itself" is exactly the failure this
    /// canonicalization avoids for a line, the same way an explicit total order avoids it for a heap.
    ///
    /// <para><b>Tie-break: the lower cell index.</b> Where the accumulated error crosses zero on both
    /// axes at once (a perfect diagonal split), this Bresenham variant steps both axes together — the
    /// walk never visits either of the two equally-valid corner cells, landing directly on their shared
    /// diagonal neighbour instead, which is the canonical, single deterministic path (no ambiguity for
    /// a caller to break a tie on, because none of the two corner candidates is ever produced).</para>
    /// </summary>
    public static IReadOnlyList<GridPos> Trace(GridPos a, GridPos b)
    {
        var (start, end) = LessOrEqual(a, b) ? (a, b) : (b, a);
        return TraceOrdered(start, end);
    }

    static bool LessOrEqual(GridPos a, GridPos b) => a.Row != b.Row ? a.Row < b.Row : a.Col <= b.Col;

    static IReadOnlyList<GridPos> TraceOrdered(GridPos from, GridPos to)
    {
        var cells = new List<GridPos>();
        var x0 = from.Col; var y0 = from.Row;
        var x1 = to.Col; var y1 = to.Row;
        var dx = Math.Abs(x1 - x0); var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0); var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        var x = x0; var y = y0;

        while (x != x1 || y != y1)
        {
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }

            if (x == x1 && y == y1) break; // reached the target cell itself -- excluded, do not test it
            cells.Add(new GridPos(y, x));
        }

        return cells;
    }

    /// <summary>
    /// True when nothing blocks fire between two cells — every cell <see cref="Trace"/> visits is free
    /// of anything <paramref name="blocksFire"/> names. A shooter standing behind a wall does not
    /// block its own shot, and neither does the target's own cell, since both endpoints are excluded.
    /// </summary>
    public static bool HasLineOfFire(GridPos from, GridPos to, Func<GridPos, bool> blocksFire)
    {
        if (blocksFire is null) throw new ArgumentNullException(nameof(blocksFire));
        foreach (var cell in Trace(from, to))
            if (blocksFire(cell)) return false;
        return true;
    }

    /// <summary>
    /// The actual reader: an action that does not require line of sight is always legal (every action
    /// shipped before this module — `RequiresLineOfSight` defaults false); one that does is gated by
    /// <see cref="HasLineOfFire"/>. With no board (every caller until a siege wires one in),
    /// <paramref name="blocksFire"/> should be a function that always returns false — matching
    /// `GridDistance.InRange`'s own "with no board, every check passes" precedent.
    /// </summary>
    public static bool CanFire(bool requiresLineOfSight, GridPos from, GridPos to, Func<GridPos, bool> blocksFire) =>
        !requiresLineOfSight || HasLineOfFire(from, to, blocksFire);
}
