using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>
/// G1's bootstrap spend (spec-loam-legions.md): a legion standing on ground its own faction holds
/// may spend its own carried loam, 1:1, directly into that sector's stock. Resolves at the very top
/// of `Pressure`, before `SupplyGraph.Run` or `LoamPhases.Pressure`'s own accounting, so the spend is
/// already sitting in the sector's stock by the time the component's automatic upkeep draw and
/// weakest-sector selection run this same turn — the whole point of an army being able to save the
/// very ground that would otherwise fade out from under it.
///
/// A player-issued order, not a stance effect: `Sustain` never draws from the shared component
/// pool, so it creates no ordering conflict with `LegionSupply`'s own sector-upkeep-first draw rule,
/// which runs at the opposite end of the phase.
/// </summary>
public static class SustainResolver
{
    public static WorldState Run(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, string phase)
    {
        var next = world;

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.Sustain))
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

            if (entity.AtSectorId is not { } atSectorId)
            {
                Drop(report, phase, command, "sustain.not-standing");
                continue;
            }

            var sector = next.Sectors.FirstOrDefault(s =>
                string.Equals(s.SectorId, atSectorId, StringComparison.Ordinal));

            if (sector is null
                || !string.Equals(sector.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "sustain.not-yours");
                continue;
            }

            // Bounded by what the legion actually carries — never a promise it cannot keep.
            var spend = Math.Min(command.Amount!.Value, entity.CarriedLoam);
            if (spend <= 0)
            {
                Drop(report, phase, command, "sustain.nothing-carried");
                continue;
            }

            var entityId = entity.EntityId;
            var sectorId = sector.SectorId;
            next = next with
            {
                Entities = next.Entities
                    .Select(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal)
                        ? e with { CarriedLoam = e.CarriedLoam - spend }
                        : e)
                    .ToList(),
                Sectors = next.Sectors
                    .Select(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal)
                        ? s with { LoamStock = s.LoamStock + spend }
                        : s)
                    .ToList()
            };

            report.Add(phase, TurnReportKinds.Event, command.CommandId, "sustain:" + spend, sectorId);
        }

        return next;
    }

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
