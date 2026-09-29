using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>
/// Taking ground (spec-world-movement.md §Claiming). A sector falls when nothing hostile stands in
/// it and **every** slot's guard has been cleared — so a rich sector costs several turns and several
/// fights before it can be held.
///
/// Claims settle at the end of the turn, after movement and sieges, because all three of the things
/// a claim depends on — who is standing where, who is still alive, and which guards are left — are
/// decided by then. There is no separate tie-break between rival claims: two hostile forces in one
/// sector each block the other, and the battle they are already in is what decides it.
///
/// <para>⭐ `drop-tables` `sector-loot-wiring` (2026-09-07): <paramref name="Run"/>'s
/// <c>powerTuning</c> parameter is the real, confirmed call site for
/// <see cref="WorldSectorLootSource.TryResolve"/> — a claim's own ownership change, not anything
/// Battle-adjacent (`DESIGN-GATE.md`'s "Battle never grants" rule; `ClaimResolver` sits under
/// `World/Movement/`, outside Battle entirely). <b>Shipped inert-but-correct, by owner decision</b>:
/// this session found no live production caller of <see cref="Turn.TurnEngine.Step"/> at all, so
/// wiring the resolved `LootSourceRow` no further than a `TurnReportEntry` (never actually minting
/// through `LootPipeline`/`Instantiator`, which need DB-backed idempotency this DB-free `Core` class
/// cannot provide) is the correct scope until a real caller exists — the same "authored and inert
/// until X4 lands" shape `AffixChannels` already established. `powerTuning is null` (every existing
/// caller today) is byte-identical to this file's pre-2026-09-07 behavior.</para>
/// </summary>
public static class ClaimResolver
{
    public static WorldState Run(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, string phase, int turn,
        FusionRpg.Core.Power.PowerTuning? powerTuning = null,
        // `rate-authoring`'s own real, end-to-end usage: an independent, separately-streamed check
        // (mirrors D38's kill-drop roll) for a mythic claim bonus -- NEVER reweighted by the normal
        // table draw, unlike a plain `Weight`. T1/M2 (tunables-ssot.md): a rate is a balance-surface
        // number, never a bare `const` -- `gk-core/data/tuning/world-claim-loot.v1.json`'s
        // `mythicClaimBonusRatePerMillion` is the one source, this parameter is null (skip) until a
        // real caller loads and injects it, matching every other tunable in this file's own
        // `powerTuning` parameter.
        long? mythicClaimBonusRatePerMillion = null)
    {
        var next = world;

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.Claim))
        {
            var entity = next.Entities.FirstOrDefault(e =>
                string.Equals(e.EntityId, command.EntityId, StringComparison.Ordinal));

            // The legion may have died or been broken since the order was revealed — both are
            // ordinary outcomes of the same turn, not errors.
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
                Drop(report, phase, command, "claim.elsewhere");
                continue;
            }

            var sector = next.Sectors.First(s =>
                string.Equals(s.SectorId, entity.AtSectorId, StringComparison.Ordinal));

            // Claiming what your side already holds is a no-op, not a mistake — a standing order
            // that keeps firing must not fill the report with refusals.
            if (string.Equals(sector.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
            {
                report.Add(phase, TurnReportKinds.Event, command.CommandId, "claim.already-yours:" + sector.SectorId, sector.SectorId);
                continue;
            }

            if (ZoneOfControl.IsHeldAgainst(next, sector.SectorId, command.CommanderId))
            {
                Drop(report, phase, command, "claim.contested");
                continue;
            }

            var guarded = sector.Slots.FirstOrDefault(sl => sl.GuardState == GuardState.Intact);
            if (guarded is not null)
            {
                Drop(report, phase, command, "claim.guarded:" + guarded.SlotIndex);
                continue;
            }

            next = next with
            {
                Sectors = next.Sectors
                    .Select(s => string.Equals(s.SectorId, sector.SectorId, StringComparison.Ordinal)
                        ? s with
                        {
                            OwnerFactionId = command.CommanderId,
                            Phase = SectorPhase.Held,
                            LastSeenTurn = turn,
                            // Warding vs. capture (spec-loam-texture.md): the ward exempts FadePolicy
                            // only, never combat — capture ends the binding outright, no transfer to
                            // the new owner and no refund to the old one.
                            WardenBindingId = null,
                            // base-defense siege-supply §7 cost 6: slot ownership follows the sector.
                            // The board is the sector zoomed in (base-defense decision 3), so a
                            // captured sector's slots keeping the previous owner becomes VISIBLE on
                            // the board — every structure would read as the loser's. Buildings still
                            // have no ownership ON THE BOARD (decision 12, possession is by
                            // occupation); this is the world-layer fact the outcome record settles.
                            Slots = s.Slots
                                .Select(sl => sl with { OwnerFactionId = command.CommanderId })
                                .ToList()
                        }
                        : s)
                    .ToList()
            };

            report.Add(phase, TurnReportKinds.Event, command.CommandId, "claim.held:" + sector.SectorId, sector.SectorId);

            // drop-tables `sector-loot-wiring`: inert until powerTuning is supplied (see class doc).
            // Resolves to a LootSourceRow only -- never mints through LootPipeline/Instantiator, which
            // is a real DB-writing, idempotency-checked operation this DB-free Core class cannot do.
            if (powerTuning is { } tuning)
            {
                var lootRejection = WorldSectorLootSource.TryResolve(
                    sector.SectorId, sector.DangerBand, sector.TypeId, tuning, out var lootSource);
                if (lootRejection.IsOk && lootSource is not null)
                    report.Add(phase, TurnReportKinds.Event, command.CommandId,
                        "claim.loot:" + lootSource.TableId, sector.SectorId);
                // A refusal (e.g. drop.sector-band-safe on safe ground) is not an error here -- no
                // drop, no report line, matching WorldSectorLootSource.cs's own documented behavior.

                // rate-authoring's real, end-to-end usage: independent of the table draw above --
                // adding or removing any table entry never moves this roll's own odds.
                if (mythicClaimBonusRatePerMillion is { } mythicRate)
                {
                    var mythicEntry = new IndependentRateEntry("world.mythic-claim-bonus", mythicRate);
                    if (RateAuthoring.Hit(mythicEntry, unchecked((ulong)turn), "claim.mythic." + sector.SectorId))
                        report.Add(phase, TurnReportKinds.Event, command.CommandId,
                            "claim.mythic:" + sector.SectorId, sector.SectorId);
                }
            }

            // spec-loam-turn.md's settlement rule needs no enforcement — the fade *is* the
            // enforcement (ideal §8.10: barren ground can be taken but never kept). Refusing this
            // claim would delete a real play (seizing a corridor to sever an enemy's chain), so it
            // is allowed and simply warned about, naming the ground as temporary.
            if (!Habitability.For(sector))
                report.Add(phase, TurnReportKinds.Event, command.CommandId, "claim.barren:" + sector.SectorId, sector.SectorId);
        }

        return next;
    }

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
