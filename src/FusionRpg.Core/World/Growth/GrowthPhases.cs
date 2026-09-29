using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Growth;

/// <summary>
/// Wakes the `Growth` phase up (spec-sector-development.md §1) — `TurnEngine.cs:196-200` was
/// `report.BeginPhase(Phases.Growth); return world;` in full before this task. Pure in
/// <c>(state, seed)</c> like every other phase — no wall clock, no unowned RNG, `CalendarRoll` is
/// the only randomness anywhere near this and it is derived, not drawn here. Kept out of
/// `TurnEngine.cs` for the same reason `LoamPhases`/`SiegePhase`/`MovementPhase` are their own
/// files: that file is already the busiest in the module.
/// </summary>
public static class GrowthPhases
{
    /// <summary>
    /// Every held sector with a Seat accrues the weekly pulse into
    /// <see cref="WorldSector.RecruitStock"/>, on week boundaries and only on week boundaries
    /// (<see cref="RecruitPolicy.PulseFor"/> itself gates that). Walks `world.Sectors` in its own
    /// stored order (never a dictionary) — the same stable-ordering discipline every other phase
    /// already keeps.
    ///
    /// Takes every tuning number as an explicit parameter rather than reading `RecruitPolicy`'s own
    /// accessors internally — the same split the module's own Code Style section states ("pure
    /// functions over the world model... the phase wiring calls them and applies the result") and
    /// `RecruitPolicy.PulseFor` itself already uses one level down: `TurnEngine.Growth` (the real
    /// caller) reads the live, process-wide tuning and passes it in; a test supplies whatever local
    /// values it needs directly, with no risk of racing `RecruitPolicy.Configure`'s shared static
    /// field against another test class under xUnit's default parallelism.
    /// </summary>
    public static WorldState Growth(
        WorldState world, TurnReport report, string phase, int turn, ulong seed,
        long seatPulsePerWeek, int lairMultiplierMilli, int specialWeekMultiplierMilli)
    {
        var roll = TurnCalendar.Roll(turn, seed);
        var sectors = new List<WorldSector>(world.Sectors.Count);

        foreach (var sector in world.Sectors)
        {
            var working = AdvanceProject(sector, report, phase);

            if (working.OwnerFactionId is null)
            {
                sectors.Add(working);
                continue;
            }

            var hasSeat = working.Slots.Any(sl => sl.SlotTypeId == SlotTypeCatalog.SeatSlotTypeId);
            var clearedLairSlots = working.Slots
                .Where(sl => SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Lair && sl.GuardState == GuardState.Cleared)
                .ToList();
            var lairCleared = clearedLairSlots.Count > 0;

            var pulse = RecruitPolicy.PulseFor(
                hasSeat, lairCleared, roll, seatPulsePerWeek,
                EffectiveLairMultiplierMilli(clearedLairSlots, lairMultiplierMilli), specialWeekMultiplierMilli);

            if (pulse <= 0)
            {
                sectors.Add(working);
                continue;
            }

            // Structural, not prose (world-stage W39's SectorId field) — the same discipline
            // `LoamPhases.Production`'s own "loam.overflow" line already follows.
            report.Add(phase, TurnReportKinds.Event, working.SectorId, "growth.pulse:" + pulse, working.SectorId);
            sectors.Add(working with { RecruitStock = working.RecruitStock + pulse });
        }

        return world with { Sectors = sectors };
    }

    /// <summary>
    /// world-map W56 (spec-sector-development.md §3): "a hatchery on a lair multiplies that sector's
    /// recruit pulse through W43's policy rather than through a second code path" — folds any active
    /// hatchery's own <see cref="StructureDef.YieldMultiplierMilli"/> into the SAME
    /// `lairMultiplierMilli` value <see cref="RecruitPolicy.PulseFor"/> already takes, rather than
    /// adding a second multiplier parameter or a parallel formula. A hatchery only ever contributes
    /// while its own lair slot is actually cleared (an intact lair's hatchery, if that were ever
    /// legal to build, contributes nothing — <c>PulseFor</c> ignores this value entirely unless
    /// <c>lairCleared</c> is true, so this never needs to special-case that itself). Composed as one
    /// combined per-mille product with a single division at the end (widened to `long` before
    /// multiplying, `checked`) — the identical AGENTS.md overflow discipline every other per-mille
    /// composition in this module already follows.
    /// </summary>
    static int EffectiveLairMultiplierMilli(IReadOnlyList<WorldSlot> clearedLairSlots, int lairMultiplierMilli)
    {
        var hatcheryFactorMilli = 1000L;

        foreach (var slot in clearedLairSlots)
        {
            if (slot.StructureId is not { } structureId
                || slot.ConstructionTurnsRemaining is > 0
                || !StructureCatalog.IsKnown(structureId))
                continue;

            var structure = StructureCatalog.Get(structureId);
            if (structure.Kind != StructureKind.Yield) continue;

            checked { hatcheryFactorMilli = hatcheryFactorMilli * structure.YieldMultiplierMilli / 1000; }
        }

        if (hatcheryFactorMilli == 1000) return lairMultiplierMilli;

        checked { return (int)((long)lairMultiplierMilli * hatcheryFactorMilli / 1000); }
    }

    /// <summary>
    /// world-map W52 (spec-sector-development.md §3): a sector-wide project counts down here, never
    /// in `Production` — the deliberate split the task's own text states, mirroring
    /// `LoamPhases.DecrementConstruction`'s exact shape one level up, including its "decrement
    /// unconditional on ownership" precedent (`LoamPhases.Production` counts a structure down even
    /// for a sector that changed hands earlier the same turn; a lost sector's half-built structure
    /// is only ever cleared explicitly, in `LoamPhases.Pressure`'s own `Lost` branch — a project
    /// follows the identical rule, cleared there too, never here).
    ///
    /// Reaching zero completes the project — world-map W53 is the one line this adds: a completed
    /// project raises the sector's <see cref="WorldSector.DevelopmentLevel"/> by the project's own
    /// authored <see cref="ProjectDef.DevelopmentBonus"/>, once, closing the trap
    /// empire-economy-ssot.md A8 names (development priced as pure cost with no producer). **No code
    /// path anywhere lowers a level** — AGENTS.md's no-hard-progression-ceiling rule applies
    /// symmetrically in the other direction too: nothing here, or anywhere else in `src/`, claws a
    /// level back, matching the acceptance's own explicit "there is no de-development" clause.
    /// </summary>
    static WorldSector AdvanceProject(WorldSector sector, TurnReport report, string phase)
    {
        if (sector.ProjectId is not { } projectId) return sector;

        var remaining = (sector.ProjectTurnsRemaining ?? 0) - 1;
        if (remaining > 0)
            return sector with { ProjectTurnsRemaining = remaining };

        report.Add(phase, TurnReportKinds.Event, sector.SectorId, "develop.completed:" + projectId, sector.SectorId);
        var completed = sector with { ProjectId = null, ProjectTurnsRemaining = null };

        // Defensive, matching `LoamProduction.For`'s own `StructureCatalog.IsKnown` guard before a
        // `.Get` one module over: a persisted sector's `ProjectId` could in principle outlive a
        // future catalog edit that removes the row, and completion must not throw for that.
        var bonus = ProjectCatalog.IsKnown(projectId) ? ProjectCatalog.Get(projectId).DevelopmentBonus : 0;
        if (bonus <= 0) return completed;

        report.Add(phase, TurnReportKinds.Event, sector.SectorId, "development.raised:" + bonus, sector.SectorId);
        return completed with { DevelopmentLevel = completed.DevelopmentLevel + bonus };
    }
}
