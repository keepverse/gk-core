using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Review findings (2026-08-22): a legion stopped mid-lane must remember which way it was going,
/// and a legion standing in a sector must not carry stale lane progress.
/// </summary>
public class MarchDirectionTests
{
    /// <summary>
    /// The template digs the wild pack in, and since W23 a garrison's march orders are refused. This
    /// test is about *direction*, so it puts the pack on the road — and changes nothing else, so the
    /// budget it starts with is still the template's.
    /// </summary>
    static WorldState World()
    {
        var w = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        return w with
        {
            Entities = w.Entities
                .Select(e => e.EntityId == "e-wild-pack-1"
                    ? e with { Stance = "march" }
                    : e)
                .ToList()
        };
    }

    static WorldCommand Move(string entityId, params string[] lanePath) => new()
    {
        CommanderId = "wild",
        CommandId = "m1",
        Kind = WorldCommandKinds.Move,
        EntityId = entityId,
        LanePath = lanePath
    };

    /// <summary>
    /// l-ember-ash runs ember-hollow → ash-waste. The wild pack starts at ash-waste, so marching it
    /// down that lane means travelling against the lane's stored direction.
    /// </summary>
    [Fact]
    public void A_march_against_the_lanes_direction_does_not_reverse_when_the_turn_runs_out()
    {
        var world = World();

        // Two lanes of travel from ash-waste, so the budget certainly runs out mid-lane.
        var first = TurnEngine.Step(world, new[] { Move("e-wild-pack-1", "l-ember-ash", "l-home-ember") }, seed: 1);
        var midway = first.World.Entities.Single(e => e.EntityId == "e-wild-pack-1");

        Assert.Equal("l-ember-ash", midway.OnLaneId);
        Assert.Equal("ember-hollow", midway.OnLaneTowardSectorId); // it is heading *away* from ash-waste

        // Carrying on next turn must continue toward ember-hollow, never snap back to ash-waste.
        var second = TurnEngine.Step(first.World,
            new[] { Move("e-wild-pack-1", "l-ember-ash", "l-home-ember") }, seed: 1);
        var later = second.World.Entities.Single(e => e.EntityId == "e-wild-pack-1");

        Assert.NotEqual("ash-waste", later.AtSectorId);
    }

    [Fact]
    public void A_legion_standing_in_a_sector_carries_no_lane_progress()
    {
        var world = World();
        var stale = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { LaneProgressMilli = 700 } : e)
                .ToList()
        };

        // Progress with no lane is meaningless state; left unvalidated it silently teleports a
        // legion 70% down the first lane of its next march.
        var ex = Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(stale));
        Assert.Contains("e-dave-legion-1", ex.Message);
    }

    [Fact]
    public void A_legion_on_a_lane_must_say_where_it_is_heading()
    {
        var world = World();
        var headless = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1"
                    ? e with { AtSectorId = null, OnLaneId = "l-home-ember", LaneProgressMilli = 300 }
                    : e)
                .ToList()
        };

        var ex = Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(headless));
        Assert.Contains("heading", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_heading_must_be_an_end_of_the_lane_the_legion_is_on()
    {
        var world = World();
        var wrong = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1"
                    ? e with
                    {
                        AtSectorId = null, OnLaneId = "l-home-ember",
                        LaneProgressMilli = 300, OnLaneTowardSectorId = "verdant-shelf"
                    }
                    : e)
                .ToList()
        };

        Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(wrong));
    }
}
