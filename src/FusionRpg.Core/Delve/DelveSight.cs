using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;

namespace FusionRpg.Core.Delve;

/// <summary>
/// The per-party sight overlay on a delve world (spec-delve-scope.md §6) — pure, no store, no
/// clock. <see cref="Visibility.SeenBy"/> is reused unchanged for the faction floor (owned sectors
/// Full, every acting entity's zone of control); this overlay adds the party's OWN radius —
/// <c>sight.lanes + bands.sightBand.{room's own sightBand}.extraLanes</c> — so a room's archetype
/// (dim/lit/scouting) decides how far that specific room reveals, never the faction-wide constant
/// alone (ideal §11.1: a `dim` room must not be lit by a blanket radius).
///
/// <c>roomSightBand</c> is the fact <c>delve-graph-roll</c> exposes per room (its own room
/// archetype's `sightBand`, not yet built as of this module — see that module's own task).
/// Supplying it as a lookup here, rather than reading a concrete store shape, is what keeps this
/// overlay a pure function testable before that module exists.
/// </summary>
public static class DelveSight
{
    public static IReadOnlyDictionary<string, SectorSight> ForParty(
        WorldState world, string partyEntityId, string factionId,
        Func<string /* sectorId */, string /* sightBand: dim|lit|scouting */> roomSightBand,
        int sightLanes, int scoutLanes, Func<string /* sightBand */, int /* extraLanes */> extraLanesFor,
        bool scouted = false)
    {
        var floor = Visibility.SeenBy(world, factionId);
        var overlay = new SortedDictionary<string, SectorSight>(StringComparer.Ordinal);
        foreach (var (sectorId, sight) in floor) overlay[sectorId] = sight;

        var party = world.Entities.FirstOrDefault(e => e.EntityId == partyEntityId);
        if (party?.AtSectorId is not { } here) return overlay; // mid-lane or unknown party sees only the faction floor

        Raise(overlay, here, SectorSight.Full, floor);

        var band = roomSightBand(here);
        var radius = (scouted ? scoutLanes : sightLanes) + extraLanesFor(band);
        if (radius <= 0) return overlay;

        var neighbours = Neighbours(world);
        var frontier = new HashSet<string> { here };
        var visited = new HashSet<string> { here };
        for (var step = 0; step < radius; step++)
        {
            var next = new HashSet<string>();
            foreach (var sectorId in frontier)
            {
                if (!neighbours.TryGetValue(sectorId, out var adjacent)) continue;
                foreach (var n in adjacent)
                {
                    if (!visited.Add(n)) continue;
                    Raise(overlay, n, SectorSight.Glimpse, floor);
                    next.Add(n);
                }
            }
            frontier = next;
        }

        return overlay;
    }

    /// <summary>Never raises above what the faction floor already grants — the overlay only adds
    /// what the party's own presence contributes, it cannot see less than the faction does.</summary>
    static void Raise(IDictionary<string, SectorSight> overlay, string sectorId, SectorSight level,
        IReadOnlyDictionary<string, SectorSight> floor)
    {
        if (!overlay.TryGetValue(sectorId, out var current)) return; // not a real sector in this world
        var floorLevel = floor.TryGetValue(sectorId, out var f) ? f : SectorSight.None;
        var best = Max(Max(current, level), floorLevel);
        overlay[sectorId] = best;
    }

    static SectorSight Max(SectorSight a, SectorSight b) => (SectorSight)Math.Max((int)a, (int)b);

    static Dictionary<string, List<string>> Neighbours(WorldState world)
    {
        var neighbours = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in world.Sectors) neighbours[s.SectorId] = new List<string>();
        foreach (var l in world.Lanes)
        {
            neighbours[l.FromSectorId].Add(l.ToSectorId);
            neighbours[l.ToSectorId].Add(l.FromSectorId);
        }
        return neighbours;
    }
}
