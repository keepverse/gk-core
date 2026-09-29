using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>
/// The chain back to the homeworld (spec-world-movement.md §Supply connectivity).
///
/// A plain breadth-first pass in stable id order, recomputed every turn and never cached: a stored
/// "in supply" flag is exactly the kind of derived state that goes stale the first time a lane is
/// cut, and it would then be wrong in the one situation the player cares about.
/// </summary>
public static class SupplyGraph
{
    /// <summary>
    /// Sectors this faction can still reach from a Seat it holds. A faction with no Seat of its own
    /// has no supply network at all — the wild do not starve for want of a capital they never had —
    /// so the result is empty and <see cref="Run"/> leaves them alone.
    /// </summary>
    public static IReadOnlySet<string> ConnectedSectors(WorldState world, string factionId)
    {
        var byId = world.Sectors.ToDictionary(s => s.SectorId, StringComparer.Ordinal);

        bool Owned(string sectorId) =>
            byId.TryGetValue(sectorId, out var sector)
            && string.Equals(sector.OwnerFactionId, factionId, StringComparison.Ordinal);

        // base-defense siege-supply (audit F1): can supply TRAVEL through this sector? No — a
        // contested sector is a roadblock. This was always the right answer for the TRAVERSAL, and
        // is unchanged from the original `Usable`.
        bool Traversable(string sectorId) =>
            Owned(sectorId) && !ZoneOfControl.IsHeldAgainst(world, sectorId, factionId);

        // Owned AND contested — the sector a besieged garrison stands in. Deliberately narrower than
        // "not reached by the BFS": a lane-severed-but-uncontested sector is NOT besieged and must
        // keep reporting `supply.cut:` exactly as before this fix — only contested ground gets the
        // self-supply exemption below.
        bool Besieged(string sectorId) => IsBesieged(world, sectorId, factionId);

        // The traversal itself belongs to `SupplyReach`, because a faction policy asks the same
        // question of what it *believes* — same rule, different and deliberately less reliable
        // inputs. A besieged Seat cannot seed the network — that is the traversal being correctly
        // cut — but see below for what it still gets on its own.
        var seats = world.Sectors
            .Where(s => Traversable(s.SectorId)
                        && s.Slots.Any(sl => sl.SlotTypeId == SlotTypeCatalog.SeatSlotTypeId))
            .Select(s => s.SectorId);

        var reached = new HashSet<string>(
            SupplyReach.From(seats, SupplyReach.LinksOf(world.Lanes), Traversable),
            StringComparer.Ordinal);

        // base-defense siege-supply (audit F1b): a besieged sector still supplies ITSELF, even
        // though it cannot route supply to or from the rest of the network — "a base with stores is
        // not a legion in the field". `SupplyReach.From`'s seed inclusion is gated by the SAME
        // `usable` predicate as its traversal expansion (verified by reading `SupplyReach.From`,
        // not assumed — an earlier draft of this fix assumed seeds bypass `usable` and they do not),
        // so a besieged Seat would otherwise be silently dropped as a seed too. Union every besieged
        // owned sector back in explicitly, additive only — this never removes a sector the BFS
        // already reached, and never adds one that is merely lane-severed rather than contested.
        //
        // Consequence, and it is the correct one, not a residual bug: if a faction's ONLY Seat is
        // besieged, `seats` is empty, so no OTHER sector is reached via traversal either — every
        // other sector the faction owns correctly reports `supply.cut:` and its legions correctly
        // burn, exactly as losing your only capital should mean. What F1b actually fixes is narrower
        // and specific: `connected.Count == 0` no longer holds (the besieged Seat itself is in
        // `reached`), so `SupplyGraph.Run`/`LegionSupply.Resolve`'s per-faction
        // `if (connected.Count == 0) continue` skip no longer exempts the WHOLE FACTION from the
        // burn/cut pass — every sector and entity is now correctly evaluated per its own
        // reachability, instead of the besieged Seat's exclusion silently granting the entire
        // faction blanket immunity.
        foreach (var sector in world.Sectors)
            if (Besieged(sector.SectorId))
                reached.Add(sector.SectorId);

        return reached;
    }

    /// <summary>Owned by <paramref name="factionId"/> and held against it right now — factored out of
    /// <see cref="ConnectedSectors"/> so <see cref="Run"/>'s report can name the same fact without
    /// duplicating the predicate.</summary>
    public static bool IsBesieged(WorldState world, string sectorId, string factionId)
    {
        var sector = world.Sectors.FirstOrDefault(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal));
        return sector is not null
               && string.Equals(sector.OwnerFactionId, factionId, StringComparison.Ordinal)
               && ZoneOfControl.IsHeldAgainst(world, sectorId, factionId);
    }

    /// <summary>
    /// The Pressure phase's supply pass: report every holding that has fallen off the chain, and
    /// mend every garrison that is holding one. Exactly once, because it runs exactly once.
    ///
    /// What happens to a force standing *outside* supply is no longer this method's job
    /// (spec-loam-legions.md): `LegionSupply.Resolve` runs after `LoamPhases.Pressure` and owns the
    /// whole burn/destroy decision now that carried loam has replaced wound-based attrition.
    /// </summary>
    public static WorldState Run(WorldState world, TurnReport report, string phase)
    {
        var connectedByFaction = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var faction in world.Factions)
            connectedByFaction[faction.FactionId] = ConnectedSectors(world, faction.FactionId);

        foreach (var sector in world.Sectors)
        {
            if (sector.OwnerFactionId is not { } owner) continue;
            if (!connectedByFaction.TryGetValue(owner, out var connected) || connected.Count == 0) continue;

            // base-defense siege-supply: a besieged sector is now ALWAYS in `connected` (F1/F1b), so
            // it can never also hit the `supply.cut:` branch below — report the fact that actually
            // changed for the player (under siege, not simply severed) instead of staying silent.
            if (IsBesieged(world, sector.SectorId, owner))
            {
                report.Add(phase, TurnReportKinds.Event, owner, "supply.besieged:" + sector.SectorId,
                    sector.SectorId, audience: owner);
                continue;
            }

            if (!connected.Contains(sector.SectorId))
                report.Add(phase, TurnReportKinds.Event, owner, "supply.cut:" + sector.SectorId, sector.SectorId);
        }

        var survivors = new List<WorldEntity>(world.Entities.Count);
        foreach (var entity in world.Entities)
        {
            if (!connectedByFaction.TryGetValue(entity.OwnerFactionId, out var connected) || connected.Count == 0)
            {
                survivors.Add(entity);
                continue;
            }

            if (InSupply(world, entity, connected))
            {
                // Supply gives as well as takes: a garrison with a line home mends. This is the only
                // healing there is, which is why `hold` had to mean something.
                survivors.Add(string.Equals(entity.Stance, MovementPolicy.Hold, StringComparison.Ordinal)
                    ? Recover(entity, report, phase)
                    : entity);
                continue;
            }

            survivors.Add(entity);
        }

        return world with { Entities = survivors };
    }

    /// <summary>A force on a lane counts as supplied if either end of it still is.</summary>
    public static bool InSupply(WorldState world, WorldEntity entity, IReadOnlySet<string> connected)
    {
        if (entity.AtSectorId is { } at) return connected.Contains(at);
        if (entity.OnLaneId is not { } laneId) return false;

        var lane = world.Lanes.FirstOrDefault(l => l.LaneId == laneId);
        return lane is not null
               && (connected.Contains(lane.FromSectorId) || connected.Contains(lane.ToSectorId));
    }

    static WorldEntity Recover(WorldEntity entity, TurnReport report, string phase)
    {
        var mended = false;
        var members = new List<WorldEntityMember>(entity.Members.Count);

        foreach (var m in entity.Members)
        {
            var healed = Math.Max(0, m.Wounds - Math.Max(1, (int)((long)m.Hp * MovementPolicy.RecoveryMilli / 1000)));
            if (healed != m.Wounds) mended = true;
            members.Add(m with { Wounds = healed });
        }

        if (!mended) return entity;

        report.Add(phase, TurnReportKinds.Event, entity.EntityId, "recovery:" + (entity.AtSectorId ?? ""), entity.AtSectorId);
        return entity with { Members = members };
    }
}
