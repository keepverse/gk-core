using FusionRpg.Core.Delve;

namespace FusionRpg.Core.World;

/// <summary>
/// Which rules apply and which catalogs answer rules 1 and 6 (party-dungeon delve-scope,
/// 2026-09-05). <see cref="Map"/> is today's behaviour exactly — every lookup is the same
/// <see cref="SectorTypeCatalog"/>/<see cref="LaneTypeCatalog"/> call <see cref="WorldValidation"/>
/// always made. <see cref="Delve"/> swaps in a room/door catalog pair
/// (<see cref="RoomTypeCatalog"/>/<see cref="DoorTypeCatalog"/>, not served on
/// <c>/api/world/catalog</c>) and skips rules 4/5/11/13, none of which mean anything for a room
/// graph with no Home, no Seat and no map size tier.
/// </summary>
public sealed record WorldValidationProfile(
    string Name,
    Func<string?, bool> SectorTypeKnown, Func<string, SectorTypeDef> SectorType,
    Func<string?, bool> LaneTypeKnown,
    bool RequireHomeworld /* rules 4, 11 */, bool RequireSeatCounts /* rule 5 */, bool RequireTemplateSize /* rule 13 */)
{
    public static readonly WorldValidationProfile Map = new(
        "map", SectorTypeCatalog.IsKnown, SectorTypeCatalog.Get, LaneTypeCatalog.IsKnown,
        RequireHomeworld: true, RequireSeatCounts: true, RequireTemplateSize: true);

    public static WorldValidationProfile Delve(RoomTypeCatalog rooms, DoorTypeCatalog doors) => new(
        "delve", rooms.IsKnown, rooms.Get, doors.IsKnown,
        RequireHomeworld: false, RequireSeatCounts: false, RequireTemplateSize: false);
}

/// <summary>
/// The sixteen creation rules (spec-world-model.md §Creation and validation; 15-16 added
/// spec-sector-development.md W44). A malformed world must never reach the turn engine, so this
/// throws loudly at the gate rather than returning a flag.
/// </summary>
public static class WorldValidation
{
    /// <summary>
    /// Three times baseline (spec-loam-model.md, decided 2026-08-23). Past this, intensity would
    /// dominate every other term in the upkeep formula and the other inputs stop being able to
    /// matter — a multiplier that can drown its own operands is not a gradient, it is a switch.
    /// </summary>
    public const int MaxIntensityMilli = 3000;

    /// <summary>Zero would make a faction invulnerable to upkeep entirely (spec-loam-model.md rule 5).</summary>
    public const int MinHandicapMilli = 1;

    /// <summary>Same ceiling reasoning as <see cref="MaxIntensityMilli"/> — generous until `loam-calc`'s harness has an opinion.</summary>
    public const int MaxHandicapMilli = 3000;

    public static WorldState Validate(WorldState world) => Validate(world, WorldValidationProfile.Map);

    /// <summary>
    /// party-dungeon delve-scope (2026-09-05, decisions.md:133): a delve world validates under
    /// <see cref="WorldValidationProfile.Delve"/>, which skips rules 4/5/11/13 (a delve has no
    /// Home, no Seat, no capital, and its TemplateId is a layout id, not a map size tier) and
    /// reads rules 1/6's catalog lookups through the profile instead of the hard-coded map
    /// catalogs — a room kind is not a <see cref="SectorTypeCatalog"/> row and must not become
    /// one. <see cref="WorldValidationProfile.Map"/> is byte-for-byte today's behaviour.
    /// </summary>
    public static WorldState Validate(WorldState world, WorldValidationProfile profile)
    {
        RequireStableOrder(world);
        Rule1CatalogIdsExist(world, profile);
        Rule2LaneShape(world);
        Rule3Connected(world);
        if (profile.RequireHomeworld) Rule4Homeworld(world);
        if (profile.RequireSeatCounts) Rule5SeatCounts(world);
        Rule6SlotShape(world, profile);
        Rule7EntityPlacement(world);
        Rule8BeliefReferences(world);
        Rule9FractureIntensityBounded(world);
        Rule10LoamStockNonNegative(world);
        if (profile.RequireHomeworld) Rule11HomeworldHasARootbed(world);
        Rule12HandicapBounded(world);
        if (profile.RequireTemplateSize) Rule13TemplateSizeMatchesItsDeclaredTier(world);
        Rule14StructureSlotKindMatches(world);
        Rule15RecruitStockNonNegative(world);
        Rule16ProjectTurnsRemainingNeedsAProjectId(world);
        return world;
    }

    /// <summary>Ordering is load-bearing for hashing and replay, so it is validated, not assumed.</summary>
    static void RequireStableOrder(WorldState w)
    {
        RequireSorted(w.Factions.Select(f => f.FactionId), "factions");
        RequireSorted(w.Sectors.Select(s => s.SectorId), "sectors");
        RequireSorted(w.Lanes.Select(l => l.LaneId), "lanes");
        RequireSorted(w.Entities.Select(e => e.EntityId), "entities");
    }

    static void RequireOwner(string? ownerFactionId, HashSet<string> factionIds, string what)
    {
        if (ownerFactionId != null && !factionIds.Contains(ownerFactionId))
            throw new InvalidOperationException($"{what} is owned by unknown faction '{ownerFactionId}'.");
    }

    static void RequireSorted(IEnumerable<string> ids, string what)
    {
        string? previous = null;
        foreach (var id in ids)
        {
            if (previous != null && string.CompareOrdinal(previous, id) >= 0)
                throw new InvalidOperationException($"World {what} must be in stable ascending id order ('{previous}' before '{id}').");
            previous = id;
        }
    }

    static void Rule1CatalogIdsExist(WorldState w, WorldValidationProfile profile)
    {
        var factionIds = w.Factions.Select(f => f.FactionId).ToHashSet(StringComparer.Ordinal);

        foreach (var f in w.Factions)
        {
            WorldIds.RequireKebab(f.FactionId, "Faction id");

            // A policy id is a catalog reference like a sector or lane type, and it is inside the
            // state hash. Left unchecked, a typo would look exactly like "somebody is playing this
            // faction" — so the faction would simply never act, for the whole campaign, silently.
            if (f.PolicyId != null && !Ai.FactionPolicies.IsKnown(f.PolicyId))
                throw new InvalidOperationException(
                    $"Faction '{f.FactionId}' names unknown policy '{f.PolicyId}'.");
        }

        // Ownership is a reference, so it is checked like one — a sector owned by a faction that
        // does not exist would otherwise validate and then break every ownership read downstream.
        foreach (var s in w.Sectors)
        {
            RequireOwner(s.OwnerFactionId, factionIds, $"Sector '{s.SectorId}'");
            foreach (var slot in s.Slots)
                RequireOwner(slot.OwnerFactionId, factionIds, $"Sector '{s.SectorId}' slot {slot.SlotIndex}");
        }

        foreach (var e in w.Entities)
            if (!factionIds.Contains(e.OwnerFactionId))
                throw new InvalidOperationException($"Entity '{e.EntityId}' belongs to unknown faction '{e.OwnerFactionId}'.");

        foreach (var s in w.Sectors)
        {
            if (!profile.SectorTypeKnown(s.TypeId))
                throw new InvalidOperationException($"Sector '{s.SectorId}' has unknown sector type '{s.TypeId}'.");
            foreach (var slot in s.Slots)
                if (!SlotTypeCatalog.IsKnown(slot.SlotTypeId))
                    throw new InvalidOperationException($"Sector '{s.SectorId}' slot {slot.SlotIndex} has unknown slot type '{slot.SlotTypeId}'.");
        }

        foreach (var l in w.Lanes)
            if (!profile.LaneTypeKnown(l.TypeId))
                throw new InvalidOperationException($"Lane '{l.LaneId}' has unknown lane type '{l.TypeId}'.");
    }

    static void Rule2LaneShape(WorldState w)
    {
        var sectorIds = w.Sectors.Select(s => s.SectorId).ToHashSet(StringComparer.Ordinal);
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var l in w.Lanes)
        {
            if (string.Equals(l.FromSectorId, l.ToSectorId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Lane '{l.LaneId}' joins sector '{l.FromSectorId}' to itself.");
            if (!sectorIds.Contains(l.FromSectorId))
                throw new InvalidOperationException($"Lane '{l.LaneId}' starts at unknown sector '{l.FromSectorId}'.");
            if (!sectorIds.Contains(l.ToSectorId))
                throw new InvalidOperationException($"Lane '{l.LaneId}' ends at unknown sector '{l.ToSectorId}'.");

            // Undirected identity: A→B and B→A are the same edge.
            var key = string.CompareOrdinal(l.FromSectorId, l.ToSectorId) <= 0
                ? l.FromSectorId + "|" + l.ToSectorId
                : l.ToSectorId + "|" + l.FromSectorId;
            if (!seenPairs.Add(key))
                throw new InvalidOperationException($"Duplicate lane between '{l.FromSectorId}' and '{l.ToSectorId}' ('{l.LaneId}').");
        }
    }

    static void Rule3Connected(WorldState w)
    {
        if (w.Sectors.Count == 0)
            throw new InvalidOperationException("A world needs at least one sector.");

        // Undirected reachability: a one-way current still means the places are neighbours.
        var neighbours = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in w.Sectors)
            neighbours[s.SectorId] = new List<string>();
        foreach (var l in w.Lanes)
        {
            neighbours[l.FromSectorId].Add(l.ToSectorId);
            neighbours[l.ToSectorId].Add(l.FromSectorId);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { w.Sectors[0].SectorId };
        var queue = new Queue<string>();
        queue.Enqueue(w.Sectors[0].SectorId);
        while (queue.Count > 0)
            foreach (var next in neighbours[queue.Dequeue()])
                if (seen.Add(next))
                    queue.Enqueue(next);

        foreach (var s in w.Sectors)
            if (!seen.Contains(s.SectorId))
                throw new InvalidOperationException($"Sector '{s.SectorId}' is unreachable — the world graph must be connected.");
    }

    static void Rule4Homeworld(WorldState w)
    {
        var homes = w.Sectors
            .Where(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home))
            .ToList();

        if (homes.Count != 1)
            throw new InvalidOperationException($"A world needs exactly one homeworld sector; found {homes.Count}.");

        var home = homes[0];
        var player = w.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)
            ?? throw new InvalidOperationException("A world needs a player faction to own the homeworld.");

        if (!string.Equals(home.OwnerFactionId, player.FactionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Homeworld '{home.SectorId}' must be owned by the player faction '{player.FactionId}'.");
        if (home.Slots.All(sl => sl.SlotTypeId != SlotTypeCatalog.SeatSlotTypeId))
            throw new InvalidOperationException($"Homeworld '{home.SectorId}' must hold a Seat slot.");
    }

    static void Rule5SeatCounts(WorldState w)
    {
        foreach (var s in w.Sectors)
        {
            var type = SectorTypeCatalog.Get(s.TypeId);
            var seats = s.Slots.Count(sl => sl.SlotTypeId == SlotTypeCatalog.SeatSlotTypeId);

            if (type.CanHostSeat && seats != 1)
                throw new InvalidOperationException($"Sector '{s.SectorId}' is base-capable and needs exactly one Seat slot; found {seats}.");
            if (!type.CanHostSeat && seats != 0)
                throw new InvalidOperationException($"Sector '{s.SectorId}' cannot host a base yet holds a Seat slot.");
        }
    }

    static void Rule6SlotShape(WorldState w, WorldValidationProfile profile)
    {
        foreach (var s in w.Sectors)
        {
            var type = profile.SectorType(s.TypeId);
            for (var i = 0; i < s.Slots.Count; i++)
                if (s.Slots[i].SlotIndex != i)
                    throw new InvalidOperationException(
                        $"Sector '{s.SectorId}' slot indexes must be contiguous from 0 (index {i} is {s.Slots[i].SlotIndex}).");

            foreach (var slot in s.Slots)
            {
                if (!type.AllowedSlotTypes.Contains(slot.SlotTypeId, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Sector '{s.SectorId}' (type '{type.TypeId}') does not allow slot type '{slot.SlotTypeId}'.");

                // An intact guard with no encounter can never be cleared, and a sector is claimable
                // only when every slot is cleared — so this would soft-lock the map while validating
                // perfectly. A *cleared* guard may keep its id: it is the record of what was here.
                if (slot.GuardState == GuardState.Intact && string.IsNullOrEmpty(slot.GuardWaveId))
                    throw new InvalidOperationException(
                        $"Sector '{s.SectorId}' slot {slot.SlotIndex} has an intact guard with no encounter id — it could never be cleared.");
            }
        }
    }

    /// <summary>
    /// Belief is the one stored thing that is not derivable from the rest of the world, so it is
    /// also the one thing that can quietly reference something that no longer exists. A snapshot of
    /// a sector that was deleted, or held by a faction that was never created, would validate all
    /// the way to a projection and then fail somewhere far away from the cause.
    /// </summary>
    static void Rule8BeliefReferences(WorldState w)
    {
        var factionIds = w.Factions.Select(f => f.FactionId).ToHashSet(StringComparer.Ordinal);
        var sectorIds = w.Sectors.Select(s => s.SectorId).ToHashSet(StringComparer.Ordinal);

        RequireSorted(w.Intel.Select(i => i.FactionId), "faction intel");

        foreach (var intel in w.Intel)
        {
            if (!factionIds.Contains(intel.FactionId))
                throw new InvalidOperationException($"Intel belongs to unknown faction '{intel.FactionId}'.");

            RequireSorted(intel.Sectors.Select(s => s.SectorId), $"intel for '{intel.FactionId}'");

            foreach (var snapshot in intel.Sectors)
            {
                if (!sectorIds.Contains(snapshot.SectorId))
                    throw new InvalidOperationException(
                        $"Faction '{intel.FactionId}' remembers unknown sector '{snapshot.SectorId}'.");

                if (snapshot.LastSeenTurn < 0)
                    throw new InvalidOperationException(
                        $"Faction '{intel.FactionId}' remembers '{snapshot.SectorId}' from turn {snapshot.LastSeenTurn}.");

                // A glimpse cannot carry slot detail — if it does, the fog is leaking at the source
                // rather than at the projection, which is a far harder leak to find.
                if (snapshot.Detail != Intel.SectorSight.Full && snapshot.Slots.Count > 0)
                    throw new InvalidOperationException(
                        $"Faction '{intel.FactionId}' has slot detail for '{snapshot.SectorId}' from a glimpse.");
            }
        }
    }

    static void Rule7EntityPlacement(WorldState w)
    {
        var sectorIds = w.Sectors.Select(s => s.SectorId).ToHashSet(StringComparer.Ordinal);
        var laneIds = w.Lanes.Select(l => l.LaneId).ToHashSet(StringComparer.Ordinal);
        foreach (var e in w.Entities)
        {
            var atSector = e.AtSectorId != null;
            var onLane = e.OnLaneId != null;

            if (atSector && onLane)
                throw new InvalidOperationException($"Entity '{e.EntityId}' is both at a sector and on a lane.");
            if (!atSector && !onLane)
                throw new InvalidOperationException($"Entity '{e.EntityId}' is neither at a sector nor on a lane.");
            if (atSector && !sectorIds.Contains(e.AtSectorId!))
                throw new InvalidOperationException($"Entity '{e.EntityId}' stands at unknown sector '{e.AtSectorId}'.");
            if (onLane && !laneIds.Contains(e.OnLaneId!))
                throw new InvalidOperationException($"Entity '{e.EntityId}' marches on unknown lane '{e.OnLaneId}'.");
            if (atSector && e.LaneProgressMilli != 0)
                throw new InvalidOperationException(
                    $"Entity '{e.EntityId}' stands in a sector but carries lane progress {e.LaneProgressMilli}.");
            if (atSector && e.OnLaneTowardSectorId != null)
                throw new InvalidOperationException(
                    $"Entity '{e.EntityId}' stands in a sector but is still heading somewhere.");

            if (onLane)
            {
                if (e.OnLaneTowardSectorId is not { } heading)
                    throw new InvalidOperationException(
                        $"Entity '{e.EntityId}' is on a lane without a heading — progress alone cannot say which way it is going.");

                var lane = w.Lanes.First(l => string.Equals(l.LaneId, e.OnLaneId, StringComparison.Ordinal));
                if (!string.Equals(heading, lane.FromSectorId, StringComparison.Ordinal)
                    && !string.Equals(heading, lane.ToSectorId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Entity '{e.EntityId}' is heading to '{heading}', which is not an end of lane '{lane.LaneId}'.");
            }

            if (e.LaneProgressMilli is < 0 or > 1000)
                throw new InvalidOperationException($"Entity '{e.EntityId}' lane progress {e.LaneProgressMilli} is outside 0..1000.");
        }
    }

    /// <summary>A negative Fracture is nonsense and an unbounded one is an overflow waiting for a multiplication.</summary>
    static void Rule9FractureIntensityBounded(WorldState w)
    {
        foreach (var s in w.Sectors)
            if (s.FractureIntensityMilli < 0 || s.FractureIntensityMilli > MaxIntensityMilli)
                throw new InvalidOperationException(
                    $"Sector '{s.SectorId}' has Fracture intensity {s.FractureIntensityMilli}, outside 0..{MaxIntensityMilli}.");
    }

    /// <summary>There is no such thing as owing loam — a shortfall is a fade, resolved in loam-turn, never a negative balance.</summary>
    static void Rule10LoamStockNonNegative(WorldState w)
    {
        foreach (var s in w.Sectors)
            if (s.LoamStock < 0)
                throw new InvalidOperationException($"Sector '{s.SectorId}' has negative loam stock {s.LoamStock}.");
    }

    /// <summary>
    /// A starting position with no loam source is a world that cannot be played. This is the only
    /// rule that mentions the homeworld, and it is about playability, not about loam mechanics —
    /// after the component-pooling resolution, nothing in the loam rules themselves reads
    /// <see cref="SectorTypeFlags.Home"/> at all.
    /// </summary>
    static void Rule11HomeworldHasARootbed(WorldState w)
    {
        var home = w.Sectors.Single(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home));
        if (home.Slots.All(sl => sl.SlotTypeId != SlotTypeCatalog.RootbedSlotTypeId))
            throw new InvalidOperationException($"Homeworld '{home.SectorId}' must hold a rootbed slot.");
    }

    /// <summary>An unbounded multiplier is an overflow, and a zero one is an invulnerable faction.</summary>
    static void Rule12HandicapBounded(WorldState w)
    {
        foreach (var f in w.Factions)
            if (f.UpkeepHandicapMilli < MinHandicapMilli || f.UpkeepHandicapMilli > MaxHandicapMilli)
                throw new InvalidOperationException(
                    $"Faction '{f.FactionId}' has upkeep handicap {f.UpkeepHandicapMilli}, outside {MinHandicapMilli}..{MaxHandicapMilli}.");
    }

    /// <summary>
    /// A template's node count matches the size tier it declares (spec-loam-maps.md) — so a
    /// template cannot silently outgrow (or fall short of) the vocabulary it claims, a range rather
    /// than a number because the authored map lands where its teaching properties want it.
    /// </summary>
    static void Rule13TemplateSizeMatchesItsDeclaredTier(WorldState w)
    {
        var sizeId = WorldTemplateCatalog.SizeIdOf(w.TemplateId);
        var size = WorldSizeCatalog.Get(sizeId);
        if (w.Sectors.Count < size.MinNodes || w.Sectors.Count > size.MaxNodes)
            throw new InvalidOperationException(
                $"Template '{w.TemplateId}' has {w.Sectors.Count} sectors, outside its declared '{sizeId}' range {size.MinNodes}..{size.MaxNodes}.");
    }

    /// <summary>
    /// A structure only sits on the ground it was built for (spec-structure-substrate.md) — same
    /// shape as <see cref="Rule6SlotShape"/>'s sector-type/slot-type pairing, one level down.
    /// </summary>
    static void Rule14StructureSlotKindMatches(WorldState w)
    {
        foreach (var s in w.Sectors)
            foreach (var slot in s.Slots)
            {
                if (slot.StructureId is null) continue;

                if (!StructureCatalog.IsKnown(slot.StructureId))
                    throw new InvalidOperationException(
                        $"Sector '{s.SectorId}' slot {slot.SlotIndex} carries unknown structure '{slot.StructureId}'.");

                var structure = StructureCatalog.Get(slot.StructureId);
                var slotKind = SlotTypeCatalog.Get(slot.SlotTypeId).Kind;
                if (slotKind != structure.RequiredSlotKind)
                    throw new InvalidOperationException(
                        $"Sector '{s.SectorId}' slot {slot.SlotIndex} ({slotKind}) cannot carry structure " +
                        $"'{slot.StructureId}', which requires {structure.RequiredSlotKind}.");
            }
    }

    /// <summary>Same shape as <see cref="Rule10LoamStockNonNegative"/>, one level up — a stock, never a debt.</summary>
    static void Rule15RecruitStockNonNegative(WorldState w)
    {
        foreach (var s in w.Sectors)
            if (s.RecruitStock < 0)
                throw new InvalidOperationException($"Sector '{s.SectorId}' has negative recruit stock {s.RecruitStock}.");
    }

    /// <summary>
    /// Mirrors `WorldSlot`'s own `ConstructionTurnsRemaining`/`StructureId` pairing, one level up —
    /// a turns-remaining count with nothing it is counting down is a malformed world, not content.
    /// </summary>
    static void Rule16ProjectTurnsRemainingNeedsAProjectId(WorldState w)
    {
        foreach (var s in w.Sectors)
            if (s.ProjectTurnsRemaining is not null && s.ProjectId is null)
                throw new InvalidOperationException(
                    $"Sector '{s.SectorId}' has ProjectTurnsRemaining {s.ProjectTurnsRemaining} with no ProjectId.");
    }
}
