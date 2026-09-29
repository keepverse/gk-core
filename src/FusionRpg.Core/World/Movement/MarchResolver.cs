using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>Why an order could not be carried out. Empty means it was fine.</summary>
public readonly record struct MarchRefusal(string Reason);

/// <summary>
/// Marching: spend a legion's budget along its ordered lane path, stopping wherever the budget runs
/// out so the next turn resumes from exactly there.
///
/// Pure over the world; the turn engine applies the results and reports them.
/// </summary>
public static class MarchResolver
{
    /// <summary>
    /// Checks an order against the world as it stands *now*. Submission already admitted it; the
    /// world may have moved since, so contiguity and lane state are re-checked at reveal.
    /// </summary>
    public static MarchRefusal? Validate(WorldState world, WorldEntity entity, IReadOnlyList<string> lanePath)
    {
        if (lanePath.Count == 0) return new MarchRefusal("path.empty");

        var standingAt = entity.AtSectorId;
        var remaining = lanePath;

        if (standingAt is null)
        {
            // Mid-march. A standing order is re-issued whole each turn, so the lane the legion is
            // already on may sit anywhere in the path — resume from there rather than refusing the
            // order the player never changed.
            var current = world.Lanes.FirstOrDefault(l => l.LaneId == entity.OnLaneId);
            if (current is null) return new MarchRefusal("lane.unknown");

            var index = lanePath.ToList().FindIndex(id => string.Equals(id, current.LaneId, StringComparison.Ordinal));
            if (index < 0) return new MarchRefusal("path.not-contiguous");

            remaining = lanePath.Skip(index).ToList();

            // Walk the rest of the path from the end being marched toward — not from the lane's
            // stored `from`, which is only one of the two ways this lane gets travelled.
            if (entity.OnLaneTowardSectorId is not { } heading) return new MarchRefusal("lane.no-heading");
            standingAt = Other(current, heading);
            if (standingAt is null) return new MarchRefusal("lane.no-heading");
        }

        var at = standingAt;
        foreach (var laneId in remaining)
        {
            var lane = world.Lanes.FirstOrDefault(l => l.LaneId == laneId);
            if (lane is null) return new MarchRefusal("lane.unknown");
            if (lane.State == LaneState.Severed) return new MarchRefusal("lane.severed");

            var next = Other(lane, at);
            if (next is null) return new MarchRefusal("path.not-contiguous");

            var type = LaneTypeCatalog.Get(lane.TypeId);
            if (LaneGate.Refusal(type, lane, at) is { } refusal) return refusal;

            at = next;
        }

        return null;
    }

    /// <summary>
    /// Walks the path with the budget available, returning where the legion ends up and how far it
    /// got through the turn (per-mille) — the timestamp its arrival event carries.
    /// </summary>
    public static MarchOutcome March(WorldState world, WorldEntity entity, IReadOnlyList<string> lanePath)
    {
        var banner = BannerElement.Of(entity);
        var budget = entity.MovementRemaining;
        var spent = 0;

        var atSector = entity.AtSectorId;
        var startedOnLane = entity.OnLaneId;
        var startedProgress = entity.LaneProgressMilli;
        var heading = entity.OnLaneTowardSectorId;
        var arrivedAt = (string?)null;
        var arrivedViaLaneId = (string?)null;

        string? onLane = null;
        var progress = 0;
        var halted = false;

        // Every sector the legion actually set foot in. Where it *went* is not recoverable from
        // where it ended up, and world-intel needs it: marching clean through somewhere and
        // reporting nothing about it is absurd.
        var visited = new List<string>();

        // Resume from the lane already under foot (see Validate) rather than restarting the path.
        var path = lanePath;
        if (atSector is null && startedOnLane is { } currentLaneId)
        {
            var index = path.ToList().FindIndex(id => string.Equals(id, currentLaneId, StringComparison.Ordinal));
            if (index > 0) path = path.Skip(index).ToList();

            var current = world.Lanes.First(l => l.LaneId == currentLaneId);
            atSector = Other(current, heading!); // the end it left, so the walk below has a frame
        }

        foreach (var laneId in path)
        {
            var lane = world.Lanes.First(l => l.LaneId == laneId);
            var cost = LaneCost.For(world, lane, banner);
            var towards = Other(lane, atSector!);

            // Ground already covered counts only on the lane the legion was actually standing on.
            var covered = string.Equals(laneId, startedOnLane, StringComparison.Ordinal) ? startedProgress : 0;
            var remainingCost = (int)((long)cost * (1000 - covered) / 1000);

            if (budget - spent >= remainingCost)
            {
                spent += remainingCost;
                atSector = towards;
                arrivedAt = towards;
                arrivedViaLaneId = laneId;
                onLane = null;
                progress = 0;
                if (towards != null) visited.Add(towards);

                // Zone of control: walking *into* a hostile-held sector ends the march there. The
                // check is on arrival only, so a legion that starts its turn surrounded may still
                // leave, and a guarded-but-unoccupied sector never stops anyone.
                if (ZoneOfControl.IsHeldAgainst(world, towards!, entity.OwnerFactionId))
                {
                    halted = true;
                    break;
                }

                continue;
            }

            // Out of budget partway along: stop here, remembering both how far and which way.
            var affordable = budget - spent;
            var step = cost == 0 ? 1000 : (int)((long)affordable * 1000 / cost);
            onLane = laneId;
            heading = towards;
            progress = Math.Clamp(covered + step, 0, 999);
            atSector = null;
            spent = budget;
            break;
        }

        var timeMilli = budget <= 0 ? 0 : (int)Math.Clamp((long)spent * 1000 / budget, 0, 1000);
        return new MarchOutcome(
            atSector, onLane, onLane is null ? null : heading, progress, budget - spent, timeMilli,
            arrivedAt, halted, visited, arrivedViaLaneId);
    }

    /// <summary>
    /// The first stretch of lane a force will be on this turn — which lane, which way, from where,
    /// and how much of the lane it covers in a whole turn. That is everything two legions closing on
    /// one lane need in order to work out where they meet, and nothing else needs it, so it is a
    /// small read rather than a second march.
    ///
    /// Null when the force is standing in a sector with nowhere to go.
    /// </summary>
    public static LaneStep? FirstLaneStep(WorldState world, WorldEntity entity, IReadOnlyList<string>? lanePath)
    {
        string laneId;
        string? towards;
        int progress;

        if (entity.OnLaneId is { } current)
        {
            // Already on a lane: it keeps its heading whether or not an order came in this turn.
            laneId = current;
            towards = entity.OnLaneTowardSectorId;
            progress = entity.LaneProgressMilli;
        }
        else
        {
            if (lanePath is not { Count: > 0 }) return null;
            laneId = lanePath[0];
            var first = world.Lanes.FirstOrDefault(l => l.LaneId == laneId);
            if (first is null) return null;
            towards = Other(first, entity.AtSectorId);
            progress = 0;
        }

        if (towards is null) return null;

        var lane = world.Lanes.FirstOrDefault(l => l.LaneId == laneId);
        if (lane is null) return null;

        // No order means no movement — a force parked on a lane still occupies it, at zero speed.
        var speed = 0;
        if (lanePath is { Count: > 0 } && lanePath.Contains(laneId, StringComparer.Ordinal))
        {
            var cost = LaneCost.For(world, lane, BannerElement.Of(entity));
            speed = cost <= 0
                ? LaneCrossing.LaneLengthMilli
                : (int)Math.Min(int.MaxValue, (long)entity.MovementRemaining * 1000 / cost);
        }

        return new LaneStep(laneId, towards, progress, speed);
    }

    static string? Other(WorldLane lane, string? from) =>
        string.Equals(lane.FromSectorId, from, StringComparison.Ordinal) ? lane.ToSectorId
        : string.Equals(lane.ToSectorId, from, StringComparison.Ordinal) ? lane.FromSectorId
        : null;
}

/// <summary>Where a march ended, which way it was pointing, and when — in turn per-mille.</summary>
/// <param name="ArrivedViaLaneId">
/// The last lane walked to reach <see cref="ArrivedAtSectorId"/>, or null when the march never
/// completed a hop this turn. Movement-phase-local only — never stored on <see cref="WorldEntity"/>
/// — it exists solely so a same-turn Sector-kind contact fight that routs this force has somewhere to
/// fall back to (<see cref="Turn.BattleApplication"/>'s own fall-back logic, world-map, 2026-09-05).
/// </param>
public readonly record struct MarchOutcome(
    string? AtSectorId, string? OnLaneId, string? OnLaneTowardSectorId, int LaneProgressMilli,
    int MovementRemaining, int TimeMilli, string? ArrivedAtSectorId, bool HaltedByZoneOfControl,
    IReadOnlyList<string> VisitedSectorIds, string? ArrivedViaLaneId = null);

/// <summary>
/// One force's occupancy of one lane this turn: where along it, which end it faces, and how much of
/// the lane a full turn buys. Positions are per-mille measured from the end it started at.
/// </summary>
public readonly record struct LaneStep(string LaneId, string TowardSectorId, int ProgressMilli, int SpeedMilli);
