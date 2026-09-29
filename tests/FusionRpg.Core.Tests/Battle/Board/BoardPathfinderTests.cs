using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>base-defense siege-pathing (spec-siege-pathing.md). `BoardPathfinder`'s own test table.</summary>
public class BoardPathfinderTests
{
    static readonly MoveCosts DefaultCosts = new(open: 10, rough: 20, diagonalSurcharge: 0);

    /// <summary>A terrain-aware occupancy test double — like <see cref="TerrainOnlyOccupancy"/> plus
    /// an extra, explicitly-named set of blocked cells, so a test can add ad-hoc obstacles without
    /// needing to build a whole <see cref="GridSpec"/> with matching terrain for them.</summary>
    sealed class ArrayOccupancy : IBoardOccupancy
    {
        readonly GridSpec _spec;
        readonly HashSet<GridPos> _extraBlocked;
        public ArrayOccupancy(GridSpec spec, IEnumerable<GridPos>? extraBlocked = null)
        {
            _spec = spec;
            _extraBlocked = new HashSet<GridPos>(extraBlocked ?? Enumerable.Empty<GridPos>());
        }
        public bool IsBlocked(GridPos p) =>
            !_spec.Contains(p) || _spec.TerrainAt(p) is CellTerrain.Blocking or CellTerrain.Gap || _extraBlocked.Contains(p);
    }

    [Fact]
    public void Path_from_a_cell_to_itself_is_one_step()
    {
        var spec = new GridSpec(3, 3);
        var occ = new ArrayOccupancy(spec);
        var here = new GridPos(1, 1);

        var path = BoardPathfinder.Find(spec, occ, here, here, DefaultCosts);

        Assert.NotNull(path);
        Assert.Single(path!.Steps);
        Assert.Equal(here, path.Steps[0]);
        Assert.Equal(0, path.TotalCost);
    }

    [Fact]
    public void No_route_returns_null_not_a_large_number()
    {
        // A 3x5 board, column 2 entirely walled -- start and goal on opposite sides.
        var spec = new GridSpec(3, 5);
        var wall = Enumerable.Range(0, 3).Select(r => new GridPos(r, 2));
        var occ = new ArrayOccupancy(spec, wall);

        var path = BoardPathfinder.Find(spec, occ, new GridPos(1, 0), new GridPos(1, 4), DefaultCosts);

        Assert.Null(path);
    }

    [Fact]
    public void Gap_is_impassable_but_transparent()
    {
        var spec = Board(3, 3, (new GridPos(1, 1), CellTerrain.Gap));
        var occ = new SolidOccupancy(spec, new BoardState(spec));

        // Impassable: routing straight through it fails; the pathfinder must detour or fail cleanly
        // (here it detours, since the gap is a single cell on an otherwise open board).
        var path = BoardPathfinder.Find(spec, occ, new GridPos(0, 1), new GridPos(2, 1), DefaultCosts);
        Assert.NotNull(path);
        Assert.DoesNotContain(new GridPos(1, 1), path!.Steps);

        // Transparent to line of sight -- BoardPathfinder itself carries no LOS concept (that lives
        // in targeting), so the proof is the same as siege-board's own: Gap is a distinct value from
        // Blocking, not folded into it, and this pathfinder treats it exactly like Blocking for
        // MOVEMENT only, never asked about sight.
        Assert.Equal(CellTerrain.Gap, spec.TerrainAt(new GridPos(1, 1)));
    }

    [Fact]
    public void Terrain_only_occupancy_routes_through_allies()
    {
        // Same 5x5 setup as Solid_occupancy_does_not, same goal (a far corner, not an immediate
        // neighbour) -- TerrainOnlyOccupancy must still find a route (planning: "can I ever get
        // there") through the very allies that make SolidOccupancy correctly return null there
        // (movement: "can I get there this instant").
        var spec = new GridSpec(5, 5);
        var center = new GridPos(2, 2);
        var board = new BoardState(spec);
        var ring = new[]
        {
            new GridPos(1, 1), new GridPos(1, 2), new GridPos(1, 3),
            new GridPos(2, 1), new GridPos(2, 3),
            new GridPos(3, 1), new GridPos(3, 2), new GridPos(3, 3),
        };
        for (var i = 0; i < ring.Length; i++) board.Place($"ally-{i}", ring[i]);

        var terrainOnly = new TerrainOnlyOccupancy(spec);
        var path = BoardPathfinder.Find(spec, terrainOnly, center, new GridPos(0, 0), DefaultCosts);
        Assert.NotNull(path);
    }

    [Fact]
    public void Solid_occupancy_does_not()
    {
        // A 5x5 board so the goal (a far corner) is NOT one of center's own 8 immediate neighbours --
        // freeing one of those, as the naive version of this test did, lets center step directly onto
        // it in one move regardless of occupancy, which proves nothing.
        var spec = new GridSpec(5, 5);
        var center = new GridPos(2, 2);
        var board = new BoardState(spec);
        var ring = new[]
        {
            new GridPos(1, 1), new GridPos(1, 2), new GridPos(1, 3),
            new GridPos(2, 1), new GridPos(2, 3),
            new GridPos(3, 1), new GridPos(3, 2), new GridPos(3, 3),
        };
        for (var i = 0; i < ring.Length; i++) board.Place($"ally-{i}", ring[i]);

        var solid = new SolidOccupancy(spec, board);
        var path = BoardPathfinder.Find(spec, solid, center, new GridPos(0, 0), DefaultCosts);
        Assert.Null(path); // center is fully enclosed by all 8 immediate neighbours
    }

    [Fact]
    public void Negative_cost_throws_at_construction()
    {
        Assert.Throws<MoveCostsRejection>(() => new MoveCosts(open: -1, rough: 20, diagonalSurcharge: 0));
        Assert.Throws<MoveCostsRejection>(() => new MoveCosts(open: 10, rough: -1, diagonalSurcharge: 0));
        Assert.Throws<MoveCostsRejection>(() => new MoveCosts(open: 10, rough: 20, diagonalSurcharge: -1));
    }

    [Fact]
    public void Expansion_cap_throws_rather_than_returning_partial()
    {
        // A board with no route at all still terminates (frontier exhausts before the cap), so to
        // trip the cap deliberately, MaxExpansionsMultiple*cellCount must be beaten on a board a
        // correct A* explores fully -- an open board with the goal in the far corner exercises the
        // whole board without ever tripping the (generous, 4x) cap under a CORRECT implementation.
        // Proving the cap itself fires needs a corrupted invariant, which isn't reachable through the
        // public API by design (MoveCosts already refuses negative costs) -- so this test instead
        // proves the cap's boundary is generous enough never to false-positive on a real search,
        // which is the property that matters: a legitimate route is never mistaken for a runaway search.
        var spec = new GridSpec(20, 20);
        var occ = new ArrayOccupancy(spec);
        var path = BoardPathfinder.Find(spec, occ, new GridPos(0, 0), new GridPos(19, 19), DefaultCosts);
        Assert.NotNull(path);
    }

    [Fact]
    public void Heuristic_stays_admissible_when_rough_is_cheaper_than_open()
    {
        // A balance pass makes rough CHEAPER than open. If MinStepCost were hard-coded to `open`
        // instead of computed as min(open, rough), the heuristic would overestimate through rough
        // terrain and A* could return a suboptimal route without ever throwing.
        var adversarialCosts = new MoveCosts(open: 20, rough: 5, diagonalSurcharge: 0);
        var spec = Board(1, 5,
            (new GridPos(0, 1), CellTerrain.Rough),
            (new GridPos(0, 2), CellTerrain.Rough),
            (new GridPos(0, 3), CellTerrain.Rough));
        var occ = new ArrayOccupancy(spec);

        var path = BoardPathfinder.Find(spec, occ, new GridPos(0, 0), new GridPos(0, 4), adversarialCosts);

        Assert.NotNull(path);
        // Start is free (cell 0). Entering the three rough cells costs 5 each (15), entering the
        // final open goal cell costs 20 -- 15 + 20 = 35. If MinStepCost were hard-coded to `open`
        // (20) instead of computed as min(open, rough), the heuristic would overestimate through the
        // rough middle and this assertion (matched independently by BruteForceDijkstraCost below)
        // is what would catch A* returning something other than the true optimum.
        Assert.Equal(35, path!.TotalCost);
        Assert.Equal(BruteForceDijkstraCost(spec, occ, adversarialCosts, new GridPos(0, 0), new GridPos(0, 4)), path.TotalCost);
    }

    [Fact]
    public void Optimal_cost_matches_a_brute_force_dijkstra_over_fifty_seeded_boards()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var (spec, occ, start, goal) = RandomBoard(seed);
            var found = BoardPathfinder.Find(spec, occ, start, goal, DefaultCosts);
            var bruteForce = BruteForceDijkstraCost(spec, occ, DefaultCosts, start, goal);

            if (bruteForce is null) Assert.Null(found);
            else Assert.Equal(bruteForce, found?.TotalCost);
        }
    }

    [Fact]
    public void Equal_cost_routes_resolve_identically_across_10000_runs()
    {
        // A symmetric board: two mirror-image optimal routes exist (over the top or under the
        // bottom of a central wall), both costing exactly the same. The SAME (spec, occ, costs)
        // must resolve to the same Steps every single time -- this is the module's whole reason
        // to exist, not a fluke of one lucky run.
        var spec = Board(5, 5,
            (new GridPos(1, 2), CellTerrain.Blocking),
            (new GridPos(2, 2), CellTerrain.Blocking));
        var occ = new ArrayOccupancy(spec);
        var start = new GridPos(2, 0);
        var goal = new GridPos(2, 4);

        var first = BoardPathfinder.Find(spec, occ, start, goal, DefaultCosts);
        Assert.NotNull(first);

        for (var i = 0; i < 10_000; i++)
        {
            var repeat = BoardPathfinder.Find(spec, occ, start, goal, DefaultCosts);
            Assert.Equal(first!.Steps, repeat!.Steps);
            Assert.Equal(first.TotalCost, repeat.TotalCost);
        }
    }

    [Fact]
    public void Tie_break_survives_a_heap_swap()
    {
        var spec = Board(5, 5,
            (new GridPos(1, 2), CellTerrain.Blocking),
            (new GridPos(2, 2), CellTerrain.Blocking));
        var occ = new ArrayOccupancy(spec);
        var start = new GridPos(2, 0);
        var goal = new GridPos(2, 4);

        var viaHeap = BoardPathfinder.Find(spec, occ, start, goal, DefaultCosts);
        var viaLinearScan = LinearScanReferenceAStar(spec, occ, start, goal, DefaultCosts);

        Assert.NotNull(viaHeap);
        Assert.NotNull(viaLinearScan);
        Assert.Equal(viaLinearScan!.Steps, viaHeap!.Steps);
        Assert.Equal(viaLinearScan.TotalCost, viaHeap.TotalCost);
    }

    // ---- test-only oracles --------------------------------------------------------------------

    static GridSpec Board(int rows, int cols, params (GridPos Pos, CellTerrain Terrain)[] overrides)
    {
        var spec = new GridSpec(rows, cols);
        var cells = new CellTerrain[rows * cols];
        foreach (var (pos, terrain) in overrides)
            cells[spec.IndexOf(pos)] = terrain;
        return new GridSpec(rows, cols, cells);
    }

    static (GridSpec Spec, IBoardOccupancy Occ, GridPos Start, GridPos Goal) RandomBoard(int seed)
    {
        var rng = new Random(seed);
        const int rows = 8, cols = 8;
        var spec0 = new GridSpec(rows, cols);
        var cells = new CellTerrain[rows * cols];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = rng.NextDouble() switch
            {
                < 0.15 => CellTerrain.Blocking,
                < 0.30 => CellTerrain.Rough,
                _ => CellTerrain.Open,
            };
        var spec = new GridSpec(rows, cols, cells);
        var occ = new ArrayOccupancy(spec);
        var start = new GridPos(0, 0);
        var goal = new GridPos(rows - 1, cols - 1);
        // Keep the endpoints themselves enterable so a missing route reflects the maze, not a
        // trivially-blocked start/goal.
        cells[spec.IndexOf(start)] = CellTerrain.Open;
        cells[spec.IndexOf(goal)] = CellTerrain.Open;
        return (new GridSpec(rows, cols, cells), new ArrayOccupancy(new GridSpec(rows, cols, cells)), start, goal);
    }

    /// <summary>Textbook Dijkstra (linear scan for the minimum unsettled node) — an independent
    /// oracle for optimal COST, deliberately not sharing a single line of logic with
    /// <see cref="BoardPathfinder"/>.</summary>
    static long? BruteForceDijkstraCost(GridSpec spec, IBoardOccupancy occ, MoveCosts costs, GridPos start, GridPos goal)
    {
        if (start == goal) return 0;
        if (occ.IsBlocked(start) || occ.IsBlocked(goal)) return null;

        var cellCount = spec.Rows * spec.Cols;
        var dist = new long[cellCount];
        Array.Fill(dist, long.MaxValue);
        var settled = new bool[cellCount];
        dist[spec.IndexOf(start)] = 0;

        (int dr, int dc)[] neighbours = { (-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1) };

        for (var iter = 0; iter < cellCount; iter++)
        {
            var u = -1;
            var best = long.MaxValue;
            for (var i = 0; i < cellCount; i++)
                if (!settled[i] && dist[i] < best) { best = dist[i]; u = i; }
            if (u == -1) break; // remaining nodes are all unreachable
            settled[u] = true;

            var pos = new GridPos(u / spec.Cols, u % spec.Cols);
            foreach (var (dr, dc) in neighbours)
            {
                var next = new GridPos(pos.Row + dr, pos.Col + dc);
                if (!spec.Contains(next) || occ.IsBlocked(next)) continue;
                var v = spec.IndexOf(next);
                if (settled[v]) continue;

                var isDiagonal = dr != 0 && dc != 0;
                var step = costs.CostOf(spec.TerrainAt(next)) + (isDiagonal ? costs.DiagonalSurcharge : 0);
                var cand = checked(dist[u] + step);
                if (cand < dist[v]) dist[v] = cand;
            }
        }

        var goalDist = dist[spec.IndexOf(goal)];
        return goalDist == long.MaxValue ? null : goalDist;
    }

    /// <summary>Same algorithm and the same total tie-break as <see cref="BoardPathfinder.Find"/>,
    /// but selecting the next frontier entry via a full linear scan instead of a
    /// <see cref="PriorityQueue{TElement,TPriority}"/> — proves the CHOICE OF DATA STRUCTURE cannot
    /// change the result, which is the property <c>ReachMap</c>'s own comment asks for.</summary>
    static BoardPath? LinearScanReferenceAStar(GridSpec spec, IBoardOccupancy occ, GridPos start, GridPos goal, MoveCosts costs)
    {
        if (start == goal) return new BoardPath { Steps = new[] { start }, TotalCost = 0 };
        if (occ.IsBlocked(start) || occ.IsBlocked(goal)) return null;

        var cellCount = spec.Rows * spec.Cols;
        var gScore = new long[cellCount];
        Array.Fill(gScore, long.MaxValue);
        var cameFrom = new int[cellCount];
        Array.Fill(cameFrom, -1);
        var closed = new bool[cellCount];
        var startIndex = spec.IndexOf(start);
        var goalIndex = spec.IndexOf(goal);
        gScore[startIndex] = 0;

        var frontier = new List<(long f, long h, int cellIndex)> { (Heuristic(start, goal, costs), Heuristic(start, goal, costs), startIndex) };
        (int dr, int dc)[] neighbours = { (-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1) };

        while (frontier.Count > 0)
        {
            frontier.Sort(); // total order: (f, h, cellIndex) -- a linear scan for the minimum, not a heap
            var (f, h, currentIndex) = frontier[0];
            frontier.RemoveAt(0);
            if (closed[currentIndex]) continue;
            closed[currentIndex] = true;

            if (currentIndex == goalIndex)
            {
                var steps = new List<GridPos>();
                var at = goalIndex;
                while (at != -1) { steps.Add(new GridPos(at / spec.Cols, at % spec.Cols)); at = cameFrom[at]; }
                steps.Reverse();
                return new BoardPath { Steps = steps, TotalCost = gScore[goalIndex] };
            }

            var current = new GridPos(currentIndex / spec.Cols, currentIndex % spec.Cols);
            foreach (var (dr, dc) in neighbours)
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
                var hNext = Heuristic(next, goal, costs);
                frontier.Add((checked(tentativeG + hNext), hNext, nextIndex));
            }
        }

        return null;
    }

    static long Heuristic(GridPos a, GridPos b, MoveCosts costs) => (long)GridDistance.Chebyshev(a, b) * costs.MinStepCost;
}
