using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Topology;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>
/// Founding a structure (spec-loam-structures.md). A legion standing on ground its own faction holds
/// may order construction on a compatible, empty slot, spending the founding legion's own
/// `CarriedLoam` — G1's bootstrap spend, the same reason `Sustain` never draws from the shared
/// component pool either.
///
/// Resolves in `Snapshot`, the same phase `Claim` already settles in, and for the same reason: who
/// owns the sector is only decided once the rest of the turn has run. `Build` re-validates ownership
/// at resolution rather than trusting Reveal-time admission — the same race `ClaimResolver` already
/// guards against, not a new discipline.
/// </summary>
public static class BuildResolver
{
    public static WorldState Run(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, string phase)
    {
        var next = world;

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.Build))
        {
            var entity = next.Entities.FirstOrDefault(e =>
                string.Equals(e.EntityId, command.EntityId, StringComparison.Ordinal));

            if (entity is null)
            {
                Drop(report, phase, command, "entity.gone");
                continue;
            }

            if (entity.Routed)
            {
                Drop(report, phase, command, "entity.routed");
                continue;
            }

            if (entity.AtSectorId is null
                || !string.Equals(entity.AtSectorId, command.SectorId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "build.elsewhere");
                continue;
            }

            var sector = next.Sectors.First(s =>
                string.Equals(s.SectorId, entity.AtSectorId, StringComparison.Ordinal));

            // Re-validated here, not trusted from admission: the founder may have lost this ground
            // to fade or conquest later the same turn.
            if (!string.Equals(sector.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "build.not-yours");
                continue;
            }

            var slot = sector.Slots.FirstOrDefault(sl => sl.SlotIndex == command.SlotIndex);
            if (slot is null)
            {
                Drop(report, phase, command, "slot.unknown");
                continue;
            }

            if (slot.StructureId != null)
            {
                Drop(report, phase, command, "build.occupied:" + slot.StructureId);
                continue;
            }

            var structureId = command.StructureId!;
            if (!StructureCatalog.IsKnown(structureId))
            {
                Drop(report, phase, command, "structure.unknown");
                continue;
            }

            var structure = StructureCatalog.Get(structureId);
            var slotKind = SlotTypeCatalog.Get(slot.SlotTypeId).Kind;
            if (slotKind != structure.RequiredSlotKind)
            {
                Drop(report, phase, command,
                    $"build.wrong-slot-kind:{slotKind}-needs-{structure.RequiredSlotKind}");
                continue;
            }

            // G5's range rule: a *new* source (a Seat-founded structure — a waystation) may only be
            // founded within reach of ground the founder already has anchored. A Rootbed is already
            // its own source, so a well never needs this — it has nothing to be "in range of" that
            // it is not already standing on.
            if (structure.RequiredSlotKind == SlotKind.Seat
                && !WithinWaystationRange(next, command.CommanderId, sector.SectorId))
            {
                Drop(report, phase, command, "build.out-of-range:" + sector.SectorId);
                continue;
            }

            // loam-relics-and-wonders `wonder-build-flow` §Design 4: the resolution-time re-check
            // for a command the Data-side reachability gate rewrote (emptied) moments before
            // resolution. Distinct reason from admission's `relic.count-mismatch` on purpose: the
            // same structural symptom now means "verified unreachable," a live-state finding.
            if (structure.RelicCost > 0 && command.RelicInstanceIds.Count != structure.RelicCost)
            {
                Drop(report, phase, command, "relic.not-reachable");
                continue;
            }

            // loam-relics-and-wonders `wonder-build-flow` §Design 5: the existence cap. Counts from
            // BUILD-ACCEPTANCE (WonderExistenceScan counts under-construction rows), not completion.
            if (structure.WonderScope is { } wScope && structure.WonderRarity is { } wRarity)
            {
                var existing = WonderExistenceScan.CountExisting(next, sector, wScope, wRarity);
                var cap = WonderPolicy.ExistenceCapFor(wScope, wRarity);
                if (existing >= cap)
                {
                    Drop(report, phase, command, "wonder.cap-reached");
                    continue;
                }
            }

            // loam-relics-and-wonders `wonder-build-flow` §Design 6: closes the pre-existing
            // wiring gap for EVERY structure, not only Wonders — no peacetime `build` order has
            // ever exercised this check before (only siege-time `Built` spends these stocks).
            if (!ConstructionCost.CanAffordBuilt(sector.RubbleStock, sector.IronworkStock, structure))
            {
                Drop(report, phase, command, "build.cannot-afford-materials");
                continue;
            }

            if (entity.CarriedLoam < structure.Cost)
            {
                Drop(report, phase, command, "build.cannot-afford");
                continue;
            }

            var entityId = entity.EntityId;
            var sectorId = sector.SectorId;
            var slotIndex = slot.SlotIndex;
            var (newRubble, newIronwork) =
                ConstructionCost.SpendBuilt(sector.RubbleStock, sector.IronworkStock, structure);

            next = next with
            {
                Entities = next.Entities
                    .Select(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal)
                        ? e with { CarriedLoam = e.CarriedLoam - structure.Cost }
                        : e)
                    .ToList(),
                Sectors = next.Sectors
                    .Select(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal)
                        ? s with
                        {
                            RubbleStock = newRubble,
                            IronworkStock = newIronwork,
                            Slots = s.Slots
                                .Select(sl => sl.SlotIndex == slotIndex
                                    ? sl with { StructureId = structureId, ConstructionTurnsRemaining = structure.BuildTurns }
                                    : sl)
                                .ToList()
                        }
                        : s)
                    .ToList()
            };

            report.Add(phase, TurnReportKinds.Event, command.CommandId,
                $"build.started:{structureId}", sectorId);
        }

        return next;
    }

    /// <summary>
    /// G5: is <paramref name="targetSectorId"/> within <see cref="LoamPolicy.WaystationRangeHops"/>
    /// unweighted hops (<see cref="Hops"/>, not the march-cost-weighted <see cref="AllPairsCost"/>)
    /// of a sector the founder already holds that is itself currently habitable — anchored, not
    /// merely held (spec-loam-structures.md).
    /// </summary>
    static bool WithinWaystationRange(WorldState world, string factionId, string targetSectorId)
    {
        var graph = LaneGraph.Build(world);

        foreach (var sector in world.Sectors)
        {
            if (!string.Equals(sector.OwnerFactionId, factionId, StringComparison.Ordinal)) continue;
            if (!Habitability.For(sector)) continue;

            var hops = Hops.Between(graph, sector.SectorId, targetSectorId);
            if (hops is { } h && h <= LoamPolicy.WaystationRangeHops) return true;
        }

        return false;
    }

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
