using FusionRpg.Core.Battle.Board;

namespace FusionRpg.Core.Actions.Movement;

/// <summary>
/// A9 `movement-actions` (spec-movement-actions.md) — the authored row's own resolution. Deliberately
/// ONE file and ONE pure function: the spec's own words are "if this module grows a runtime of its
/// own, movement has stopped being an ordinary action and something is wrong" — this is a decision,
/// not a mechanism `BattleRunState`/`BoardPathfinder` do not already provide.
///
/// <para><b>Destination choice — "move toward the nearest living enemy," owner-confirmed 2026-09-07.</b>
/// `ActionAnchorSource.ChosenCell` names a target shape but no spec anywhere resolves it to a real
/// cell; nothing in the codebase read it before this (confirmed by grep). This is the one, minimal,
/// deterministic rule chosen: matches the existing basic-attack AI's own "nearest enemy" convention
/// (<c>StubIntentSource</c>/<c>SourceOrder</c>), so a movement action decides its target the same way
/// every other autonomous decision in this engine already does — not a new philosophy.</para>
///
/// <para><b>Greedy, one cell at a time — not a full A* route to the enemy's own cell.</b> The enemy's
/// cell is always occupied, so pathing TO it (not adjacent to it) would return no route by
/// <see cref="BoardPathfinder"/>'s own design ("occ.IsBlocked(goal) -- return null"). A full
/// nearest-open-neighbour-of-the-target search is unrequested complexity for what the spec calls "a
/// cheap step" — greedily picking the single legal neighbour that most reduces Chebyshev distance,
/// repeated up to the actor's own <c>move.range</c>, produces the same qualitative behaviour (walk
/// toward the enemy, stop being blocked by occupied cells) with none of the goal-is-occupied special
/// casing a full pathfind would need.</para>
/// </summary>
public static class MoveAction
{
    /// <summary>
    /// The single legal neighbour of <paramref name="from"/> (8-directional, board-legal per
    /// <see cref="BoardState.CanEnter"/>) that most reduces Chebyshev distance to
    /// <paramref name="target"/> — null when already adjacent/equal, or when every neighbour is
    /// illegal or does not improve distance (never a worse or sideways step; standing still is
    /// preferred over a pointless move).
    /// </summary>
    public static GridPos? NearestLegalStepToward(BoardState board, GridPos from, GridPos target)
    {
        ArgumentNullException.ThrowIfNull(board);

        var currentDistance = GridDistance.Chebyshev(from, target);
        if (currentDistance == 0) return null;

        GridPos? best = null;
        var bestDistance = currentDistance;

        // Row-major, north-west first -- the SAME fixed tie-break order BoardPathfinder's own
        // Neighbours table uses, so two equally-good steps resolve identically every time, not by
        // whatever order a hash-based enumeration happens to produce.
        for (var dr = -1; dr <= 1; dr++)
        for (var dc = -1; dc <= 1; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            var next = new GridPos(from.Row + dr, from.Col + dc);
            if (!board.CanEnter(next)) continue;

            var d = GridDistance.Chebyshev(next, target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = next;
            }
        }

        return best;
    }

    /// <summary>
    /// Moves <paramref name="actorKey"/> up to <paramref name="maxCells"/> legal steps toward
    /// <paramref name="target"/>, one <see cref="BoardState.Move"/> per step so
    /// <see cref="BoardState.Entered"/>/<see cref="BoardState.Exited"/> fire once per cell exactly as
    /// they would for any other caller. Stops early the moment no legal improving step exists (adjacent
    /// already, or boxed in) — never throws for "nowhere better to go", since that is an ordinary
    /// outcome for a movement action, not a caller error. Returns the number of cells actually moved.
    /// </summary>
    public static int MoveToward(BoardState board, string actorKey, GridPos target, int maxCells)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (maxCells <= 0) return 0;
        if (!board.Positions.TryGetValue(actorKey, out var at)) return 0;

        var moved = 0;
        for (var i = 0; i < maxCells; i++)
        {
            var step = NearestLegalStepToward(board, at, target);
            if (step is not { } next) break;
            board.Move(actorKey, next);
            at = next;
            moved++;
        }
        return moved;
    }
}
