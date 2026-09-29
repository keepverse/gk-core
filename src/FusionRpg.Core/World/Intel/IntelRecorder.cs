using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Intel;

/// <summary>
/// Turning sight into memory (spec-world-intel.md §Remembering).
///
/// Pure: the world at turn start and end, plus what each faction already believed, in — the new
/// belief out. Everything a faction can see is re-snapshotted at this turn; everything it cannot
/// keeps the snapshot it had, and ages.
///
/// A destroyed force is forgotten rather than remembered as a ghost, and that falls out rather than
/// being special-cased: a visible sector's forces are rebuilt from the truth, so anything that died
/// in front of you is simply absent from the new snapshot. What you could not see, you still believe.
/// </summary>
public static class IntelRecorder
{
    /// <param name="marchedThrough">
    /// Per faction, the ground it crossed this turn without ending there — from the movement phase,
    /// because a route cannot be read back out of a destination.
    /// </param>
    public static IReadOnlyList<FactionIntel> Observe(
        WorldState atStart, WorldState atEnd, int turn,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? marchedThrough = null)
    {
        var prior = atEnd.Intel.ToDictionary(f => f.FactionId, StringComparer.Ordinal);
        var result = new List<FactionIntel>(atEnd.Factions.Count);

        foreach (var faction in atEnd.Factions.OrderBy(f => f.FactionId, StringComparer.Ordinal))
        {
            var walked = marchedThrough != null
                         && marchedThrough.TryGetValue(faction.FactionId, out var route)
                ? route
                : null;

            var sight = Visibility.SeenBy(atStart, atEnd, faction.FactionId, walked);
            prior.TryGetValue(faction.FactionId, out var believed);

            var snapshots = new List<IntelSnapshot>();
            foreach (var sector in atEnd.Sectors)
            {
                var level = sight.TryGetValue(sector.SectorId, out var s) ? s : SectorSight.None;

                if (level == SectorSight.None)
                {
                    // Out of sight is not out of mind — the old snapshot stands and grows stale.
                    var remembered = believed?.Of(sector.SectorId);
                    if (remembered != null) snapshots.Add(remembered);
                    continue;
                }

                snapshots.Add(Merge(believed?.Of(sector.SectorId), Snapshot(atEnd, sector, level, turn)));
            }

            result.Add(new FactionIntel
            {
                FactionId = faction.FactionId,
                Sectors = snapshots.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList()
            });
        }

        return result;
    }

    /// <summary>
    /// A fresh sighting, without throwing away what a better one already taught you.
    ///
    /// Found by playing the game: a legion surveyed a sector, stepped one lane away, and forgot what
    /// was in it — because a glimpse produced a whole new snapshot carrying no slots. Surveying was
    /// nearly worthless. Everything a glimpse *can* see (who holds it, what is standing on it, when
    /// you last looked) takes the new value; what only a survey can see is kept from the survey.
    ///
    /// The nuance worth knowing: after this, `LastSeenTurn` is when you last *looked*, which is not
    /// necessarily when you last learned what is inside. That is the honest trade — the alternative
    /// is either forgetting the inside or pretending you have not seen the outside since.
    /// </summary>
    static IntelSnapshot Merge(IntelSnapshot? remembered, IntelSnapshot fresh)
    {
        if (remembered is null || remembered.Detail <= fresh.Detail) return fresh;

        return fresh with
        {
            Detail = remembered.Detail,
            Slots = remembered.Slots,
            DevelopmentLevel = remembered.DevelopmentLevel,
            RecruitStock = remembered.RecruitStock,
            ProjectId = remembered.ProjectId,
            ProjectTurnsRemaining = remembered.ProjectTurnsRemaining
        };
    }

    static IntelSnapshot Snapshot(WorldState world, WorldSector sector, SectorSight level, int turn) => new()
    {
        SectorId = sector.SectorId,
        LastSeenTurn = turn,
        Detail = level,
        OwnerFactionId = sector.OwnerFactionId,
        Phase = sector.Phase,
        Climate = sector.Climate,
        DangerBand = sector.DangerBand,
        FractureIntensityMilli = sector.FractureIntensityMilli,
        DevelopmentLevel = level == SectorSight.Full ? sector.DevelopmentLevel : 0,
        RecruitStock = level == SectorSight.Full ? sector.RecruitStock : 0,
        ProjectId = level == SectorSight.Full ? sector.ProjectId : null,
        ProjectTurnsRemaining = level == SectorSight.Full ? sector.ProjectTurnsRemaining : null,

        // A glimpse from next door tells you who holds the ground, not what is buried in it. That is
        // what keeps claiming a rich sector a gamble rather than a lookup.
        Slots = level == SectorSight.Full
            ? sector.Slots
                .OrderBy(sl => sl.SlotIndex)
                .Select(sl => new RememberedSlot
                {
                    SlotIndex = sl.SlotIndex,
                    SlotTypeId = sl.SlotTypeId,
                    Element = sl.Element,
                    GuardWaveId = sl.GuardWaveId,
                    State = sl.State,
                    GuardState = sl.GuardState,
                    StructureId = sl.StructureId,
                    ConstructionTurnsRemaining = sl.ConstructionTurnsRemaining
                })
                .ToList()
            : Array.Empty<RememberedSlot>(),

        Forces = ForcesAt(world, sector.SectorId, level).ToList()
    };

    static IEnumerable<RememberedForce> ForcesAt(WorldState world, string sectorId, SectorSight level)
    {
        var forces = new List<RememberedForce>();

        foreach (var entity in world.Entities)
        {
            var standing = string.Equals(entity.AtSectorId, sectorId, StringComparison.Ordinal);

            // A force on the road is recorded against the ground it is walking toward: "something is
            // coming" is the single most useful thing an observer reports, and a fog that hides an
            // army until it arrives removes the only tension worth having.
            var arriving = entity.AtSectorId is null
                           && string.Equals(entity.OnLaneTowardSectorId, sectorId, StringComparison.Ordinal);

            if (!standing && !arriving) continue;

            var strength = ForceStrength.Of(entity);

            forces.Add(new RememberedForce
            {
                EntityId = entity.EntityId,
                OwnerFactionId = entity.OwnerFactionId,
                Kind = entity.Kind,
                // Only a survey counts heads. Everything else is a band, including anything in transit
                // — you can see a column on the road, not read its muster roll.
                Exact = level == SectorSight.Full && standing,
                Strength = level == SectorSight.Full && standing ? strength : 0,
                BandIndex = StrengthBandCatalog.Of(strength).Index
            });
        }

        return forces.OrderBy(f => f.EntityId, StringComparer.Ordinal);
    }
}

/// <summary>
/// Prospecting (spec-loam-texture.md): a dowser stance reveals which sectors hold a loam source — a
/// rootbed, or an active well/waystation, exactly <see cref="Habitability"/>'s own rule for it — at a
/// reach beyond ordinary scouting, without leaking anything else a plain scout would not already
/// know (no owner, no danger band, no forces). Answers *where the prizes are*, an intel question, so
/// it lives here as its own query rather than folded into <see cref="IntelRecorder.Observe"/>'s
/// persisted snapshot: nothing here touches <c>FactionIntel</c> or <c>WorldCanonical</c>, the same
/// loam-pure-and-unwired-from-belief boundary this program has held since wave 1.
/// </summary>
public static class Prospecting
{
    /// <summary>
    /// Reach beyond <see cref="Visibility.ScoutSightLanes"/> — a dowser answers a much narrower
    /// question (is there a source here) at a wider range than an ordinary scout answers a broader one.
    /// </summary>
    public const int DowserSightLanes = 4;

    public const string DowserStance = "dowse";

    /// <summary>Every sector a faction's dowsers have confirmed holds a loam source this turn.</summary>
    public static IReadOnlySet<string> Reveal(WorldState world, string factionId)
    {
        var neighbours = Neighbours(world);
        var reached = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in world.Entities)
        {
            if (!string.Equals(entity.OwnerFactionId, factionId, StringComparison.Ordinal)) continue;
            if (!string.Equals(entity.Stance, DowserStance, StringComparison.Ordinal)) continue;
            if (entity.AtSectorId is not { } origin) continue;

            reached.Add(origin);
            var frontier = new List<string> { origin };
            for (var step = 0; step < DowserSightLanes && frontier.Count > 0; step++)
            {
                var next = new List<string>();
                foreach (var current in frontier)
                {
                    if (!neighbours.TryGetValue(current, out var adjacent)) continue;
                    foreach (var neighbour in adjacent)
                        if (reached.Add(neighbour)) next.Add(neighbour);
                }

                frontier = next;
            }
        }

        return world.Sectors
            .Where(s => reached.Contains(s.SectorId) && Habitability.For(s))
            .Select(s => s.SectorId)
            .ToHashSet(StringComparer.Ordinal);
    }

    static Dictionary<string, List<string>> Neighbours(WorldState world)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var sector in world.Sectors) map[sector.SectorId] = new List<string>();

        foreach (var lane in world.Lanes)
        {
            if (lane.State != LaneState.Open) continue;
            if (!map.ContainsKey(lane.FromSectorId) || !map.ContainsKey(lane.ToSectorId)) continue;

            map[lane.FromSectorId].Add(lane.ToSectorId);
            map[lane.ToSectorId].Add(lane.FromSectorId);
        }

        return map;
    }
}
