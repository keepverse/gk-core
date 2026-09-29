using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Board;

public sealed class BoardPathfinderRejection : Exception
{
    public BoardPathfinderRejection(string message) : base(message) { }
}

/// <summary>
/// A route between two cells (spec-siege-pathing.md §5). Start-inclusive, goal-inclusive — a path
/// from a cell to itself is one step, not zero, so a caller slicing by movement budget never hits an
/// empty-list special case.
/// </summary>
public sealed record BoardPath
{
    public IReadOnlyList<GridPos> Steps { get; init; } = Array.Empty<GridPos>();

    /// <summary><b>long.</b> A sum over an unbounded step count (docs/architecture/numeric-types.md rule 1) — the per-step
    /// cost is `int`, the accumulator is not.</summary>
    public long TotalCost { get; init; }
}

/// <summary>
/// A* with an explicit, total tie-break (spec-siege-pathing.md §1). The heap's own internal pop
/// order among equal-priority entries is unspecified by the BCL, so every frontier entry carries a
/// total key — <c>(fScore, hScore, cellIndex)</c> — under which no two entries can ever compare
/// equal. With a total comparator there is exactly one valid pop sequence, so any correct heap
/// (this one, a hand-rolled one, a future replacement) produces byte-identical routes forever.
/// </summary>
public static class BoardPathfinder
{
    /// <summary>Structural bound on one search, not a progression ceiling (AGENTS.md's exemption for
    /// per-runtime caps). A correct A* expands each cell at most once, so 4x cellCount is pure
    /// headroom — exceeding it means the invariant broke (an unbounded board, a cost gone negative
    /// past MoveCosts' own guard), and that must throw rather than return a wrong-but-plausible
    /// partial route indistinguishable from "no route."</summary>
    const int MaxExpansionsMultiple = 4;

    // Row-major clockwise from north-west. FIXED ORDER -- a change here changes which of two
    // equal-cost routes is returned, which changes a replay. Not a style choice.
    static readonly (int dr, int dc)[] Neighbours =
        { (-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1) };

    /// <summary>Cheapest route between two cells, or null when none exists. Null is "no route",
    /// never a large number — the same distinction <c>ReachMap</c> already draws by making an
    /// unreachable sector absent: a caller that conflates them walks a unit at a wall forever.</summary>
    public static BoardPath? Find(GridSpec spec, IBoardOccupancy occ, GridPos start, GridPos goal, MoveCosts costs)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        if (occ is null) throw new ArgumentNullException(nameof(occ));
        if (costs is null) throw new ArgumentNullException(nameof(costs));
        if (!spec.Contains(start)) throw new BoardPathfinderRejection($"BoardPathfinder.Find: start {start} is outside the board.");
        if (!spec.Contains(goal)) throw new BoardPathfinderRejection($"BoardPathfinder.Find: goal {goal} is outside the board.");

        if (start == goal)
            return new BoardPath { Steps = new[] { start }, TotalCost = 0 };

        if (occ.IsBlocked(start) || occ.IsBlocked(goal))
            return null;

        var cellCount = spec.Rows * spec.Cols;
        var gScore = new long[cellCount];
        Array.Fill(gScore, long.MaxValue);
        var cameFrom = new int[cellCount];
        Array.Fill(cameFrom, -1);
        var closed = new bool[cellCount];

        var startIndex = spec.IndexOf(start);
        var goalIndex = spec.IndexOf(goal);
        gScore[startIndex] = 0;

        var frontier = new PriorityQueue<int, (long f, long h, int cellIndex)>();
        frontier.Enqueue(startIndex, (Heuristic(start, goal, costs), Heuristic(start, goal, costs), startIndex));

        var maxExpansions = checked(MaxExpansionsMultiple * cellCount);
        var expansions = 0;

        while (frontier.Count > 0)
        {
            var currentIndex = frontier.Dequeue();
            if (closed[currentIndex]) continue; // a stale duplicate entry -- the current best already settled this cell
            closed[currentIndex] = true;

            if (currentIndex == goalIndex)
                return Reconstruct(spec, cameFrom, goalIndex, gScore[goalIndex]);

            expansions++;
            if (expansions > maxExpansions)
                throw new BoardPathfinderRejection(
                    $"BoardPathfinder.Find: exceeded {maxExpansions} expansions ({MaxExpansionsMultiple}x " +
                    $"{cellCount} cells) -- a correct A* never does; this is a defect, not a hard board.");

            var current = new GridPos(currentIndex / spec.Cols, currentIndex % spec.Cols);

            foreach (var (dr, dc) in Neighbours)
            {
                var next = new GridPos(current.Row + dr, current.Col + dc);
                if (!spec.Contains(next) || occ.IsBlocked(next)) continue;

                var nextIndex = spec.IndexOf(next);
                if (closed[nextIndex]) continue;

                var isDiagonal = dr != 0 && dc != 0;
                var stepCost = costs.CostOf(spec.TerrainAt(next)) + (isDiagonal ? costs.DiagonalSurcharge : 0);
                var tentativeG = checked(gScore[currentIndex] + stepCost);

                if (tentativeG >= gScore[nextIndex]) continue;

                gScore[nextIndex] = tentativeG;
                cameFrom[nextIndex] = currentIndex;

                var h = Heuristic(next, goal, costs);
                var f = checked(tentativeG + h);
                frontier.Enqueue(nextIndex, (f, h, nextIndex));
            }
        }

        return null; // frontier exhausted -- no route
    }

    /// <summary>Widen before multiplying (docs/architecture/numeric-types.md rule 3): the cast binds to the whole product, not
    /// the result of an int multiply that already overflowed.</summary>
    static long Heuristic(GridPos a, GridPos b, MoveCosts costs) =>
        (long)GridDistance.Chebyshev(a, b) * costs.MinStepCost;

    static BoardPath Reconstruct(GridSpec spec, int[] cameFrom, int goalIndex, long totalCost)
    {
        var steps = new List<GridPos>();
        var at = goalIndex;
        while (at != -1)
        {
            steps.Add(new GridPos(at / spec.Cols, at % spec.Cols));
            at = cameFrom[at];
        }
        steps.Reverse();
        return new BoardPath { Steps = steps, TotalCost = totalCost };
    }
}
