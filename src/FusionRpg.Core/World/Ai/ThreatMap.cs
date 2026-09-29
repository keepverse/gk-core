using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;

namespace FusionRpg.Core.World.Ai;

/// <summary>Which way to be wrong about a force you could not count.</summary>
public enum ThreatReading
{
    /// <summary>Assume the worst — the band's ceiling. For deciding whether to defend.</summary>
    Defensive,

    /// <summary>Assume the likely — the band's midpoint. For deciding whether to attack.</summary>
    Offensive
}

/// <summary>
/// Fear, spread by ignorance (spec-ai-commander.md §ThreatMap).
///
/// A remembered enemy is not *there*; it is somewhere within however far it could have marched since
/// you looked. So a fresh sighting is a sharp local fear, a three-turn-old one is the same worry
/// smeared across everything within three lanes, and a seven-turn-old one is nothing at all —
/// because by then you genuinely do not know, and pretending otherwise is worse than admitting it.
///
/// Uncertainty therefore makes a commander defend more places, which is the right response to it,
/// and stale intel eventually stops mattering, which is what makes scouting worth its cost.
///
/// Integer per-mille throughout, no RNG, no floating point: the same belief always produces the same
/// fear, and a replay cannot drift.
/// </summary>
public static class ThreatMap
{
    /// <summary>How much confidence a turn of staleness costs. Seven turns is total amnesia.</summary>
    public static int StaleDecayPerTurn => WorldAiPolicy.Tuning.ThreatMap.StaleDecayPerTurn;

    /// <summary>
    /// How far a sighting is allowed to smear. Beyond four hops the fear is so diffuse it says
    /// nothing, and freshness has nearly run out by then anyway.
    /// </summary>
    public static int MaxSpreadHops => WorldAiPolicy.Tuning.ThreatMap.MaxSpreadHops;

    /// <summary>What each hop past the spread costs. Zero at three, which bounds the walk.</summary>
    public static int ProximityFalloffPerHop => WorldAiPolicy.Tuning.ThreatMap.ProximityFalloffPerHop;

    /// <summary>Where the falloff reaches zero — the radius worth visiting at all.</summary>
    public static int FalloffReach => 1000 / ProximityFalloffPerHop;   // 2

    public static IReadOnlyDictionary<string, long> For(IWorldView view, ThreatReading reading)
    {
        // Every sector gets an answer, including zero: a missing key and a zero read the same to a
        // person and differently to a caller.
        var threat = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var sectorId in view.SectorIds) threat[sectorId] = 0;

        // The **march** lens: an enemy across a deep rift carries no grain and is still two days
        // away. Building this on the supply graph would make it invisible.
        var graph = MarchGraph.Of(view);
        var hopsFrom = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);

        foreach (var sectorId in view.SectorIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (view.Believed(sectorId) is not { } believed) continue;

            var age = Math.Max(0, view.CurrentTurn - believed.LastSeenTurn);
            var freshness = Math.Max(0, 1000 - age * StaleDecayPerTurn);
            if (freshness == 0) continue;             // no longer worth acting on

            var spreadHops = Math.Min(age, MaxSpreadHops);

            foreach (var force in believed.Forces)
            {
                if (!ZoneOfControl.IsHostile(force.OwnerFactionId, view.FactionId)) continue;

                var strength = reading == ThreatReading.Defensive ? force.Defensive : force.Offensive;
                if (strength <= 0) continue;

                // A guard defends its slot and cannot come and find you, so its menace does not
                // travel. The same rule that makes marching past one free.
                var reach = ZoneOfControl.Projects(force.Kind) ? spreadHops + FalloffReach : 0;

                if (!hopsFrom.TryGetValue(sectorId, out var hops))
                    hopsFrom[sectorId] = hops = Hops.From(graph, sectorId);

                foreach (var (target, distance) in hops)
                {
                    if (distance > reach) continue;

                    var beyond = Math.Max(0, distance - spreadHops);
                    var proximity = Math.Max(0, 1000 - beyond * ProximityFalloffPerHop);
                    if (proximity == 0) continue;

                    threat[target] += strength * freshness / 1000 * proximity / 1000;
                }
            }
        }

        return threat;
    }
}
