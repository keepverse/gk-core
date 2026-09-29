using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Growth;

/// <summary>
/// `raise` (world-map W51, spec-sector-development.md §1): a pulse never spawns a legion by
/// itself — this spends a sector's <see cref="WorldSector.RecruitStock"/> and founds one at the
/// sector's Seat.
///
/// Resolves in `Snapshot`, right after <c>BuildResolver</c>, for the identical reason that resolver
/// states: who owns the sector, and who else is standing in it, is only decided once the rest of
/// the turn has run — so every legality check here is resolution-time, never trusted from
/// admission (<see cref="WorldCommandAdmission"/>'s own `raise` arm checks only that a sector was
/// named).
/// </summary>
public static class RaiseResolver
{
    public static WorldState Run(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, string phase, int turn)
    {
        var next = world;

        // A raise consumes the sector's own stock, so at most one can succeed per sector per turn —
        // but only if that is actually enforced here rather than merely assumed: `next` is threaded
        // through this loop and each successful raise decrements the sector's stock immediately, so
        // a stock deep enough to afford two would otherwise let a second raise against the same
        // sector also succeed, and both would derive the identical id
        // `e-{factionId}-legion-{turn}-{sectorId}` — a collision `WorldValidation`'s stable-order
        // rule rejects outright. Tracked explicitly, and only once a raise actually *succeeds* (never
        // for one merely attempted), so the "unique by construction" claim is actually true rather
        // than an assumption a large enough pulse would break.
        var raisedThisTurn = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.Raise))
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

            // Checked (never added) here: only a raise that actually *succeeds* below claims this
            // sector for the turn. A command dropped for any other reason (not yours, no seat,
            // contested, cannot afford) must not block a *different*, legal order at the same
            // sector later in this same command batch.
            if (raisedThisTurn.Contains(sector.SectorId))
            {
                Drop(report, phase, command, "raise.already-founded");
                continue;
            }

            // Re-validated here, not trusted from admission: the raising faction may have lost this
            // ground to fade or conquest later the same turn (identical reasoning to `BuildResolver`).
            if (!string.Equals(sector.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "raise.not-yours");
                continue;
            }

            var hasSeat = sector.Slots.Any(sl => sl.SlotTypeId == SlotTypeCatalog.SeatSlotTypeId);
            if (!hasSeat)
            {
                Drop(report, phase, command, "raise.no-seat");
                continue;
            }

            var hostile = next.Entities.Any(e =>
                string.Equals(e.AtSectorId, sector.SectorId, StringComparison.Ordinal)
                && !string.Equals(e.OwnerFactionId, command.CommanderId, StringComparison.Ordinal));
            if (hostile)
            {
                Drop(report, phase, command, "raise.contested");
                continue;
            }

            var raiseCost = RecruitPolicy.RaiseCostPoints;
            if (sector.RecruitStock < raiseCost)
            {
                Drop(report, phase, command, "raise.cannot-afford");
                continue;
            }

            raisedThisTurn.Add(sector.SectorId);

            // Derived from its cause, not a monotonic counter: unique by construction (the
            // `raisedThisTurn` guard above is what makes that actually true), and a replay derives
            // the identical id without any hidden state to reproduce.
            var entityId = $"e-{command.CommanderId}-legion-{turn}-{sector.SectorId}";
            var legion = FoundLegion(entityId, command.CommanderId, sector);

            var sid = sector.SectorId;
            next = next with
            {
                Entities = next.Entities
                    .Concat(new[] { legion })
                    .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                    .ToList(),
                Sectors = next.Sectors
                    .Select(s => string.Equals(s.SectorId, sid, StringComparison.Ordinal)
                        ? s with { RecruitStock = s.RecruitStock - raiseCost }
                        : s)
                    .ToList()
            };

            report.Add(phase, TurnReportKinds.Event, command.CommandId, "raise.founded:" + entityId, sid);
        }

        return next;
    }

    /// <summary>
    /// A pure constructor, no RNG — the same shape <c>LoamPhases.SpawnTheUnmade</c> already uses
    /// one module over. One Fighter member at level 1, starting with no carried loam (a fresh
    /// legion has not been given any yet — `sustain`/production is how it gets some).
    /// </summary>
    static WorldEntity FoundLegion(string entityId, string ownerFactionId, WorldSector sector) => new()
    {
        EntityId = entityId,
        Kind = WorldEntityKind.Legion,
        OwnerFactionId = ownerFactionId,
        AtSectorId = sector.SectorId,
        Stance = MovementPolicy.March,
        MovementRemaining = 0, // overwritten by Snapshot's own posture-refill pass, later this phase
        Members = new[]
        {
            new WorldEntityMember
            {
                SpeciesId = SpeciesFor(sector.Climate),
                Level = 1,
                Hp = RecruitPolicy.RaiseMemberHp,
                Role = WorldEntityMemberRole.Fighter
            }
        }
    };

    /// <summary>
    /// Which species a sector recruits is the sector's climate (spec-sector-development.md §1,
    /// world-graph-ideal.md:488) — no new selection mechanism: this reuses
    /// <see cref="CreatureSpeciesCatalog"/>'s existing <c>ElementPrimary</c> field, the same catalog
    /// <c>BannerElement.Of</c> (`Movement/LaneCost.cs`) already reads to go the other way
    /// (species → element). Deterministic, not rolled: the zombie-side species whose primary
    /// element matches, lowest by <c>SpeciesId</c> ordinal — pure, so a replay never disagrees with
    /// itself. A sector with no climate (only the homeworld) falls back to <see cref="ElementTypeId.Dark"/>
    /// — an arbitrary but documented placeholder pick for "no particular climate", the same shape
    /// every other provisional number in this module already takes (not literally the Unmade's own
    /// `normalzombie`, which is a different Dark-side species: the picked species is whichever Dark
    /// zombie sorts first, exactly like every other climate).
    /// </summary>
    static string SpeciesFor(ElementTypeId? climate)
    {
        var element = climate ?? ElementTypeId.Dark;

        var candidate = CreatureSpeciesCatalog.All
            .Where(s => string.Equals(s.Side, "zombie", StringComparison.Ordinal) && s.ElementPrimary == element)
            .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
            .FirstOrDefault();

        // Every one of the six elements has real zombie-side species in the shipped catalog
        // (verified, not assumed); `normalzombie` is a real, always-known last resort if a future
        // roster edit ever left one empty, so this never produces an entity with an unknown species.
        return candidate?.SpeciesId ?? "normalzombie";
    }

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
