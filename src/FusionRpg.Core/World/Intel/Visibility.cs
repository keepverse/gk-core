using FusionRpg.Core.World.Movement;

namespace FusionRpg.Core.World.Intel;

/// <summary>How much of a sector a faction can make out right now.</summary>
public enum SectorSight
{
    /// <summary>Nothing. Position and lanes only — the graph is public, its contents are not.</summary>
    None,

    /// <summary>Next door: who holds it, how dangerous it looks, and any force present as a band.</summary>
    Glimpse,

    /// <summary>Standing in it or holding it: everything, including what is buried in the ground.</summary>
    Full
}

/// <summary>
/// What a faction can see this turn (spec-world-intel.md §Seeing, now).
///
/// Presence plus one lane, doubled by the `scout` stance. Adjacency is a glimpse rather than a
/// survey, which is what keeps claiming a rich sector a gamble instead of a lookup.
///
/// Evaluated over the world at turn **start and end**, unioned. That single detail removes two
/// special cases: a legion that marches through a sector reports on it, and a faction driven out of
/// one remembers it as of this turn rather than as of whenever it last looked.
///
/// Pure, integer, ordinal-ordered — no storage, no clock, no rolls.
/// </summary>
public static class Visibility
{
    /// <summary>How far a force sees past the ground it holds.</summary>
    public const int SightLanes = 1;

    /// <summary>What the `scout` stance buys, at half a turn's march (see world-movement).</summary>
    public const int ScoutSightLanes = 2;

    public const string ScoutStance = "scout";

    public static IReadOnlyDictionary<string, SectorSight> SeenBy(WorldState world, string factionId) =>
        SeenBy(world, world, factionId);

    /// <param name="marchedThrough">
    /// Ground this faction set foot in during the turn without ending there. Neither world snapshot
    /// can show it — a route is not recoverable from a destination — so the movement phase reports
    /// it, and a legion that crossed a sector surveys it exactly as if it had stopped.
    /// </param>
    public static IReadOnlyDictionary<string, SectorSight> SeenBy(
        WorldState atStart, WorldState atEnd, string factionId, IReadOnlySet<string>? marchedThrough = null)
    {
        var sight = new SortedDictionary<string, SectorSight>(StringComparer.Ordinal);
        foreach (var sector in atEnd.Sectors) sight[sector.SectorId] = SectorSight.None;

        Accumulate(sight, atStart, factionId);
        Accumulate(sight, atEnd, factionId);

        if (marchedThrough is { Count: > 0 })
            AccumulateRoute(sight, atEnd, marchedThrough);

        return sight;
    }

    /// <summary>
    /// Ground crossed mid-turn is surveyed, and sight spreads one lane out from it — the same as
    /// standing there, because for a moment the legion was.
    /// </summary>
    static void AccumulateRoute(
        IDictionary<string, SectorSight> sight, WorldState world, IReadOnlySet<string> marchedThrough)
    {
        var neighbours = Neighbours(world);

        foreach (var sectorId in marchedThrough.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!sight.ContainsKey(sectorId)) continue;

            Raise(sight, sectorId, SectorSight.Full);
            if (!neighbours.TryGetValue(sectorId, out var adjacent)) continue;

            foreach (var neighbour in adjacent) Raise(sight, neighbour, SectorSight.Glimpse);
        }
    }

    static void Accumulate(IDictionary<string, SectorSight> sight, WorldState world, string factionId)
    {
        var neighbours = Neighbours(world);

        // Observation points: ground you hold or stand on, plus both ends of a lane you are on.
        // Each carries a radius — the best any single force there can manage.
        var posts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var sector in world.Sectors)
            if (string.Equals(sector.OwnerFactionId, factionId, StringComparison.Ordinal))
            {
                Raise(sight, sector.SectorId, SectorSight.Full);
                Post(posts, sector.SectorId, SightLanes);
            }

        foreach (var entity in world.Entities)
        {
            if (!string.Equals(entity.OwnerFactionId, factionId, StringComparison.Ordinal)) continue;

            // A guard defends its vein, not the ground — the same rule that makes marching past one
            // free. It watches nothing on the faction's behalf.
            if (!ZoneOfControl.Projects(entity)) continue;

            var radius = string.Equals(entity.Stance, ScoutStance, StringComparison.Ordinal)
                ? ScoutSightLanes
                : SightLanes;

            if (entity.AtSectorId is { } standing)
            {
                Raise(sight, standing, SectorSight.Full);
                Post(posts, standing, radius);
                continue;
            }

            if (entity.OnLaneId is not { } laneId) continue;

            // Mid-march: you can see the ground at either end of the road, but you are in neither.
            var lane = world.Lanes.FirstOrDefault(l => l.LaneId == laneId);
            if (lane is null) continue;

            foreach (var end in new[] { lane.FromSectorId, lane.ToSectorId })
            {
                Raise(sight, end, SectorSight.Glimpse);
                Post(posts, end, radius);
            }
        }

        // Sight spreads out from each post over open lanes, in ordinal order.
        foreach (var (origin, radius) in posts)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal) { origin };
            var frontier = new List<string> { origin };

            for (var step = 0; step < radius && frontier.Count > 0; step++)
            {
                var next = new List<string>();
                foreach (var current in frontier)
                {
                    if (!neighbours.TryGetValue(current, out var adjacent)) continue;
                    foreach (var neighbour in adjacent)
                    {
                        if (!reached.Add(neighbour)) continue;
                        Raise(sight, neighbour, SectorSight.Glimpse);
                        next.Add(neighbour);
                    }
                }

                next.Sort(StringComparer.Ordinal);
                frontier = next;
            }
        }
    }

    /// <summary>A survey is never demoted to a glimpse by looking at it from further away.</summary>
    static void Raise(IDictionary<string, SectorSight> sight, string sectorId, SectorSight level)
    {
        if (sight.TryGetValue(sectorId, out var current) && current >= level) return;
        sight[sectorId] = level;
    }

    static void Post(IDictionary<string, int> posts, string sectorId, int radius)
    {
        if (posts.TryGetValue(sectorId, out var current) && current >= radius) return;
        posts[sectorId] = radius;
    }

    static SortedDictionary<string, List<string>> Neighbours(WorldState world)
    {
        var map = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var sector in world.Sectors) map[sector.SectorId] = new List<string>();

        foreach (var lane in world.Lanes.OrderBy(l => l.LaneId, StringComparer.Ordinal))
        {
            // Sight follows the road, and a severed road carries nothing — not supply, not a look.
            if (lane.State != LaneState.Open) continue;
            if (!map.ContainsKey(lane.FromSectorId) || !map.ContainsKey(lane.ToSectorId)) continue;

            map[lane.FromSectorId].Add(lane.ToSectorId);
            map[lane.ToSectorId].Add(lane.FromSectorId);
        }

        foreach (var list in map.Values) list.Sort(StringComparer.Ordinal);
        return map;
    }
}
