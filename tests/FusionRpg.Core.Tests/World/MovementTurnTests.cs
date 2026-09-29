using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W9 (spec-world-movement.md): the `move` order through a turn — marching, resuming across turns,
/// and being reported rather than thrown when it has gone stale.
/// </summary>
public class MovementTurnTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand Move(string entityId, params string[] lanePath) => new()
    {
        CommanderId = "dave",
        CommandId = "m1",
        Kind = WorldCommandKinds.Move,
        EntityId = entityId,
        LanePath = lanePath
    };

    static WorldEntity Legion(WorldState w) => w.Entities.Single(e => e.EntityId == "e-dave-legion-1");

    [Fact]
    public void A_legion_ordered_down_one_lane_arrives_and_the_report_says_so()
    {
        // l-home-ember is a corridor of length 800 — inside one turn's budget.
        var result = TurnEngine.Step(World(), new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);

        var legion = Legion(result.World);
        Assert.Equal("ember-hollow", legion.AtSectorId);
        Assert.Null(legion.OnLaneId);
        Assert.Equal(0, legion.LaneProgressMilli);
        Assert.Contains(result.Report.Entries,
            e => e.Phase == TurnEngine.Phases.Movement && e.Subject == "e-dave-legion-1" && e.Detail.Contains("ember-hollow"));
    }

    [Fact]
    public void A_march_too_long_for_one_turn_stops_on_the_lane_and_resumes_exactly()
    {
        // Two lanes back to back cost more than a turn's budget.
        var path = new[] { "l-home-ember", "l-ember-ash" };

        var first = TurnEngine.Step(World(), new[] { Move("e-dave-legion-1", path) }, seed: 1);
        var midway = Legion(first.World);
        Assert.NotNull(midway.OnLaneId);
        Assert.InRange(midway.LaneProgressMilli, 1, 999);

        // The order stands: next turn it carries on from exactly where it stopped.
        var second = TurnEngine.Step(first.World, new[] { Move("e-dave-legion-1", path) }, seed: 1);
        var later = Legion(second.World);

        var advanced = later.OnLaneId == null || later.LaneProgressMilli > midway.LaneProgressMilli;
        Assert.True(advanced, "the second turn must continue the march, not restart it");
    }

    /// <summary>
    /// The contract the map view routes against: a legion caught mid-lane may be re-routed, but the
    /// new path has to carry the lane it is already on at the head. Without that the order looks
    /// perfectly legal to a client and is dropped when the turn resolves.
    /// </summary>
    [Fact]
    public void A_mid_march_legion_can_be_re_routed_as_long_as_the_path_keeps_the_lane_it_is_on()
    {
        // Turn one crosses the corridor and leaves it partway down the ley lane beyond it.
        var first = TurnEngine.Step(World(),
            new[] { Move("e-dave-legion-1", "l-home-ember", "l-ember-ash") }, seed: 1);
        var midway = Legion(first.World);
        Assert.Equal("l-ember-ash", midway.OnLaneId);
        Assert.Equal("ash-waste", midway.OnLaneTowardSectorId);

        // A new destination, routed the way the map view builds it: current lane, then onward.
        var accepted = TurnEngine.Step(first.World,
            new[] { Move("e-dave-legion-1", "l-ember-ash", "l-ash-black") }, seed: 1);
        Assert.Empty(accepted.Report.Dropped);

        // The same destination without the lane under its feet is what gets refused.
        var refused = TurnEngine.Step(first.World,
            new[] { Move("e-dave-legion-1", "l-ash-black") }, seed: 1);
        Assert.Equal("path.not-contiguous", refused.Report.Dropped.Single().Detail);
    }

    [Fact]
    public void Movement_never_goes_negative_or_outruns_the_budget()
    {
        var world = World();
        var result = TurnEngine.Step(world, new[] { Move("e-dave-legion-1", "l-home-ember", "l-ember-ash") }, seed: 1);

        foreach (var e in result.World.Entities)
        {
            Assert.InRange(e.MovementRemaining, 0, MovementPolicy.PointsPerTurn);
            Assert.InRange(e.LaneProgressMilli, 0, 1000);
        }
    }

    /// <summary>
    /// Since W23 the refill reads each legion's posture. `first-light` authors the wild pack as a
    /// garrison — wave 1 ignored that and handed it a full budget anyway.
    /// </summary>
    [Fact]
    public void Every_legion_starts_the_next_turn_with_the_budget_its_posture_allows()
    {
        var result = TurnEngine.Step(World(), new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);

        Assert.All(result.World.Entities,
            e => Assert.Equal(MovementPolicy.BudgetFor(e.Stance), e.MovementRemaining));
        Assert.Equal(MovementPolicy.PointsPerTurn, Legion(result.World).MovementRemaining);
        // Hold-allowance (spec-hold-allowance.md §Design 1+5): the ash-waste garrison refills to
        // the tuned allowance, not 0 — same `BudgetFor` the line above already asserts per stance.
        Assert.Equal(WorldTuningHub.Tuning.Movement.HoldAllowanceMilli,
            result.World.Entities.Single(e => e.EntityId == "e-wild-pack-1").MovementRemaining);
    }

    [Fact]
    public void A_path_that_does_not_start_where_the_legion_stands_is_dropped_with_a_reason()
    {
        // l-ash-black is nowhere near the homeworld.
        var result = TurnEngine.Step(World(), new[] { Move("e-dave-legion-1", "l-ash-black") }, seed: 1);

        var dropped = Assert.Single(result.Report.Dropped);
        Assert.Equal("path.not-contiguous", dropped.Detail);
        Assert.Equal("homeworld", Legion(result.World).AtSectorId); // it did not budge
    }

    [Fact]
    public void A_path_that_jumps_between_unconnected_lanes_is_dropped()
    {
        var result = TurnEngine.Step(World(),
            new[] { Move("e-dave-legion-1", "l-home-ember", "l-frost-ash") }, seed: 1);

        Assert.Single(result.Report.Dropped);
        Assert.Equal("path.not-contiguous", result.Report.Dropped.Single().Detail);
    }

    [Fact]
    public void A_severed_lane_cannot_be_marched()
    {
        var world = World();
        var severed = world with
        {
            Lanes = world.Lanes
                .Select(l => l.LaneId == "l-home-ember" ? l with { State = LaneState.Severed } : l)
                .ToList()
        };

        var result = TurnEngine.Step(severed, new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);
        Assert.Equal("lane.severed", result.Report.Dropped.Single().Detail);
    }

    [Fact]
    public void Marching_is_deterministic_and_independent_of_command_order()
    {
        var commands = new[]
        {
            Move("e-dave-legion-1", "l-home-ember"),
            new WorldCommand { CommanderId = "wild", CommandId = "w1", Kind = WorldCommandKinds.StandFast }
        };

        var forward = TurnEngine.Step(World(), commands, seed: 5);
        var reversed = TurnEngine.Step(World(), commands.Reverse().ToArray(), seed: 5);

        Assert.Equal(forward.StateHash, reversed.StateHash);
    }
}
