namespace FusionRpg.Core.World.Topology;

/// <summary>
/// Sectors whose loss cuts the network in two — Tarjan's algorithm, `O(V+E)`
/// (spec-world-topology.md).
///
/// A boolean where <see cref="ReconnectionCost"/> gives a number, and worth having separately
/// because it is far cheaper: "does this cut the empire" answers most garrison questions on its
/// own, and the expensive sweep only matters when you need to rank two junctions against each other.
///
/// The graph is treated as undirected, which it is for supply — every two-way lane contributed both
/// directions when the graph was built. Neighbours are visited in index order, which is ordinal id
/// order, because a depth-first search that wanders in a different order finds a different (equally
/// valid) answer, and replay cannot survive that.
/// </summary>
public static class ArticulationPoints
{
    public static IReadOnlySet<string> Find(LaneGraph graph)
    {
        var n = graph.Count;
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (n < 3) return found;   // nothing with two or fewer sectors can cut anything

        var adjacency = Adjacency(graph);
        var discovered = new int[n];
        var low = new int[n];
        var parent = new int[n];
        var seen = new bool[n];
        var isCut = new bool[n];
        var timer = 0;

        Array.Fill(parent, -1);

        for (var root = 0; root < n; root++)
        {
            if (seen[root]) continue;
            Walk(root, adjacency, discovered, low, parent, seen, isCut, ref timer);
        }

        for (var i = 0; i < n; i++)
            if (isCut[i])
                found.Add(graph.Sectors[i]);

        return found;
    }

    /// <summary>
    /// Iterative rather than recursive: a generated map could be deep enough to blow the stack, and
    /// a stack overflow inside a turn is not a failure anyone can diagnose from a replay.
    /// </summary>
    static void Walk(
        int root, List<int>[] adjacency, int[] discovered, int[] low, int[] parent,
        bool[] seen, bool[] isCut, ref int timer)
    {
        var stack = new Stack<(int Node, int NextNeighbour)>();
        var rootChildren = 0;

        seen[root] = true;
        discovered[root] = low[root] = timer++;
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();

            if (next < adjacency[node].Count)
            {
                stack.Push((node, next + 1));
                var neighbour = adjacency[node][next];

                if (neighbour == parent[node]) continue;

                if (seen[neighbour])
                {
                    // A back edge: this subtree can reach an ancestor without going through `node`.
                    if (discovered[neighbour] < low[node]) low[node] = discovered[neighbour];
                    continue;
                }

                parent[neighbour] = node;
                seen[neighbour] = true;
                discovered[neighbour] = low[neighbour] = timer++;
                if (node == root) rootChildren++;
                stack.Push((neighbour, 0));
                continue;
            }

            // Done with `node` — fold its low-link into its parent and decide whether the parent cuts.
            var up = parent[node];
            if (up < 0) continue;

            if (low[node] < low[up]) low[up] = low[node];

            // The parent is a cut vertex when this child cannot reach above it. The root is special:
            // it cuts only if it has more than one child, which is counted above.
            if (up != root && low[node] >= discovered[up]) isCut[up] = true;
        }

        if (rootChildren > 1) isCut[root] = true;
    }

    static List<int>[] Adjacency(LaneGraph graph)
    {
        var adjacency = new List<int>[graph.Count];
        for (var i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>();

        foreach (var edge in graph.Edges)
            adjacency[graph.IndexOf(edge.FromSectorId)].Add(graph.IndexOf(edge.ToSectorId));

        // Index order is ordinal id order; sorting makes the walk reproducible regardless of the
        // order lanes happened to be declared in.
        foreach (var list in adjacency) list.Sort();
        return adjacency;
    }
}
