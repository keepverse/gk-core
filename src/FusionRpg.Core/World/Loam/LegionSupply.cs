using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Loam;

/// <summary>
/// Pure capacity/burn/leash math for a legion's own loam economy (spec-loam-legions.md). Unwired —
/// nothing here reads `WorldState` beyond one entity, and nothing here is called from `TurnEngine`
/// yet; that is L27's (top-up/burn) and L28's (`Sustain`) job.
///
/// The degeneracy SSOT §6 names by name: capacity and burn must scale with *different* things, or
/// bearers buy nothing. Capacity scales with bearer count alone; burn scales with total headcount.
/// </summary>
public static class LegionSupply
{
    public static int BearerCount(WorldEntity entity) =>
        entity.Members.Count(m => m.Role == WorldEntityMemberRole.Bearer);

    /// <summary>Zero for an all-fighter legion — it can top up freely in supply but has no reserve at all the moment it steps outside it.</summary>
    public static long Capacity(WorldEntity entity) =>
        BearerCount(entity) * LoamPolicy.CarryPerBearer;

    /// <summary>Mouths to feed, fighters and bearers alike.</summary>
    public static long Burn(WorldEntity entity) =>
        entity.Members.Count * LoamPolicy.BurnPerMember;

    /// <summary>
    /// Turns of runway at the current burn rate before <see cref="Capacity"/> runs out, floored —
    /// the number the ideal wants legible and plannable. Null for an empty legion, which has
    /// nothing to burn and so nothing to run out on.
    /// </summary>
    public static int? LeashTurns(WorldEntity entity)
    {
        var burn = Burn(entity);
        return burn <= 0 ? null : (int)(Capacity(entity) / burn);
    }

    /// <summary>
    /// The march-loam gate's soft half (spec-loam-ai.md §"the march loam gate"): turns of runway
    /// left in what this legion is *actually carrying* right now, at its current burn rate — not
    /// <see cref="LeashTurns"/>'s full-tank ceiling. Pure reporting over already-carried state, no
    /// supply-connectivity lookup: it does not ask whether the legion is in supply this turn (that
    /// answer changes every turn it marches), only what its own numbers already say. Null for the
    /// same reason <see cref="LeashTurns"/> is: nothing to burn means nothing to run out on.
    /// </summary>
    public static int? TurnsUntilExhausted(WorldEntity entity)
    {
        var burn = Burn(entity);
        return burn <= 0 ? null : (int)(Math.Max(0, entity.CarriedLoam) / burn);
    }

    /// <summary>
    /// The Pressure phase's legion pass (spec-loam-legions.md), resolved *after*
    /// <see cref="LoamPhases.Pressure"/>'s own sector-upkeep draw — sector upkeep first, legion
    /// top-up second, from whatever the same pool has left. A legion inside its faction's supply
    /// tops its <see cref="WorldEntity.CarriedLoam"/> up toward <see cref="Capacity"/> for free and
    /// never burns; a legion outside it burns toward zero and is destroyed outright the turn its
    /// carried loam would go negative covering the burn — no wound-based fallback.
    ///
    /// A faction with no supply network at all (no Seat reachable) is exempt entirely, the same
    /// gate <see cref="SupplyGraph.Run"/> already applies — the wild do not burn for want of a
    /// capital they never had.
    /// </summary>
    public static WorldState Resolve(WorldState world, TurnReport report, string phase)
    {
        var connectedByFaction = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var faction in world.Factions)
            connectedByFaction[faction.FactionId] = SupplyGraph.ConnectedSectors(world, faction.FactionId);

        var stockById = world.Sectors.ToDictionary(s => s.SectorId, s => s.LoamStock, StringComparer.Ordinal);
        var carriedById = world.Entities.ToDictionary(e => e.EntityId, e => e.CarriedLoam, StringComparer.Ordinal);
        var destroyed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var faction in world.Factions)
        {
            if (!connectedByFaction.TryGetValue(faction.FactionId, out var connected) || connected.Count == 0)
                continue;

            // base-defense siege-supply §2: `TerritoryComponents.For` pools by ownership + lane
            // adjacency ONLY — it does not itself cut on contested-ness (verified by reading it, not
            // assumed; it has four OTHER callers — loam upkeep, an AI policy, a server endpoint — so
            // fixing contested-ness INTO that shared function would silently change all of them).
            // Re-split its output LOCALLY, here, so a besieged sector never pools with a friendly
            // neighbour for legion top-up specifically: "a besieged sector draws only on itself."
            foreach (var rawComponent in TerritoryComponents.For(world, faction.FactionId))
            foreach (var component in SplitOutBesieged(rawComponent, world, faction.FactionId))
            {
                var toppingUp = world.Entities
                    .Where(e => string.Equals(e.OwnerFactionId, faction.FactionId, StringComparison.Ordinal))
                    .Where(e => e.AtSectorId is { } at && component.Contains(at))
                    .Select(e => (Entity: e, Demand: RationedDemand(e, carriedById[e.EntityId], world, faction.FactionId)))
                    .Where(x => x.Demand > 0)
                    .OrderBy(x => x.Entity.EntityId, StringComparer.Ordinal)
                    .ToList();

                if (toppingUp.Count == 0) continue;

                var available = component.Sum(id => stockById[id]);
                var totalDemand = toppingUp.Sum(x => x.Demand);
                var drawn = Math.Min(available, totalDemand);
                if (drawn <= 0) continue;

                LoamPhases.DrawProportionally(component, stockById, drawn, available);
                DistributeToLegions(toppingUp, carriedById, drawn, totalDemand);
                report.Add(phase, TurnReportKinds.Event, faction.FactionId, "legion.topup:" + drawn, audience: faction.FactionId);

                // world-stage W11 (re-homed from `world-playback` and ideal §2.3): the counterpart
                // to `supply.cut:` — nothing reported the reverse before this. Per legion, not per
                // component, because a player asks "is *my* legion okay now", not "did this pool
                // move any loam". Fires only on the turn a legion's own deficit is fully erased
                // (`Capacity - carried == 0` after distribution), never on a turn that only
                // partially refills it — the signal that survives being checked every turn without
                // repeating: once a legion is whole, `Demand` is 0 and it drops out of `toppingUp`
                // entirely, so it cannot fire again until it is genuinely cut and refilled a second
                // time. `CarriedLoam` alone (not a new persisted flag) is what makes this
                // derivable without adding a hashed field.
                foreach (var (entity, _) in toppingUp)
                    if (carriedById[entity.EntityId] == Capacity(entity))
                        report.Add(phase, TurnReportKinds.Event, entity.EntityId, "supply.restored",
                            sectorId: entity.AtSectorId, audience: entity.OwnerFactionId);
            }
        }

        foreach (var entity in world.Entities)
        {
            if (!connectedByFaction.TryGetValue(entity.OwnerFactionId, out var connected) || connected.Count == 0)
                continue;
            if (SupplyGraph.InSupply(world, entity, connected))
                continue;

            var burn = Burn(entity);
            if (burn <= 0) continue;

            var remaining = carriedById[entity.EntityId] - burn;
            if (remaining < 0)
            {
                destroyed.Add(entity.EntityId);
                report.Add(phase, TurnReportKinds.Event, entity.EntityId,
                    "legion.starved:" + (entity.AtSectorId ?? entity.OnLaneId ?? ""), entity.AtSectorId);
            }
            else
            {
                carriedById[entity.EntityId] = remaining;
                report.Add(phase, TurnReportKinds.Event, entity.EntityId, "legion.burn:" + burn, entity.AtSectorId);
            }
        }

        var survivingEntities = world.Entities
            .Where(e => !destroyed.Contains(e.EntityId))
            .Select(e => e with { CarriedLoam = carriedById[e.EntityId] })
            .ToList();

        var sectors = world.Sectors.Select(s => s with { LoamStock = stockById[s.SectorId] }).ToList();

        return world with { Sectors = sectors, Entities = survivingEntities };
    }

    /// <summary>
    /// Re-partitions a `TerritoryComponents.For` component so a BESIEGED sector never pools with its
    /// neighbours for legion top-up (audit F1 §2). `TerritoryComponents.For` is shared by four other
    /// callers (loam upkeep, an AI policy, a server endpoint) and does not itself cut on
    /// contested-ness — this is a LOCAL, LegionSupply-only re-split, never a change to that shared
    /// utility, which would silently change behaviour for all of them.
    ///
    /// <para>Ordinal by sector id within each yielded group, so the enumeration order this feeds into
    /// (a `foreach` whose body draws stock) is replay-stable regardless of dictionary iteration order
    /// upstream.</para>
    /// </summary>
    static IEnumerable<IReadOnlyList<string>> SplitOutBesieged(
        IReadOnlyList<string> component, WorldState world, string factionId)
    {
        var remaining = new List<string>(component.Count);
        foreach (var sectorId in component.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (SupplyGraph.IsBesieged(world, sectorId, factionId))
                yield return new[] { sectorId };   // its own singleton — draws only on itself
            else
                remaining.Add(sectorId);
        }

        if (remaining.Count > 0)
            yield return remaining;
    }

    /// <summary>
    /// base-defense siege-supply, decision 42: a garrison topping up inside a BESIEGED sector draws
    /// at `LoamPolicy.BesiegedRationMilli` of normal — applied to DEMAND, before
    /// <see cref="DrawProportionally"/>-equivalent distribution runs, so a rationed garrison asks for
    /// less rather than being served less and the proportional-draw arithmetic downstream is
    /// untouched. `1000` (shipped default) is a no-op, keeping the F1/F1b defect fix and this balance
    /// dial independently verifiable.
    /// </summary>
    static long RationedDemand(WorldEntity entity, long carried, WorldState world, string factionId)
    {
        var raw = Math.Max(0, Capacity(entity) - carried);
        if (raw <= 0) return raw;
        if (entity.AtSectorId is not { } at || !SupplyGraph.IsBesieged(world, at, factionId))
            return raw;

        // long * int, one divide, last, checked — docs/architecture/numeric-types.md rules 3/4/5.
        return checked(raw * LoamPolicy.BesiegedRationMilli / 1000);
    }

    /// <summary>Same shape as <see cref="LoamPhases.DrawProportionally"/>, one level down: proportional by demand share, remainder to the first legion in ordinal id order.</summary>
    static void DistributeToLegions(
        IReadOnlyList<(WorldEntity Entity, long Demand)> toppingUp,
        Dictionary<string, long> carriedById, long drawn, long totalDemand)
    {
        var remaining = drawn;
        foreach (var (entity, demand) in toppingUp)
        {
            var share = drawn * demand / totalDemand;
            carriedById[entity.EntityId] += share;
            remaining -= share;
        }

        if (remaining > 0)
            carriedById[toppingUp[0].Entity.EntityId] += remaining;
    }
}
