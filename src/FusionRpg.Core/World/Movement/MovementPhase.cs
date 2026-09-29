using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>
/// The Movement phase, end to end (spec-world-movement.md). Orders are re-checked, marches are
/// walked, head-on meetings on a lane are solved before anyone commits to a position, and whatever
/// contact that leaves is fought.
///
/// It lives here rather than in <see cref="TurnEngine"/> so the engine stays a readable list of
/// phases: the engine owns the *order* things happen in, this file owns what movement means.
///
/// Two bounds keep the turn replayable: everything is walked in ordinal entity-id order, and there
/// is at most one battle per lane and per sector per turn.
/// </summary>
/// <summary>
/// What the movement phase produced: the new world, and every sector each faction set foot in along
/// the way. The second part exists because a legion's route is not recoverable from its destination,
/// and world-intel needs it.
/// </summary>
public sealed record MovementResult(
    WorldState World, IReadOnlyDictionary<string, IReadOnlySet<string>> VisitedByFaction);

public static class MovementPhase
{
    public static MovementResult Run(
        WorldState world,
        IReadOnlyList<WorldCommand> commands,
        TurnReport report,
        string phase,
        int turn,
        IBattleResolver resolver,
        ulong seed)
    {
        var orders = commands
            .Where(c => c.Kind == WorldCommandKinds.Move && c.EntityId != null)
            .GroupBy(c => c.EntityId!, StringComparer.Ordinal)
            // A commander who files two marches for one legion gets the later order. Reveal has
            // already sorted by (commander, command id), so "later" is a stable, replayable choice.
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entity in world.Entities)
        {
            if (!orders.TryGetValue(entity.EntityId, out var order)) continue;

            var refusal = MarchResolver.Validate(world, entity, order.LanePath);
            if (refusal is { } why)
            {
                report.Add(phase, TurnReportKinds.CommandDropped, order.CommandId, why.Reason);
                continue;
            }

            paths[entity.EntityId] = order.LanePath;
        }

        // Who is on which lane this turn, and how fast. Solving crossings *before* anyone commits to
        // a position is the whole point: two legions closing head-on must agree on where they met,
        // and that answer cannot depend on which of them was walked first.
        var steps = new Dictionary<string, LaneStep>(StringComparer.Ordinal);
        foreach (var entity in world.Entities)
        {
            if (!ZoneOfControl.Projects(entity)) continue;
            paths.TryGetValue(entity.EntityId, out var path);
            if (entity.OnLaneId is null && path is null) continue;
            if (MarchResolver.FirstLaneStep(world, entity, path) is { } step)
                steps[entity.EntityId] = step;
        }

        var crossings = ContactResolver.LaneContacts(world, steps);
        var stoppedByCrossing = new Dictionary<string, (LaneContact Contact, bool IsA)>(StringComparer.Ordinal);
        foreach (var crossing in crossings)
        {
            stoppedByCrossing[crossing.EntityA] = (crossing, true);
            stoppedByCrossing[crossing.EntityB] = (crossing, false);
        }

        var queue = new TurnEventQueue();
        var requests = new Dictionary<string, BattleRequest>(StringComparer.Ordinal);
        var moved = new Dictionary<string, WorldEntity>(StringComparer.Ordinal);

        // A force that fully arrives this turn has its `OnLaneId` cleared the instant it lands, so if
        // a Sector-kind contact fight right there routs it, `BattleApplication` would otherwise find
        // "nothing to fall back from" and treat a freshly-arrived attacker exactly like a genuine
        // long-standing garrison. This is movement-phase-local (never a `WorldEntity` field, never
        // hashed) precisely because it only needs to survive from here down to `BattleReporting.Fight`
        // within this same call.
        var arrivedViaLane = new Dictionary<string, string>(StringComparer.Ordinal);
        // Concrete on the way in, read-only on the way out. Declaring this as the interface and
        // casting back to mutate it would be a runtime failure waiting for someone to change the
        // collection type.
        var visited = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var entity in world.Entities)
        {
            if (stoppedByCrossing.TryGetValue(entity.EntityId, out var met))
            {
                moved[entity.EntityId] = StopAtCrossing(world, entity, steps[entity.EntityId], met.Contact, met.IsA);
                continue;
            }

            if (!paths.TryGetValue(entity.EntityId, out var path)) continue;

            var outcome = MarchResolver.March(world, entity, path);

            // The march-loam gate's soft half (spec-loam-ai.md): pure reporting over already-carried
            // state, alongside every admitted march order — not a supply-connectivity simulation, just
            // "at your current burn rate, here is your ceiling," the same honesty LeashTurns already
            // gives a legion that never leaves supply at all.
            if (LegionSupply.TurnsUntilExhausted(entity) is { } turnsLeft)
                // world-stage W13 (fog defect B): `outcome.OnLaneId` is a lane id, never a sector —
                // null here (not the lane) when the legion ends its march mid-lane. A dynamic fact
                // about the viewer's own force, so it carries its own audience.
                report.Add(phase, TurnReportKinds.Event, entity.EntityId,
                    "legion.runway:" + (turn + turnsLeft), sectorId: outcome.AtSectorId, audience: entity.OwnerFactionId);

            if (outcome.VisitedSectorIds.Count > 0)
            {
                if (!visited.TryGetValue(entity.OwnerFactionId, out var seen))
                    visited[entity.OwnerFactionId] = seen = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var sectorId in outcome.VisitedSectorIds) seen.Add(sectorId);
            }

            moved[entity.EntityId] = entity with
            {
                AtSectorId = outcome.AtSectorId,
                OnLaneId = outcome.OnLaneId,
                OnLaneTowardSectorId = outcome.OnLaneTowardSectorId,
                LaneProgressMilli = outcome.LaneProgressMilli,
                MovementRemaining = outcome.MovementRemaining
            };

            if (outcome.ArrivedAtSectorId is not null && outcome.ArrivedViaLaneId is { } arrivalLane)
                arrivedViaLane[entity.EntityId] = arrivalLane;

            queue.Schedule(outcome.TimeMilli, entity.EntityId, TurnEventKinds.Arrival,
                outcome.ArrivedAtSectorId ?? outcome.OnLaneId ?? "");

            if (outcome.HaltedByZoneOfControl)
                queue.Schedule(outcome.TimeMilli, entity.EntityId, TurnEventKinds.Halt,
                    "zoc:" + outcome.AtSectorId);
        }

        foreach (var crossing in crossings)
        {
            // Both sides are marching, so neither of them is defending anything.
            var request = new BattleRequest
            {
                BattleId = BattleKinds.IdFor(turn, BattleKinds.Lane, crossing.LaneId, crossing.EntityA, crossing.EntityB),
                Kind = BattleKinds.Lane,
                LocationId = crossing.LaneId,
                TimeMilli = crossing.TimeMilli,
                AttackerEntityId = crossing.EntityA,
                DefenderEntityId = crossing.EntityB
            };

            requests[request.BattleId] = request;
            queue.Schedule(crossing.TimeMilli, crossing.EntityA, TurnEventKinds.Crossing, request.BattleId);
        }

        var next = moved.Count == 0
            ? world
            : world with
            {
                Entities = world.Entities
                    .Select(e => moved.TryGetValue(e.EntityId, out var updated) ? updated : e)
                    .ToList()
            };

        // A force is a defender only if it never budged, so the set is computed from the positions
        // themselves rather than from who happened to file an order.
        var movedIds = world.Entities
            .Where(before => moved.TryGetValue(before.EntityId, out var after) && ChangedPosition(before, after))
            .Select(e => e.EntityId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var contact in ContactResolver.SectorContacts(next, movedIds))
        {
            var request = BuildContactRequest(next, turn, seed, contact);
            requests[request.BattleId] = request;
            queue.Schedule(TurnEventQueue.TurnEndMilli, contact.AttackerEntityId,
                TurnEventKinds.Contact, request.BattleId);
        }

        // Replaying the turn in the order it actually happened — which is why the report reads as a
        // story rather than as the order the orders were filed in.
        while (queue.TryDequeue(out var evt))
        {
            if (evt.Kind is TurnEventKinds.Contact or TurnEventKinds.Crossing
                && requests.TryGetValue(evt.Detail, out var request))
            {
                next = BattleReporting.Fight(next, request, resolver, report, phase, seed, arrivedViaLane);
                continue;
            }

            // world-stage W13 (fog defect B): `evt.Detail` is free text — an `Arrival` mid-lane
            // carries a lane id there, and a `Halt`'s is `"zoc:" + sectorId`, never a bare sector id
            // either way. Neither belongs in the structured sector slot (`Believed` on either
            // returns null, so the line vanished for everybody). Only `Arrival`/`Halt` ever reach
            // here (`Contact`/`Crossing` are handled above), and both are scheduled only after
            // `moved[entity.EntityId]` is set, so the entity's own post-march position is the real
            // answer: a sector when it is standing in one, null when it is not. A dynamic fact about
            // a specific legion, so it carries that legion's own audience.
            var mover = moved.TryGetValue(evt.EntityId, out var m) ? m : null;
            report.Add(phase, TurnReportKinds.Event, evt.EntityId, evt.Kind + ":" + evt.Detail,
                sectorId: mover?.AtSectorId, audience: mover?.OwnerFactionId);
        }

        return new MovementResult(
            next,
            visited.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// base-defense `siege-engagement`'s own named, deferred gap, closed 2026-09-06: a CONTINUING
    /// siege (turn 2+, no fresh `assault` order) reached this generic sector-contact path, which
    /// always built a <see cref="BattleKinds.Sector"/> request — <see cref="Turn.DistrictAssaultResolver"/>'s
    /// own delegation guard then correctly (per its own contract) routed that away to the placeholder
    /// resolver, so a siege silently stopped being a real board fight the moment its attacker held
    /// still for even one turn. Fixed by building the SAME <see cref="Turn.BattleKinds.District"/>
    /// request <see cref="Turn.DistrictAssaultPhase"/> already builds for a fresh order, whenever the
    /// contact sector is currently under siege (<see cref="Turn.SiegeEngagement.IsUnderSiege"/>) —
    /// reusing <see cref="Turn.DistrictAssaultPhase.BuildBoard"/> verbatim rather than re-deriving the
    /// projection a second time. Every OTHER sector (unowned, or owned but not besieged — the whole
    /// existing open-field game) is completely untouched: `IsUnderSiege` requires an owner and a
    /// hostile presence, which is exactly what <see cref="ContactResolver.SectorContacts"/> already
    /// required to yield this contact at all, so this is a pure re-routing, never a new trigger
    /// condition.
    ///
    /// <para><b>Attacker/defender here means "who does not own the ground" vs "who does"</b> — NOT
    /// <paramref name="contact"/>'s own movement-based split (which answers "who moved this turn,"
    /// a different question `Sector`-kind battles use for their own cover/stance framing, but the
    /// wrong one for which side is the Core-holder in a district fight — the same distinction
    /// <see cref="Turn.DistrictAssaultPhase.Run"/> already draws from its own `assault` command's
    /// issuer). For a genuinely continuing siege neither side moved, so this recomputes the roles
    /// from ownership instead of trusting a movement split that would otherwise be arbitrary
    /// (`ContactResolver.FirstHostilePair`'s own first-in-ordinal-order tie-break, not a meaningful
    /// attacker signal when nobody moved).</para>
    /// </summary>
    static BattleRequest BuildContactRequest(WorldState world, int turn, ulong seed, SectorContact contact)
    {
        var sector = world.Sectors.FirstOrDefault(s =>
            string.Equals(s.SectorId, contact.SectorId, StringComparison.Ordinal));

        if (sector is not null && Turn.SiegeEngagement.IsUnderSiege(world, contact.SectorId))
        {
            var a = world.Entities.FirstOrDefault(e => string.Equals(e.EntityId, contact.AttackerEntityId, StringComparison.Ordinal));
            var b = world.Entities.FirstOrDefault(e => string.Equals(e.EntityId, contact.DefenderEntityId, StringComparison.Ordinal));

            // One of the pair owns this ground (sector.OwnerFactionId) and the other does not --
            // IsUnderSiege already proved a hostile force is present, so exactly one of a/b is that
            // intruder regardless of which one ContactResolver happened to call "attacker".
            var (attacker, defender) =
                a is not null && !string.Equals(a.OwnerFactionId, sector.OwnerFactionId, StringComparison.Ordinal)
                    ? (a, b)
                    : (b, a);

            if (attacker is not null)
            {
                return new BattleRequest
                {
                    BattleId = BattleKinds.IdFor(turn, BattleKinds.District, sector.SectorId,
                        attacker.EntityId, defender?.EntityId),
                    Kind = BattleKinds.District,
                    LocationId = sector.SectorId,
                    TimeMilli = TurnEventQueue.TurnEndMilli,
                    AttackerEntityId = attacker.EntityId,
                    DefenderEntityId = defender?.EntityId,
                    DefenderStationary = defender is not null,
                    Board = Turn.DistrictAssaultPhase.BuildBoard(world, sector, attacker, seed),
                };
            }
        }

        return new BattleRequest
        {
            BattleId = BattleKinds.IdFor(turn, BattleKinds.Sector, contact.SectorId,
                contact.AttackerEntityId, contact.DefenderEntityId),
            Kind = BattleKinds.Sector,
            LocationId = contact.SectorId,
            TimeMilli = TurnEventQueue.TurnEndMilli,
            AttackerEntityId = contact.AttackerEntityId,
            DefenderEntityId = contact.DefenderEntityId,
            DefenderStationary = contact.DefenderStationary
        };
    }

    /// <summary>
    /// Freezes a legion at the exact point it met someone. Its progress is measured from its own
    /// end, so the two sides land on complementary positions and the lane still adds up to 1000.
    /// </summary>
    static WorldEntity StopAtCrossing(
        WorldState world, WorldEntity entity, LaneStep step, LaneContact contact, bool isA)
    {
        var stopAt = Math.Clamp(
            isA ? contact.PositionMilliFromA : LaneCrossing.LaneLengthMilli - contact.PositionMilliFromA,
            1, 999);

        var lane = world.Lanes.First(l => l.LaneId == step.LaneId);
        var cost = LaneCost.For(world, lane, BannerElement.Of(entity));
        var spent = (int)((long)cost * Math.Max(0, stopAt - step.ProgressMilli) / 1000);

        return entity with
        {
            AtSectorId = null,
            OnLaneId = step.LaneId,
            OnLaneTowardSectorId = step.TowardSectorId,
            LaneProgressMilli = stopAt,
            MovementRemaining = Math.Max(0, entity.MovementRemaining - spent)
        };
    }

    static bool ChangedPosition(WorldEntity before, WorldEntity after) =>
        !string.Equals(before.AtSectorId, after.AtSectorId, StringComparison.Ordinal)
        || !string.Equals(before.OnLaneId, after.OnLaneId, StringComparison.Ordinal)
        || before.LaneProgressMilli != after.LaneProgressMilli;

}
