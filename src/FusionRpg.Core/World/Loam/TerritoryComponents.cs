using FusionRpg.Core.World.Movement;

namespace FusionRpg.Core.World.Loam;

/// <summary>
/// Which blocks of a faction's territory can pay for each other (spec-loam-calc.md #2).
///
/// **Not <see cref="SupplyGraph.ConnectedSectors"/>.** That one seeds from Seats and asks "is this
/// in supply". This one has no seeds — every sector the faction owns is a candidate member — and
/// asks "which of my sectors are mutually reachable", because loam is fungible inside a connected
/// component and not across one. Same graph, same edge rule (<see cref="SupplyReach.LinksOf"/>),
/// genuinely different question — merging the two would be wrong for both.
/// </summary>
public static class TerritoryComponents
{
    /// <summary>The truth side: components of what a faction actually owns.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> For(WorldState world, string factionId)
    {
        var owned = world.Sectors
            .Where(s => string.Equals(s.OwnerFactionId, factionId, StringComparison.Ordinal))
            .Select(s => s.SectorId);

        return For(owned, SupplyReach.LinksOf(world.Lanes));
    }

    /// <summary>
    /// The belief side: a faction always knows its own holdings exactly — there is no fog over what
    /// you hold — so the only input that differs from truth is which lanes it believes are open.
    /// The lane graph itself is public knowledge (spec-world-topology.md assumption 1).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> For(
        IEnumerable<string> ownedSectorIds, IReadOnlyList<SupplyReach.Link> links)
    {
        var owned = new HashSet<string>(ownedSectorIds, StringComparer.Ordinal);

        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (!owned.Contains(link.FromSectorId) || !owned.Contains(link.ToSectorId)) continue;

            Add(adjacency, link.FromSectorId, link.ToSectorId);
            if (!link.OneWay) Add(adjacency, link.ToSectorId, link.FromSectorId);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<IReadOnlyList<string>>();

        // Ascending order means the first unvisited id encountered is always the lowest member of
        // its own component — no separate sort of the component list is needed afterward.
        foreach (var start in owned.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!visited.Add(start)) continue;

            var members = new List<string> { start };
            var frontier = new Queue<string>();
            frontier.Enqueue(start);

            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                if (!adjacency.TryGetValue(current, out var neighbours)) continue;

                foreach (var next in neighbours)
                    if (visited.Add(next))
                    {
                        members.Add(next);
                        frontier.Enqueue(next);
                    }
            }

            members.Sort(StringComparer.Ordinal);
            components.Add(members);
        }

        return components;
    }

    static void Add(Dictionary<string, List<string>> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var list)) map[from] = list = new List<string>();
        list.Add(to);
    }
}
