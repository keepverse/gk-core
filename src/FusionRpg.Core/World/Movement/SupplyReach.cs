namespace FusionRpg.Core.World.Movement;

/// <summary>
/// The chain home, as a traversal (spec-ai-commander.md §Believed supply).
///
/// The *rule* is one rule — start at every Seat you hold, walk supply-carrying lanes, never cross
/// ground held against you. The *inputs* are two, and they differ on purpose: the engine asks it of
/// the truth, and a faction policy asks it of what that faction believes, which is optimistic and
/// sometimes wrong. Copying the walk instead would leave two versions of one rule that have to be
/// kept identical while their inputs deliberately diverge, which is the version that rots.
/// </summary>
public static class SupplyReach
{
    /// <summary>One lane, as far as supply is concerned.</summary>
    public readonly record struct Link(string FromSectorId, string ToSectorId, bool OneWay);

    /// <summary>
    /// Every sector reachable from <paramref name="seeds"/> across <paramref name="links"/>, never
    /// leaving ground <paramref name="usable"/> rejects.
    ///
    /// Breadth-first in stable id order. The set it produces would be the same in any order; the
    /// order it is *built* in would not, and a replay cannot survive that.
    /// </summary>
    public static IReadOnlySet<string> From(
        IEnumerable<string> seeds, IReadOnlyList<Link> links, Func<string, bool> usable)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        foreach (var seed in seeds.OrderBy(id => id, StringComparer.Ordinal))
            if (usable(seed) && reached.Add(seed))
                frontier.Enqueue(seed);

        if (reached.Count == 0) return reached;

        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            Add(outgoing, link.FromSectorId, link.ToSectorId);

            // A temporal current only carries supply the way it flows.
            if (!link.OneWay) Add(outgoing, link.ToSectorId, link.FromSectorId);
        }

        foreach (var list in outgoing.Values) list.Sort(StringComparer.Ordinal);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (!outgoing.TryGetValue(current, out var next)) continue;

            foreach (var neighbour in next)
                if (usable(neighbour) && reached.Add(neighbour))
                    frontier.Enqueue(neighbour);
        }

        return reached;
    }

    static void Add(Dictionary<string, List<string>> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var list)) map[from] = list = new List<string>();
        list.Add(to);
    }

    /// <summary>The lanes that can carry supply at all, from any list of them.</summary>
    public static IReadOnlyList<Link> LinksOf(IEnumerable<WorldLane> lanes)
    {
        var links = new List<Link>();

        foreach (var lane in lanes.OrderBy(l => l.LaneId, StringComparer.Ordinal))
        {
            if (lane.State != LaneState.Open) continue;

            var type = LaneTypeCatalog.Get(lane.TypeId);
            if (!type.CarriesSupply) continue;

            // A gate you have no key to stops a supply column exactly as it stops an army. Before
            // this, a shut gate cut the topology and the lifeline overlay while leaving the chain
            // behind it fed, so an empire could be provisioned through a door nobody could open.
            // No shipped template authors a gated lane, so nothing observable changes today.
            if (type.Gated && lane.GateKeyId != null) continue;

            links.Add(new Link(lane.FromSectorId, lane.ToSectorId, type.OneWay));
        }

        return links;
    }
}
