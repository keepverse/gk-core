namespace FusionRpg.Core.World.Movement;

/// <summary>Two hostile forces sharing a sector once the dust of movement has settled.</summary>
public readonly record struct SectorContact(
    string SectorId, string AttackerEntityId, string DefenderEntityId, bool DefenderStationary);

/// <summary>Two hostile forces closing on one lane, and the exact instant they meet.</summary>
public readonly record struct LaneContact(
    string LaneId, string EntityA, string EntityB, int TimeMilli, int PositionMilliFromA);

/// <summary>
/// Who ends up fighting whom (spec-world-movement.md §Contact). This module decides that a fight
/// happens; <see cref="Turn.IBattleResolver"/> decides how it goes.
///
/// Two rules keep it bounded and replayable: everything is walked in ordinal entity-id order, and
/// **one battle per place per turn**. Resolving a second fight in the same sector would need the
/// first one's result to feed the next pairing, and that ordering is exactly the kind of thing a
/// replay cannot be trusted to reproduce.
/// </summary>
public static class ContactResolver
{
    /// <summary>
    /// Hostile pairs standing in the same sector. <paramref name="movedEntityIds"/> is who changed
    /// position this turn — a force that did not is the defender, and the only side that gets to
    /// count the ground as an advantage.
    /// </summary>
    public static IReadOnlyList<SectorContact> SectorContacts(
        WorldState world, IReadOnlySet<string> movedEntityIds)
    {
        var contacts = new List<SectorContact>();

        var present = world.Entities
            .Where(e => e.AtSectorId != null && ZoneOfControl.Projects(e))
            .OrderBy(e => e.EntityId, StringComparer.Ordinal)
            .ToList();

        foreach (var sector in world.Sectors)
        {
            var here = present
                .Where(e => string.Equals(e.AtSectorId, sector.SectorId, StringComparison.Ordinal))
                .ToList();
            if (here.Count < 2) continue;

            var (a, b) = FirstHostilePair(here);
            if (a is null || b is null) continue;

            var aMoved = movedEntityIds.Contains(a.EntityId);
            var bMoved = movedEntityIds.Contains(b.EntityId);

            // Exactly one of them held the ground: that one defends. Both moving (or both parked)
            // means nobody was defending anything.
            var (attacker, defender, stationary) = aMoved switch
            {
                true when !bMoved => (a, b, true),
                false when bMoved => (b, a, true),
                _ => (a, b, false)
            };

            contacts.Add(new SectorContact(sector.SectorId, attacker.EntityId, defender.EntityId, stationary));
        }

        return contacts;
    }

    /// <summary>
    /// Hostile forces closing head-on along one lane, with the meeting point solved arithmetically
    /// so it never depends on which of them the caller happened to process first.
    ///
    /// <paramref name="steps"/> is this turn's lane occupancy, entity id to step.
    /// </summary>
    public static IReadOnlyList<LaneContact> LaneContacts(
        WorldState world, IReadOnlyDictionary<string, LaneStep> steps)
    {
        var contacts = new List<LaneContact>();
        var owners = world.Entities.ToDictionary(e => e.EntityId, e => e.OwnerFactionId, StringComparer.Ordinal);

        foreach (var lane in world.Lanes)
        {
            var onThisLane = steps
                .Where(kv => string.Equals(kv.Value.LaneId, lane.LaneId, StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            if (onThisLane.Count < 2) continue;

            LaneContact? earliest = null;
            for (var i = 0; i < onThisLane.Count; i++)
            for (var j = i + 1; j < onThisLane.Count; j++)
            {
                var (idA, stepA) = onThisLane[i];
                var (idB, stepB) = onThisLane[j];

                if (!owners.TryGetValue(idA, out var factionA) || !owners.TryGetValue(idB, out var factionB))
                    continue;
                if (!ZoneOfControl.IsHostile(factionA, factionB)) continue;

                // Same heading means one is chasing the other, not meeting it.
                if (string.Equals(stepA.TowardSectorId, stepB.TowardSectorId, StringComparison.Ordinal))
                    continue;

                if (!LaneCrossing.TryFind(
                        stepA.ProgressMilli, stepA.SpeedMilli, stepB.ProgressMilli, stepB.SpeedMilli,
                        out var timeMilli, out var positionMilli))
                    continue;

                var candidate = new LaneContact(lane.LaneId, idA, idB, timeMilli, positionMilli);
                if (earliest is null || candidate.TimeMilli < earliest.Value.TimeMilli)
                    earliest = candidate;
            }

            if (earliest is { } found) contacts.Add(found);
        }

        return contacts;
    }

    static (WorldEntity? A, WorldEntity? B) FirstHostilePair(IReadOnlyList<WorldEntity> here)
    {
        for (var i = 0; i < here.Count; i++)
        for (var j = i + 1; j < here.Count; j++)
            if (ZoneOfControl.IsHostile(here[i].OwnerFactionId, here[j].OwnerFactionId))
                return (here[i], here[j]);

        return (null, null);
    }
}
