using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Topology;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// How many turns one legion is from everywhere (spec-ai-commander.md §ReachMap and the believed frontier).
///
/// Per **entity**, not per faction, because two legions do not measure the same map: a banner that
/// matches a ley lane's climate walks it at 800‰ of the cost, so the same road is nearer for one
/// army than another. That is also where fog reaches route planning — a legion that has not scouted
/// a ley lane's endpoints does not know the discount applies and over-prices the march.
///
/// Optimistic, deliberately: a lane the faction cannot see reads open, so a legion routes over a
/// bridge that is down and learns by arriving. A pessimistic planner would never explore.
/// </summary>
public static class ReachMap
{
    /// <summary>
    /// Turns from the legion's position to every sector it can reach. Somewhere unreachable is
    /// **absent** rather than reported as far away: "no route" and "a long way" are different
    /// answers, and a caller that conflates them will march at a sector it can never arrive at.
    /// </summary>
    public static IReadOnlyDictionary<string, int> For(IWorldView view, WorldEntity entity)
    {
        var turns = new Dictionary<string, int>(StringComparer.Ordinal);

        // Allowance is act money, never march money — a holder reaches nowhere even with 250
        // to spend. Gate on STANCE, not on budget: keying emptiness on `budget <= 0` would
        // silently light the map for garrisons the day the hold refill went nonzero
        // (spec-hold-allowance.md §Design 2). The budget backstop below stays for the
        // scout/dowse frame; asking a zero-budget stance would divide by zero, which is a
        // worse way to find out that a garrison is a garrison.
        if (string.Equals(entity.Stance, MovementPolicy.Hold, StringComparison.Ordinal))
            return turns;
        var budget = MovementPolicy.BudgetFor(entity.Stance);
        if (budget <= 0) return turns;

        // Mid-stride, a legion's next arrival is the sector it is walking toward: routing from where
        // it set off would offer it a road it has already left.
        var origin = entity.AtSectorId ?? entity.OnLaneTowardSectorId;
        if (origin is null) return turns;

        var graph = MarchGraph.Of(view, include: null, bannerElement: BannerElement.Of(entity));
        if (!graph.Contains(origin)) return turns;

        foreach (var (sectorId, cost) in Dijkstra(graph, origin))
            turns[sectorId] = (cost + budget - 1) / budget;   // ceil: an arrival part-way through a turn is next turn

        return turns;
    }

    /// <summary>Cheapest march cost to every reachable sector, in per-mille movement points.</summary>
    static IReadOnlyDictionary<string, int> Dijkstra(LaneGraph graph, string origin)
    {
        var best = new Dictionary<string, int>(StringComparer.Ordinal) { [origin] = 0 };
        var settled = new HashSet<string>(StringComparer.Ordinal);

        var outgoing = new Dictionary<string, List<LaneStepEdge>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!outgoing.TryGetValue(edge.FromSectorId, out var list))
                outgoing[edge.FromSectorId] = list = new List<LaneStepEdge>();
            list.Add(edge);
        }

        while (true)
        {
            // No priority queue: at six sectors the scan is free, and ties break by ordinal id so two
            // equally cheap routes always settle in the same order. A heap would need the same
            // tie-break written explicitly or a replay could disagree with itself.
            string? next = null;
            var cheapest = int.MaxValue;
            foreach (var (sectorId, cost) in best.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (settled.Contains(sectorId) || cost >= cheapest) continue;
                next = sectorId;
                cheapest = cost;
            }

            if (next is null) break;
            settled.Add(next);

            if (!outgoing.TryGetValue(next, out var edges)) continue;

            foreach (var edge in edges.OrderBy(e => e.ToSectorId, StringComparer.Ordinal))
            {
                var through = cheapest + edge.Cost;
                if (best.TryGetValue(edge.ToSectorId, out var known) && known <= through) continue;
                best[edge.ToSectorId] = through;
            }
        }

        return best;
    }
}
