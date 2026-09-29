using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Growth;

/// <summary>
/// `develop` (world-map W52, spec-sector-development.md §3): spends a sector's own
/// <see cref="WorldSector.LoamStock"/> to start a sector-wide project — the sector-level twin of
/// <c>BuildResolver</c>'s slot-level `build`.
///
/// Resolves in `Snapshot`, right after `raise`, for the identical reason both `BuildResolver` and
/// `RaiseResolver` already state: who owns the sector, and what is already under way there, is only
/// decided once the rest of the turn has run — every legality check here is resolution-time, never
/// trusted from admission (<see cref="WorldCommandAdmission"/>'s own `develop` arm checks only that
/// a sector and a known project were named).
/// </summary>
public static class DevelopResolver
{
    public static WorldState Run(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, string phase)
    {
        var next = world;

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.Develop))
        {
            var sectorId = command.SectorId;
            var sector = sectorId is null
                ? null
                : next.Sectors.FirstOrDefault(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal));

            if (sector is null)
            {
                Drop(report, phase, command, "sector.missing");
                continue;
            }

            // Re-validated here, not trusted from admission: the developing faction may have lost
            // this ground to fade or conquest later the same turn — identical reasoning to
            // `BuildResolver`/`RaiseResolver`.
            if (!string.Equals(sector.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "develop.not-yours");
                continue;
            }

            // One project at a time per sector — the same shape `build.occupied` already uses one
            // level down for a slot. Checked against `next`, not the turn's opening state, so a
            // project that just started earlier in this same command batch also blocks a second
            // order at the same sector, the identical thread-through-the-loop discipline
            // `BuildResolver` already relies on for its own per-slot occupancy check.
            if (sector.ProjectId != null)
            {
                Drop(report, phase, command, "develop.already-developing:" + sector.ProjectId);
                continue;
            }

            var projectId = command.ProjectId;
            if (projectId is null || !ProjectCatalog.IsKnown(projectId))
            {
                Drop(report, phase, command, "project.unknown");
                continue;
            }

            var project = ProjectCatalog.Get(projectId);
            if (sector.LoamStock < project.CostMilli)
            {
                Drop(report, phase, command, "develop.cannot-afford");
                continue;
            }

            var sid = sector.SectorId;
            next = next with
            {
                Sectors = next.Sectors
                    .Select(s => string.Equals(s.SectorId, sid, StringComparison.Ordinal)
                        ? s with
                        {
                            LoamStock = s.LoamStock - project.CostMilli,
                            ProjectId = projectId,
                            ProjectTurnsRemaining = project.ProjectTurns
                        }
                        : s)
                    .ToList()
            };

            report.Add(phase, TurnReportKinds.Event, command.CommandId, "develop.started:" + projectId, sid);
        }

        return next;
    }

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
