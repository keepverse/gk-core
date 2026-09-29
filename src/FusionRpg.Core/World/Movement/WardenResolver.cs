using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>
/// Binding a warden onto ground (spec-loam-texture.md's Wardens; world-stage W28). Resolves in
/// `Snapshot`, right after `Build`, for the same reason `Claim` and `Build` both settle there:
/// ownership is only decided once the rest of the turn has run, and a binding filed against ground
/// this faction lost the same turn must not stick. Running after `Claim` lets a claim and a
/// `bind-warden` land in the same turn, the same convenience `Build` already gets.
///
/// No entity: `bind-warden` names a sector, not a legion.
///
/// <para><b>Retired verb (warden-freeze-fix, RulesetVersion 13).</b> `WorldCommandAdmission` now
/// refuses every `bind-warden` with `warden.retired`, and `TurnEngine`'s Reveal re-admits every
/// order, so no order reaches this resolver through <c>TurnEngine.Step</c> any more. It stays,
/// unchanged, as the `WorldCommand` reconciliation seam warden-mortality-ideal.md names for a
/// future warden-shaped mechanic, and a binding it wrote before the retirement stays on the sector
/// as data with no stability effect.</para>
/// </summary>
public static class WardenResolver
{
    public static WorldState Run(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, string phase)
    {
        var next = world;

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.BindWarden))
        {
            var sector = next.Sectors.FirstOrDefault(s =>
                string.Equals(s.SectorId, command.SectorId, StringComparison.Ordinal));

            if (sector is null)
            {
                Drop(report, phase, command, "sector.gone");
                continue;
            }

            // Re-validated here, not trusted from admission: the binder may have lost this ground to
            // fade or conquest later the same turn.
            if (!string.Equals(sector.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "warden.not-yours");
                continue;
            }

            var sectorId = sector.SectorId;
            var wardenId = command.WardenId!;

            next = next with
            {
                Sectors = next.Sectors
                    .Select(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal)
                        ? s with { WardenBindingId = wardenId }
                        : s)
                    .ToList()
            };

            report.Add(phase, TurnReportKinds.Event, command.CommandId, "warden.bound:" + wardenId, sectorId);
        }

        return next;
    }

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
