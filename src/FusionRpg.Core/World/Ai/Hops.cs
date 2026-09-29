using FusionRpg.Core.World.Topology;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// How many lanes away everything is from one sector (spec-ai-commander.md §ThreatMap).
///
/// Unweighted on purpose, and therefore *not* <see cref="AllPairsCost"/>, which measures per-mille
/// march cost. Threat spreads by "how far could it have got since I looked", and a force gets one
/// sector further per turn regardless of whether that lane was long or short. Pricing the spread
/// would make a nearby enemy across an expensive lane feel further away than a distant one across a
/// cheap one, which is backwards.
/// </summary>
public static class Hops
{
    /// <summary>
    /// Hop counts from <paramref name="origin"/>, breadth-first in ordinal order. Sectors the origin
    /// cannot reach are absent rather than reported as far away — "no route" and "a long way" are
    /// different answers and a caller that conflates them will spread fear across a severed map.
    /// </summary>
    public static IReadOnlyDictionary<string, int> From(LaneGraph graph, string origin)
    {
        var distance = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!graph.Contains(origin)) return distance;

        // Neighbours in ordinal order so the walk is reproducible. The distances would be the same
        // either way; the *order they are discovered in* would not, and anything that later folds
        // over this in insertion order would drift.
        var neighbours = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!neighbours.TryGetValue(edge.FromSectorId, out var list))
                neighbours[edge.FromSectorId] = list = new List<string>();
            list.Add(edge.ToSectorId);
        }

        foreach (var list in neighbours.Values) list.Sort(StringComparer.Ordinal);

        distance[origin] = 0;
        var frontier = new Queue<string>();
        frontier.Enqueue(origin);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (!neighbours.TryGetValue(current, out var next)) continue;

            foreach (var neighbour in next)
            {
                if (distance.ContainsKey(neighbour)) continue;
                distance[neighbour] = distance[current] + 1;
                frontier.Enqueue(neighbour);
            }
        }

        return distance;
    }

    /// <summary>Hops from origin to one sector, or null when there is no route at all.</summary>
    public static int? Between(LaneGraph graph, string from, string to) =>
        From(graph, from).TryGetValue(to, out var hops) ? hops : null;
}
