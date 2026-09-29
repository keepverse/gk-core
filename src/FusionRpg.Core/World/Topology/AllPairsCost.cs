namespace FusionRpg.Core.World.Topology;

/// <summary>
/// How far everything is from everything, by Floyd–Warshall (spec-world-topology.md).
///
/// `O(V³)` and unapologetic about it: wave 1 has six sectors and this stays comfortable into the
/// hundreds. The cliff, and the two ways off it, are written down in the spec so nobody meets it by
/// surprise when the generator starts making large maps.
///
/// Unreachable is a large sentinel rather than <see cref="int.MaxValue"/>, because
/// <see cref="ReconnectionCost"/> sums differences across every pair — a sentinel that overflows
/// when added to itself would turn "the empire split in half" into a negative number.
/// </summary>
public sealed class AllPairsCost
{
    /// <summary>Far enough to mean "no route", small enough that ten thousand of them still add up.</summary>
    public const int Unreachable = 100_000_000;

    readonly LaneGraph _graph;
    readonly int[] _cost;

    AllPairsCost(LaneGraph graph, int[] cost)
    {
        _graph = graph;
        _cost = cost;
    }

    public bool Reachable(string from, string to) => Between(from, to) < Unreachable;

    public int Between(string from, string to) => _cost[_graph.IndexOf(from) * _graph.Count + _graph.IndexOf(to)];

    public static AllPairsCost Compute(LaneGraph graph)
    {
        var n = graph.Count;
        var cost = new int[n * n];

        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            cost[i * n + j] = i == j ? 0 : Unreachable;

        foreach (var edge in graph.Edges)
        {
            var i = graph.IndexOf(edge.FromSectorId);
            var j = graph.IndexOf(edge.ToSectorId);
            // Parallel lanes between the same pair are legal at the model level; keep the cheaper.
            if (edge.Cost < cost[i * n + j]) cost[i * n + j] = edge.Cost;
        }

        for (var k = 0; k < n; k++)
        for (var i = 0; i < n; i++)
        {
            var ik = cost[i * n + k];
            if (ik >= Unreachable) continue;   // nothing routes through an unreachable waypoint

            for (var j = 0; j < n; j++)
            {
                var kj = cost[k * n + j];
                if (kj >= Unreachable) continue;

                var through = ik + kj;
                if (through < cost[i * n + j]) cost[i * n + j] = through;
            }
        }

        return new AllPairsCost(graph, cost);
    }

}
