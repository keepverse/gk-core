namespace FusionRpg.Core.World.Turn;

/// <summary>
/// The Sieges phase: attacking what defends a slot rather than what defends the ground.
///
/// A guard never stops a march (see <see cref="Movement.ZoneOfControl"/>) — it only ever fights when someone
/// orders it to. That is what makes a rich sector cost several turns and several fights before it
/// can be held, which is the intended shape of the map rather than a side effect.
/// </summary>
public static class SiegePhase
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
            if (command.Kind != WorldCommandKinds.Clear) continue;

            var entity = next.Entities.FirstOrDefault(e =>
                string.Equals(e.EntityId, command.EntityId, StringComparison.Ordinal));
            if (entity is null)
            {
                Drop(report, phase, command, "entity.unknown");
                continue;
            }

            // Standing in the sector is the whole cost of admission: you cannot clear a lair you are
            // marching past, and you certainly cannot clear one two sectors away.
            if (entity.AtSectorId is null
                || !string.Equals(entity.AtSectorId, command.SectorId, StringComparison.Ordinal))
            {
                Drop(report, phase, command, "slot.elsewhere");
                continue;
            }

            var sector = next.Sectors.FirstOrDefault(s =>
                string.Equals(s.SectorId, entity.AtSectorId, StringComparison.Ordinal));
            var slot = sector?.Slots.FirstOrDefault(sl => sl.SlotIndex == command.SlotIndex);
            if (sector is null || slot is null)
            {
                Drop(report, phase, command, "slot.unknown");
                continue;
            }

            if (slot.GuardState != GuardState.Intact)
            {
                Drop(report, phase, command, "guard.already-cleared");
                continue;
            }

            var request = new BattleRequest
            {
                BattleId = BattleKinds.IdFor(turn, BattleKinds.Guard,
                    sector.SectorId + "#" + slot.SlotIndex, entity.EntityId, null),
                Kind = BattleKinds.Guard,
                LocationId = sector.SectorId,
                TimeMilli = TurnEventQueue.TurnEndMilli,
                AttackerEntityId = entity.EntityId,
                GuardWaveId = slot.GuardWaveId,
                SlotIndex = slot.SlotIndex
            };

            next = BattleReporting.Fight(next, request, resolver, report, phase, seed);
        }

        return next;
    }

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
