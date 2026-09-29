using FusionRpg.Core.World.District;

namespace FusionRpg.Core.World.Turn;

/// <summary>
/// The Assaults phase: fighting for the legions standing in a hostile district's core
/// (base-defense-ideal.md decision 26; spec-siege-seam.md §5). Modelled on <see cref="SiegePhase"/>'s
/// own structure deliberately — same command loop, same `Drop` reason-string shape,
/// <see cref="BattleReporting.Fight"/> at the end — but a district assault is a DIFFERENT thing from
/// a guard fight: `assault` targets the ground itself (a `BattleKinds.District` fight against
/// whichever hostile force holds it), not one slot's guard. `SiegePhase.cs` is untouched by this
/// file's existence.
///
/// <para><b>This phase projects the sector, it does not generate a board.</b> Deriving a
/// `GridSpec` from the projection below is `district-layout`'s job
/// (<see cref="DistrictLayout.Build"/>), called by `siege-resolver`'s own
/// <c>DistrictAssaultResolver</c> — this phase only builds the plain-data <see cref="BoardProjection"/>
/// the seam already declares, from the sector it has already loaded locally, so the world module still
/// never learns anything about rounds, decks, or damage.</para>
/// </summary>
public static class DistrictAssaultPhase
{
    public static WorldState Run(
        WorldState world,
        IReadOnlyList<WorldCommand> commands,
        TurnReport report,
        string phase,
        int turn,
        IBattleResolver resolver,
        ulong seed)
    {
        var next = world;

        foreach (var command in commands)
        {
            if (command.Kind != WorldCommandKinds.Assault) continue;

            var entity = next.Entities.FirstOrDefault(e =>
                string.Equals(e.EntityId, command.EntityId, StringComparison.Ordinal));
            if (entity is null)
            {
                Drop(report, phase, command, "entity.unknown");
                continue;
            }

            // Standing in the sector is the whole cost of admission — the same discipline
            // SiegePhase's own `clear` order takes for a slot's guard.
            if (entity.AtSectorId is null
                || !string.Equals(entity.AtSectorId, command.SectorId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "sector.elsewhere");
                continue;
            }

            var sector = next.Sectors.FirstOrDefault(s =>
                string.Equals(s.SectorId, entity.AtSectorId, StringComparison.Ordinal));
            if (sector is null)
            {
                Drop(report, phase, command, "sector.unknown");
                continue;
            }

            // Assaulting ground this faction already holds is not a battle — "the attacker cannot
            // reach the defender" is not a difficulty setting, and neither is "there is no defender
            // to reach."
            if (string.Equals(sector.OwnerFactionId, entity.OwnerFactionId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "sector.already-yours");
                continue;
            }

            // The defender, if any: a hostile force standing in the same sector. An unopposed
            // assault (nobody defending) still fights — BattleReporting.Fight already resolves a
            // one-sided combatant list without a defender entity, the same way a Sector-kind contact
            // battle does when the other side has already died earlier in the turn.
            var defender = next.Entities.FirstOrDefault(e =>
                string.Equals(e.AtSectorId, sector.SectorId, StringComparison.Ordinal)
                && !string.Equals(e.OwnerFactionId, entity.OwnerFactionId, StringComparison.Ordinal));

            var request = new BattleRequest
            {
                BattleId = BattleKinds.IdFor(turn, BattleKinds.District, sector.SectorId, entity.EntityId, defender?.EntityId),
                Kind = BattleKinds.District,
                LocationId = sector.SectorId,
                TimeMilli = TurnEventQueue.TurnEndMilli,
                AttackerEntityId = entity.EntityId,
                DefenderEntityId = defender?.EntityId,
                DefenderStationary = defender is not null,
                Board = BuildBoard(next, sector, entity, seed),
            };

            next = BattleReporting.Fight(next, request, resolver, report, phase, seed);
        }

        return next;
    }

    /// <summary>
    /// The <see cref="BoardProjection"/> for a district assault against <paramref name="sector"/>,
    /// entered by <paramref name="attacker"/>. Widened to `internal` (siege-engagement, 2026-09-06) so
    /// `MovementPhase`'s own contact loop can build the SAME projection for a CONTINUING siege — one
    /// that fires with no fresh `assault` order — rather than re-deriving this construction a second
    /// time. Never call this for a sector <paramref name="attacker"/> already owns; the caller is
    /// responsible for that check (this method has no ownership opinion of its own).
    /// </summary>
    internal static BoardProjection BuildBoard(WorldState world, WorldSector sector, WorldEntity attacker, ulong seed) =>
        new()
        {
            SectorId = sector.SectorId,
            WorldSeed = seed,
            SectorTypeId = sector.TypeId,
            DevelopmentLevel = sector.DevelopmentLevel,
            RubbleStock = sector.RubbleStock,
            IronworkStock = sector.IronworkStock,
            AttackerEdge = DistrictLayout.EntryEdgeFor(world, attacker, sector.SectorId),
            WardLevel = DistrictLayout.WardLevelFor(world, attacker),
            Slots = sector.Slots
                .Select(slot => new SlotProjection
                {
                    SlotIndex = slot.SlotIndex,
                    SlotTypeId = slot.SlotTypeId,
                    StructureId = slot.StructureId,
                    OwnerFactionId = slot.OwnerFactionId,
                    State = slot.State,
                    StructureHp = slot.StructureHp,
                })
                .ToList(),
        };

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
