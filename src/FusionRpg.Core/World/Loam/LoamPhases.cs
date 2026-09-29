using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Loam;

/// <summary>
/// Wakes `Production` and `Pressure` up (spec-loam-turn.md). Pure in <c>(state, seed)</c> like every
/// other phase — no wall clock, no unowned RNG. Kept out of `TurnEngine.cs` for the same reason
/// `SiegePhase`/`MovementPhase` are their own files: that file is already the busiest in the module.
/// </summary>
public static class LoamPhases
{
    /// <summary>
    /// Yield per sector into <c>LoamStock</c>, capped at <see cref="LoamPolicy.LoamCapacity"/>.
    /// Overflow above the cap is lost and said to be lost, naming the sector — a per-faction summary
    /// would hide *which* sector is wasting, which is the only actionable half of the fact.
    /// </summary>
    public static WorldState Production(WorldState world, TurnReport report, string phase)
    {
        // wonder-effect-empire §Design 1: an explicit two-pass shape. Decrement every sector's
        // construction counters first, producing the full post-decrement list; the per-faction
        // Wonder scan then reads that same list — so a Wonder finishing construction this exact
        // pass already contributes this pass (matching Sector-scope's own same-pass activation),
        // and no sector is ever read from two different snapshots.
        var decremented = world.Sectors.Select(DecrementConstruction).ToList();

        // Once per faction, not once per sector; recomputed fresh every phase from the loaded
        // WorldState only — never a live DB read. Written back onto the factions in memory;
        // SQL persistence is Task 2.3b's job.
        var modifierByFaction = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var faction in world.Factions)
            modifierByFaction[faction.FactionId] =
                WonderEmpireEffects.ComputeScopeModifierMilli(decremented, faction.FactionId);

        var factions = world.Factions
            .Select(f => modifierByFaction.TryGetValue(f.FactionId, out var m) && m != f.ScopeModifierMilli
                ? f with { ScopeModifierMilli = m }
                : f)
            .ToList();

        var sectors = new List<WorldSector>(decremented.Count);

        foreach (var sector in decremented)
        {
            var scopeModifierMilli = sector.OwnerFactionId is { } owner
                && modifierByFaction.TryGetValue(owner, out var m)
                    ? m
                    : 1000;

            var yield = LoamProduction.For(sector, scopeModifierMilli);
            if (yield == 0)
            {
                sectors.Add(sector);
                continue;
            }

            // The cap throttles new accrual only — it never claws back stock already held, the same
            // fix the economy harness (L9) needed for its own local ledger.
            var room = Math.Max(0, EffectiveCapacity(sector) - sector.LoamStock);
            var added = Math.Min(room, yield);
            var overflow = yield - added;

            if (overflow > 0)
                report.Add(phase, TurnReportKinds.Event, sector.SectorId, "loam.overflow:" + overflow, sector.SectorId);

            sectors.Add(sector with { LoamStock = sector.LoamStock + added });
        }

        return world with { Sectors = sectors, Factions = factions };
    }

    /// <summary>
    /// <see cref="LoamPolicy.LoamCapacity"/>, plus any active granary's <c>CapacityBonus</c>
    /// (spec-loam-texture.md), plus base-defense `structure-state`'s F12 growth term
    /// (<see cref="StructurePolicy.CapacityGrowthFor"/>) — additive to the shape `LoamProduction`'s own
    /// well multiplier already uses. Public and shared with <see cref="LoamForecast.ProjectedStock"/>
    /// so the engine and the player-facing forecast read the same ceiling rather than risking two
    /// copies drifting apart.
    ///
    /// <para><b>F12, base-defense audit finding</b>: decision 21 grows a sector's rootbed SLOTS with
    /// `DevelopmentLevel`, but a fixed storage cap meant a new slot's entire output became overflow the
    /// moment the cap was already full — "decision 21 buys zero economy" at equilibrium. The growth
    /// term here is what makes a new slot actually produce, additive to the base/granary capacity
    /// above, never a replacement of it.</para>
    /// </summary>
    public static long EffectiveCapacity(WorldSector sector)
    {
        long bonus = 0;
        foreach (var slot in sector.Slots)
        {
            if (slot.StructureId is not { } id) continue;
            if (slot.ConstructionTurnsRemaining is > 0) continue;
            if (!StructureCatalog.IsKnown(id)) continue;

            var structure = StructureCatalog.Get(id);
            if (structure.Kind == StructureKind.Storage) bonus += structure.CapacityBonus;
        }

        return checked(LoamPolicy.LoamCapacity + bonus + StructurePolicy.CapacityGrowthFor(sector.DevelopmentLevel));
    }

    /// <summary>Every slot still under construction counts down by one, this pass, before anything reads it.</summary>
    static WorldSector DecrementConstruction(WorldSector sector)
    {
        if (sector.Slots.All(sl => sl.ConstructionTurnsRemaining is not > 0)) return sector;

        return sector with
        {
            Slots = sector.Slots
                .Select(sl => sl.ConstructionTurnsRemaining is > 0
                    ? sl with { ConstructionTurnsRemaining = sl.ConstructionTurnsRemaining - 1 }
                    : sl)
                .ToList()
        };
    }

    /// <summary>
    /// Upkeep and fade, per component, run *after* <c>SupplyGraph.Run</c> so garrison upkeep reads
    /// the garrison that survived attrition this turn.
    ///
    /// Per faction, per component: sum upkeep, draw it from the pooled stock proportionally
    /// (remainder settled in ordinal id order), and — if the pool cannot cover it — apply the whole
    /// shortfall as fade to the single weakest contributor (worst net balance, ordinal tiebreak).
    /// One sector absorbs one turn's fade; if it is lost, the component recomputes next turn with
    /// one fewer member and a new weakest takes over. That is the countdown the design calls for,
    /// not a same-turn cascade across every member at once.
    ///
    /// <paramref name="ceded"/> is a faction id → sector id map built by <see cref="Turn.TurnEngine"/>
    /// from this turn's `cede` orders (world-stage W25), the same way it already derives `postures`
    /// from `stance` orders — a plain map, never a service or a lookup, passed straight into the one
    /// <see cref="LoamForecast.Weakest"/> selection so a filed order is an input to that choice, not
    /// a second rule that could disagree with it.
    /// </summary>
    public static WorldState Pressure(
        WorldState world, TurnReport report, string phase, int turn = 0, ulong seed = 0,
        IReadOnlyDictionary<string, string>? ceded = null)
    {
        var stockById = world.Sectors.ToDictionary(s => s.SectorId, s => s.LoamStock, StringComparer.Ordinal);
        var stabilityById = world.Sectors.ToDictionary(s => s.SectorId, s => s.StabilityMilli, StringComparer.Ordinal);
        var pressureById = world.Sectors.ToDictionary(s => s.SectorId, s => s.PressureMilli, StringComparer.Ordinal);
        var lost = new HashSet<string>(StringComparer.Ordinal);
        var fadedThisTurn = new HashSet<string>(StringComparer.Ordinal);

        // Fracture surges (spec-loam-texture.md): while the turn's roll includes Plague, the surge
        // scales DecayFor's whole pre-clamp sum — contagion's own PressureMilli term included — never
        // its already-clamped output, so a sector already at MaxDecayMilli can never be pushed past it.
        var surge = TurnCalendar.Roll(turn, seed).Plague;

        foreach (var faction in world.Factions)
        {
            // A visible handicap is a balance lever; a silent one is a bug that explains itself
            // away. Named exactly once per faction per turn, regardless of how many components or
            // sectors that faction's upkeep touches this turn.
            if (faction.UpkeepHandicapMilli != 1000)
                report.Add(phase, TurnReportKinds.Event, faction.FactionId,
                    "loam.handicap:" + faction.UpkeepHandicapMilli, audience: faction.FactionId);

            foreach (var component in TerritoryComponents.For(world, faction.FactionId))
            {
                var upkeep = component.Sum(id => LoamUpkeep.For(world, world.Sectors.Single(s => s.SectorId == id)));
                var available = component.Sum(id => stockById[id]);
                var drawn = Math.Min(available, upkeep);
                var shortfall = upkeep - drawn;

                DrawProportionally(component, stockById, drawn, available);

                if (shortfall > 0)
                {
                    // Same selection the forecast makes a turn early (LoamForecast.Weakest) — one
                    // rule, so the engine and the player-facing warning cannot silently disagree.
                    // Null used to mean "every member is warded". The warden freeze is retired
                    // (warden-freeze-fix, RulesetVersion 13), so every member is a candidate and a
                    // real shortfall always has a target. The branch below is no longer reachable
                    // from here. It stays so the `loam.shortfall.unresolved` event keeps its one
                    // emitter until the web playback table and the notify spec stop naming it.
                    var factionCeded = ceded != null && ceded.TryGetValue(faction.FactionId, out var cededSector)
                        ? cededSector
                        : null;
                    var weakest = LoamForecast.Weakest(world, component, available, upkeep, factionCeded);
                    if (weakest is null)
                    {
                        report.Add(phase, TurnReportKinds.Event, faction.FactionId, "loam.shortfall.unresolved:" + shortfall, audience: faction.FactionId);
                        continue;
                    }

                    stabilityById[weakest] = FadePolicy.Apply(
                        stabilityById[weakest], -shortfall, pressureById[weakest], surge);
                    report.Add(phase, TurnReportKinds.Event, faction.FactionId, "loam.shortfall:" + shortfall, weakest);
                    fadedThisTurn.Add(weakest);

                    if (stabilityById[weakest] == 0)
                        lost.Add(weakest);
                }
                else
                {
                    // Paid in full: every member recovers, not just the weakest — a component that
                    // can cover its own upkeep is not fading anywhere, which is ideal §12.4's "a
                    // rich core carries a poor frontier indefinitely" made literal. A warded member
                    // recovers like any other: the owner withdrew the warden freeze on 2026-09-13
                    // (warden-mortality-ideal.md), because a frozen StabilityMilli let one specimen
                    // switch off the decay side of the loam economy for free. A leftover
                    // WardenBindingId is data only (warden-freeze-fix, RulesetVersion 13).
                    foreach (var id in component)
                        stabilityById[id] = FadePolicy.Apply(stabilityById[id], balance: 1);
                }
            }
        }

        var nextPressureById = NextPressure(world, fadedThisTurn);

        var sectors = world.Sectors.Select(s =>
        {
            if (lost.Contains(s.SectorId))
            {
                report.Add(phase, TurnReportKinds.Event, s.OwnerFactionId ?? "", "loam.lost:" + s.SectorId, s.SectorId);
                return s with
                {
                    LoamStock = stockById[s.SectorId], StabilityMilli = 0, Phase = SectorPhase.Lost,
                    OwnerFactionId = null, PressureMilli = nextPressureById[s.SectorId],
                    // A half-built structure is not a refund, it is exactly the loss G1 warns the
                    // player about (spec-loam-structures.md) — new code, not a description of
                    // behaviour that already existed: this branch never touched `s.Slots` before.
                    Slots = s.Slots
                        .Select(sl => sl with { StructureId = null, ConstructionTurnsRemaining = null })
                        .ToList(),
                    // A half-finished sector-wide project is the identical loss, one level up
                    // (world-map W52, spec-sector-development.md §3) — a ghost `ProjectId` that
                    // `Growth` never sees again (this sector is unowned from here on, and a lost
                    // sector's own recapture starts fresh) would otherwise sit on the sector forever,
                    // permanently blocking a future `develop` order via `develop.already-developing`.
                    ProjectId = null, ProjectTurnsRemaining = null
                };
            }

            return s with
            {
                LoamStock = stockById[s.SectorId], StabilityMilli = stabilityById[s.SectorId],
                PressureMilli = nextPressureById[s.SectorId]
            };
        }).ToList();

        var (neglectedSectors, spawned) = UpdateNeglectAndSpawnTheUnmade(world, sectors, report, phase);

        var entities = spawned.Count == 0
            ? world.Entities
            : world.Entities.Concat(spawned).OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList();

        return world with { Sectors = neglectedSectors, Entities = entities };
    }

    /// <summary>
    /// The Unmade (spec-loam-texture.md): no new AI, `Wild`-owned barbarians spawned onto neglected,
    /// `Lost`, barren ground with the already-shipped `StandFastPolicy`. Gated by construction on a
    /// map actually having a `WorldFactionKind.Wild` faction row — the same G-C-shaped exemption
    /// this program already applies everywhere a mechanic needs a faction that might not exist on
    /// every map, re-proven here rather than assumed to survive into a new mechanic untested.
    /// </summary>
    static (List<WorldSector> Sectors, List<WorldEntity> Spawned) UpdateNeglectAndSpawnTheUnmade(
        WorldState world, IReadOnlyList<WorldSector> sectors, TurnReport report, string phase)
    {
        var wildFactionId = world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Wild)?.FactionId;
        var spawned = new List<WorldEntity>();
        var result = new List<WorldSector>(sectors.Count);

        foreach (var sector in sectors)
        {
            var neglectable = sector.Phase == SectorPhase.Lost && !Habitability.For(sector);
            if (!neglectable)
            {
                result.Add(sector.NeglectedTurns == 0 ? sector : sector with { NeglectedTurns = 0 });
                continue;
            }

            var neglectedTurns = sector.NeglectedTurns + 1;

            var alreadyOccupied = world.Entities.Any(e =>
                string.Equals(e.AtSectorId, sector.SectorId, StringComparison.Ordinal));

            if (wildFactionId is { } faction && neglectedTurns >= LoamPolicy.UnmadeSpawnAfterTurns && !alreadyOccupied)
            {
                spawned.Add(SpawnTheUnmade(world.Seed, world.CurrentTurn, faction, sector));
                report.Add(phase, TurnReportKinds.Event, faction, "unmade.spawned:" + sector.SectorId, sector.SectorId);
                neglectedTurns = 0; // the ground is no longer empty — the countdown starts over if it is cleared again
            }

            result.Add(sector with { NeglectedTurns = neglectedTurns });
        }

        return (result, spawned);
    }

    /// <summary>species-gear-chain T7: the warband is rolled, not literal. Members are real
    /// admitted species from the sector's weight table (seeded, replay-stable); T18b: each member
    /// carries its own species-derived `P(Θ)` HP straight off the pick — written verbatim, never
    /// recomputed at the spawn site.</summary>
    static WorldEntity SpawnTheUnmade(ulong worldSeed, int turn, string wildFactionId, WorldSector sector)
    {
        var picks = WildSpawnRoller.Roll(
            worldSeed, sector.SectorId, turn, sector, LoamPolicy.UnmadeMemberCount, WorldSpawnTuningHub.Tuning);
        return new()
        {
            EntityId = $"e-unmade-{sector.SectorId}",
            Kind = WorldEntityKind.Warband,
            OwnerFactionId = wildFactionId,
            AtSectorId = sector.SectorId,
            Stance = MovementPolicy.Hold,
            MovementRemaining = 0,
            Members = picks
                .Select(p => new WorldEntityMember { SpeciesId = p.SpeciesId, Level = 1, Hp = p.Hp })
                .ToList()
        };
    }

    /// <summary>
    /// Fade contagion (spec-loam-texture.md): every sector lane-adjacent to one that actually faded
    /// this turn has its `PressureMilli` raised by <see cref="LoamPolicy.ContagionPressurePerTurn"/>,
    /// capped at <see cref="LoamPolicy.MaxPressureMilli"/> — a live signal of nearby trouble, not a
    /// ratchet, so every other sector decays back toward zero at
    /// <see cref="LoamPolicy.PressureDecayPerTurn"/> instead.
    /// </summary>
    static Dictionary<string, int> NextPressure(WorldState world, IReadOnlySet<string> fadedThisTurn)
    {
        var pressured = new HashSet<string>(StringComparer.Ordinal);
        if (fadedThisTurn.Count > 0)
            foreach (var lane in world.Lanes)
            {
                if (fadedThisTurn.Contains(lane.FromSectorId)) pressured.Add(lane.ToSectorId);
                if (fadedThisTurn.Contains(lane.ToSectorId)) pressured.Add(lane.FromSectorId);
            }

        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sector in world.Sectors)
            next[sector.SectorId] = pressured.Contains(sector.SectorId)
                ? Math.Min(LoamPolicy.MaxPressureMilli, sector.PressureMilli + LoamPolicy.ContagionPressurePerTurn)
                : Math.Max(0, sector.PressureMilli - LoamPolicy.PressureDecayPerTurn);

        return next;
    }

    /// <summary>
    /// The SSOT's stated draw rule: proportional by stock share, remainder in ordinal id order.
    /// Public so <c>LegionSupply</c>'s own top-up pass draws from the same pool the same way,
    /// rather than inventing a second draw rule beside this one (spec-loam-legions.md).
    /// </summary>
    public static void DrawProportionally(
        IReadOnlyList<string> component, Dictionary<string, long> stockById, long drawn, long available)
    {
        if (available == 0) return;

        var remaining = drawn;
        foreach (var id in component.OrderBy(x => x, StringComparer.Ordinal))
        {
            var share = drawn * stockById[id] / available;
            stockById[id] -= share;
            remaining -= share;
        }

        if (remaining > 0)
            stockById[component.OrderBy(x => x, StringComparer.Ordinal).First()] -= remaining;
    }
}
