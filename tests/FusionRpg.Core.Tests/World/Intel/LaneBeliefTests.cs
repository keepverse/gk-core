using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// The graph's *shape* is public knowledge — you can see six sectors and how the roads join them.
/// Whether a particular road is still passable is not: that is something you learn by looking at it.
///
/// `IWorldView` promises nothing outside the engine reads the truth. Handing back a lane's real
/// state for a road nobody can see would be exactly that, and the quietest kind: a faction routing
/// confidently around a cut it has no way of knowing about.
/// </summary>
public class LaneBeliefTests
{
    static WorldState Severed(string laneId)
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        return world with
        {
            Lanes = world.Lanes.Select(l => l.LaneId == laneId ? l with { State = LaneState.Severed } : l).ToList()
        };
    }

    static WorldLane LaneOf(IWorldView view, string laneId) => view.Lanes.Single(l => l.LaneId == laneId);

    [Fact]
    public void A_cut_you_can_see_is_a_cut()
    {
        // Dave stands in the homeworld, so l-home-ember is right in front of him.
        var view = new BelievedWorldView(Severed("l-home-ember"), "dave");

        Assert.Equal(LaneState.Severed, LaneOf(view, "l-home-ember").State);
    }

    [Fact]
    public void A_cut_on_the_far_side_of_the_map_is_not_something_you_know_about()
    {
        // l-ash-black joins two sectors Dave has never set foot in.
        var view = new BelievedWorldView(Severed("l-ash-black"), "dave");

        Assert.Equal(LaneState.Open, LaneOf(view, "l-ash-black").State);
    }

    [Fact]
    public void Seeing_either_end_is_enough_to_see_the_road()
    {
        var world = Severed("l-ember-ash");

        // Dave glimpses ember-hollow from home — one end of the lane — which is enough.
        Assert.Equal(LaneState.Severed, LaneOf(new BelievedWorldView(world, "dave"), "l-ember-ash").State);

        // The wild pack stands at ash-waste, the other end. Also enough.
        Assert.Equal(LaneState.Severed, LaneOf(new BelievedWorldView(world, "wild"), "l-ember-ash").State);

        // Zomboss is nowhere near either.
        // "nobody" is a faction id nobody plays, rather than Zomboss: since 2026-08-22 the template gives him a
        // warband and a Seat, so he is no longer anybody's example of knowing nothing.
        Assert.Equal(LaneState.Open, LaneOf(new BelievedWorldView(world, "nobody"), "l-ember-ash").State);
    }

    [Fact]
    public void Everything_about_a_lane_except_whether_it_is_open_stays_public()
    {
        var world = Severed("l-ash-black");
        var truth = world.Lanes.Single(l => l.LaneId == "l-ash-black");
        var believed = LaneOf(new BelievedWorldView(world, "dave"), "l-ash-black");

        // The road is drawn on the map: where it goes, how long, how wide, what kind.
        Assert.Equal(truth.FromSectorId, believed.FromSectorId);
        Assert.Equal(truth.ToSectorId, believed.ToSectorId);
        Assert.Equal(truth.TypeId, believed.TypeId);
        Assert.Equal(truth.Length, believed.Length);
        Assert.Equal(truth.Width, believed.Width);
    }

    [Fact]
    public void An_open_lane_reads_open_whether_or_not_you_can_see_it()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var view = new BelievedWorldView(world, "zomboss");

        Assert.All(view.Lanes, l => Assert.Equal(LaneState.Open, l.State));
    }

    [Fact]
    public void The_lane_list_is_the_same_shape_for_everyone()
    {
        var world = Severed("l-ash-black");

        Assert.Equal(
            new BelievedWorldView(world, "dave").Lanes.Select(l => l.LaneId),
            new BelievedWorldView(world, "zomboss").Lanes.Select(l => l.LaneId));
    }
}
