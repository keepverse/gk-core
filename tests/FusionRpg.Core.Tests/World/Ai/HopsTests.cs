using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Topology;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W29 (spec-ai-commander.md §ThreatMap): how many lanes away, not how expensive.
///
/// Threat spreads by "how far could it have got since I looked", and a force gets one sector further
/// per turn whether that lane was long or short. Pricing the spread would make a near enemy across
/// an expensive lane feel further than a distant one across a cheap one, which is backwards.
/// </summary>
public class HopsTests
{
    static LaneGraph Graph(params string[] edges) => LaneGraph.Build(GraphShapes.From(edges));

    [Fact]
    public void Distance_is_counted_in_lanes()
    {
        var hops = Hops.From(Graph("a-b", "b-c", "c-d"), "a");

        Assert.Equal(0, hops["a"]);
        Assert.Equal(1, hops["b"]);
        Assert.Equal(2, hops["c"]);
        Assert.Equal(3, hops["d"]);
    }

    [Fact]
    public void A_long_lane_is_still_one_hop()
    {
        // The whole reason this is not AllPairsCost: the numbers must not move when the map does.
        var near = Hops.From(LaneGraph.Build(GraphShapes.From(100, "a-b")), "a");
        var far = Hops.From(LaneGraph.Build(GraphShapes.From(9000, "a-b")), "a");

        Assert.Equal(near["b"], far["b"]);
    }

    [Fact]
    public void The_shortest_way_round_is_the_one_counted()
    {
        // a-b-c-d the long way, a-d directly: the ring must not report d as three hops.
        var hops = Hops.From(Graph("a-b", "b-c", "c-d", "a-d"), "a");
        Assert.Equal(1, hops["d"]);
    }

    [Fact]
    public void Somewhere_you_cannot_reach_is_absent_rather_than_far_away()
    {
        // "No route" and "a long way" are different answers. A caller that conflated them would
        // spread fear across a severed map, which is the opposite of what a cut lane should do.
        var hops = Hops.From(Graph("a-b", "y-z"), "a");

        Assert.False(hops.ContainsKey("y"));
        Assert.Null(Hops.Between(Graph("a-b", "y-z"), "a", "z"));
    }

    [Fact]
    public void An_origin_outside_the_graph_answers_nothing_rather_than_throwing()
    {
        // The scoped graph is a filter, and a caller asking about a sector outside its own empire is
        // an ordinary question with the answer "nowhere from there".
        Assert.Empty(Hops.From(Graph("a-b"), "elsewhere"));
    }

    [Fact]
    public void Reversing_the_input_changes_no_distance()
    {
        var forward = Hops.From(LaneGraph.Build(GraphShapes.Barbell()), "a");
        var backward = Hops.From(LaneGraph.Build(GraphShapes.Reversed(GraphShapes.Barbell())), "a");

        Assert.Equal(forward.OrderBy(kv => kv.Key, StringComparer.Ordinal), backward.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }
}
