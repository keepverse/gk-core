namespace FusionRpg.Core.World.Turn;

/// <summary>
/// The world's half of the combat seam: turning an outcome back into map state. Kept apart from the
/// resolver on purpose — whatever fights the battle, only this file decides what a rout or a
/// wipeout means to the map, and there is exactly one of it.
/// </summary>
public static class BattleApplication
{
    public static WorldState Apply(
        WorldState world, BattleOutcome outcome,
        IReadOnlyDictionary<string, string>? arrivedViaLane = null)
    {
        if (outcome.Sides.Count == 0) return world;

        var sides = outcome.Sides.ToDictionary(s => s.EntityId, StringComparer.Ordinal);
        var entities = new List<WorldEntity>(world.Entities.Count);

        foreach (var entity in world.Entities)
        {
            if (!sides.TryGetValue(entity.EntityId, out var side))
            {
                entities.Add(entity);
                continue;
            }

            // Incoherent outcome: a resolver bug here would otherwise show up as a ghost army --
            // loud, at the seam, rather than silently picked one way.
            if (side.Withdrawn && side.Destroyed)
                throw new InvalidOperationException(
                    $"BattleApplication.Apply: side '{side.EntityId}' is both Withdrawn and Destroyed -- incoherent outcome.");

            // Destroyed forces leave the map entirely. Keeping an empty husk around would let it
            // hold ground, project zone of control, and block a claim with nothing in it.
            if (side.Destroyed) continue;

            // Withdrawn is NOT routed (audit F5) -- this line is the whole feature: a withdrawing
            // force keeps its orders. A newly-routed force that is NOT withdrawing still falls back.
            var newlyRouted = side.Routed && !entity.Routed && !side.Withdrawn;
            string? arrivalLane = null;
            arrivedViaLane?.TryGetValue(entity.EntityId, out arrivalLane);
            var placed = newlyRouted ? FallBack(entity, world.Lanes, arrivalLane) : entity;

            entities.Add(placed with
            {
                Members = side.Survivors,
                Routed = entity.Routed || (side.Routed && !side.Withdrawn)
            });
        }

        return world with { Entities = entities };
    }

    /// <summary>
    /// world-map: a routed force falls back rather than standing where the fight left it
    /// (spec-world-movement.md's own "`routed` — routed legions fall back and skip a turn's orders",
    /// specced but never wired — the owner confirmed directly, 2026-09-05, that it should be). Called
    /// only for a side <see cref="Apply"/> finds <b>newly</b> routed this fight (an already-routed
    /// force sits out its recovery turn where it already fell back to; the state itself blocks a
    /// second reversal even without that caller-side check, since a routed entity's next battle would
    /// have already cleared its lane fields below).
    ///
    /// Covers two shapes, unified by one formula (amended 2026-09-05 — the first cut only handled the
    /// first shape, silently treating the second exactly like a genuine long-standing garrison,
    /// which the owner confirmed was a real gap, not an intended scope line):
    ///
    /// <list type="bullet">
    /// <item>Mid-crossing: the entity still carries <see cref="WorldEntity.OnLaneId"/> and
    /// <see cref="WorldEntity.OnLaneTowardSectorId"/> — "the way it came" is simply the lane's other
    /// endpoint, no new stored state needed.</item>
    /// <item>Arrived this turn: a march that fully completed already cleared <c>OnLaneId</c>, so
    /// <paramref name="arrivedViaLaneId"/> (movement-phase-local, from <see cref="BattleReporting.Fight"/>'s
    /// own parameter — never a <see cref="WorldEntity"/> field, never hashed, since it cannot survive
    /// past the <see cref="Movement.MovementPhase"/> call that produced it) stands in for it instead.</item>
    /// </list>
    ///
    /// Either way the "destination" side of the lane is known (the sector it was heading toward, or
    /// the sector it now stands in) and the origin is simply the lane's *other* end.
    ///
    /// A force with neither — a genuine garrison losing a Contact or Siege fight on ground it already
    /// held at turn start, or a district's entrenched defender (<see cref="Battle.Board.SiegeTuningPolicy.Objective"/>'s
    /// own <c>DistrictDefenderBonusMilli</c>) — has nothing to fall back *from* and stays exactly
    /// where entrenched combat already left the map, the same distinction the deleted wave-1
    /// `PlaceholderBattleResolver` once drew between a legion caught marching and one dug in
    /// (`actor-hub-and-combat-power-solid-fixing` T20 — a fall-back Contact/Siege fight for a
    /// non-district kind is now a no-op outcome, `Sides.Count == 0`, so `FallBack` is never even
    /// called for it; only District's real engine still produces a routed side here).
    /// </summary>
    static WorldEntity FallBack(WorldEntity entity, IReadOnlyList<WorldLane> lanes, string? arrivedViaLaneId)
    {
        var laneId = entity.OnLaneId ?? arrivedViaLaneId;
        if (laneId is null) return entity;

        var lane = lanes.FirstOrDefault(l => string.Equals(l.LaneId, laneId, StringComparison.Ordinal));
        if (lane is null) return entity; // defensive: a vanished lane id falls through unmoved

        var destination = entity.OnLaneTowardSectorId ?? entity.AtSectorId;
        if (destination is null) return entity; // defensive: no destination end to retreat away from

        var origin = string.Equals(lane.ToSectorId, destination, StringComparison.Ordinal)
            ? lane.FromSectorId
            : lane.ToSectorId;

        return entity with
        {
            AtSectorId = origin,
            OnLaneId = null,
            OnLaneTowardSectorId = null,
            LaneProgressMilli = 0
        };
    }

    /// <summary>Flips one slot's guard. Only the slot named — a sector falls one fight at a time.</summary>
    public static WorldState ClearGuard(WorldState world, string sectorId, int slotIndex) => world with
    {
        Sectors = world.Sectors
            .Select(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal)
                ? s with
                {
                    Slots = s.Slots
                        .Select(sl => sl.SlotIndex == slotIndex
                            ? sl with { GuardState = GuardState.Cleared }
                            : sl)
                        .ToList()
                }
                : s)
            .ToList()
    };

    /// <summary>
    /// A district assault's third application step (spec-siege-seam.md §4) — empty for every
    /// existing battle kind, so every existing battle takes the identical path. Applies who ended the
    /// battle occupying a slot (possession is by occupation, decision 4 — buildings have no ownership)
    /// and, since base-defense `structure-state` (task 8.4), persists remaining structure HP and turns
    /// a destroyed structure into rubble: `SlotState.Ruined`, `StructureId`/`StructureHp`/
    /// `ConstructionTurnsRemaining` all cleared — `SlotState.Ruined`'s first reader, closing a wiring
    /// gap rather than adding a new enum. `district-layout` §5 already maps `Ruined` → `Rough` terrain,
    /// so rubble-you-can-cross-but-slowly falls out free.
    ///
    /// <para>base-defense `siege-construction`: a NEW structure this battle placed
    /// (<see cref="SlotOutcome.StructurePlaced"/>) wins over destruction — a slot cannot be simultaneously
    /// ruined and freshly built this same battle, and a placement is the more specific fact. Checked
    /// first, so a `StructurePlaced` result never has to also carry `StructureDestroyed: false` just to
    /// avoid being cleared by the branch below it.</para>
    /// </summary>
    public static WorldState ApplySlotResults(WorldState world, string sectorId, IReadOnlyList<SlotOutcome> slotResults)
    {
        if (slotResults.Count == 0) return world;

        var bySlot = slotResults.ToDictionary(r => r.SlotIndex);
        return world with
        {
            Sectors = world.Sectors
                .Select(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal)
                    ? s with
                    {
                        Slots = s.Slots
                            .Select(sl => bySlot.TryGetValue(sl.SlotIndex, out var result)
                                ? sl with
                                {
                                    OwnerFactionId = result.HeldByFactionId,
                                    State = result.StructurePlaced is not null
                                        ? SlotState.Intact
                                        : result.StructureDestroyed ? SlotState.Ruined : sl.State,
                                    StructureId = result.StructurePlaced
                                        ?? (result.StructureDestroyed ? null : sl.StructureId),
                                    StructureHp = result.StructurePlaced is not null
                                        ? null // freshly placed -- full HP, the same "null means undamaged" default every other new structure gets
                                        : result.StructureDestroyed ? null : result.StructureHp,
                                    ConstructionTurnsRemaining = result.StructurePlaced is not null
                                        ? result.PlacedConstructionTurnsRemaining
                                        : result.StructureDestroyed ? null : sl.ConstructionTurnsRemaining
                                }
                                : sl)
                            .ToList()
                    }
                    : s)
                .ToList()
        };
    }
}
