using FusionRpg.Core.World;
using FusionRpg.Core.World.Topology;
using Xunit;

namespace FusionRpg.Core.Tests.World.Topology;

/// <summary>
/// W15 (spec-world-topology.md): the lane network as a graph, and how far everything is from
/// everything. Scoped by a sector filter, so the same code answers "the whole map", "one faction's
/// holdings", and "holdings minus the sector an enemy is sitting in".
/// </summary>
public class LaneGraphTests
{
    [Fact]
    public void Every_open_lane_becomes_an_edge_in_both_directions()
    {
        var graph = LaneGraph.Build(GraphShapes.Path());

        Assert.Equal(new[] { "a", "b", "c" }, graph.Sectors);
        Assert.Equal(4, graph.Edges.Count);   // a→b, b→a, b→c, c→b
        Assert.Contains(graph.Edges, e => e.FromSectorId == "a" && e.ToSectorId == "b");
        Assert.Contains(graph.Edges, e => e.FromSectorId == "b" && e.ToSectorId == "a");
    }

    [Fact]
    public void A_severed_lane_is_not_an_edge()
    {
        var graph = LaneGraph.Build(GraphShapes.Sever(GraphShapes.Path(), "l-a-b"));

        Assert.DoesNotContain(graph.Edges, e => e.FromSectorId == "a" || e.ToSectorId == "a");
        Assert.Equal(2, graph.Edges.Count);
    }

    [Fact]
    public void A_shut_gate_is_not_an_edge_but_an_open_one_is()
    {
        var world = GraphShapes.Path();
        var shut = world with
        {
            Lanes = world.Lanes
                .Select(l => l.LaneId == "l-a-b" ? l with { TypeId = "gated", GateKeyId = "key-of-ash" } : l)
                .ToList()
        };
        Assert.DoesNotContain(LaneGraph.Build(shut).Edges, e => e.FromSectorId == "a");

        // A gated lane with nothing barring it is just a lane.
        var open = world with
        {
            Lanes = world.Lanes.Select(l => l.LaneId == "l-a-b" ? l with { TypeId = "gated" } : l).ToList()
        };
        Assert.Contains(LaneGraph.Build(open).Edges, e => e.FromSectorId == "a");
    }

    [Fact]
    public void A_lane_type_that_carries_no_supply_carries_no_topology_either()
    {
        var world = GraphShapes.Path();
        var deep = world with
        {
            Lanes = world.Lanes.Select(l => l.LaneId == "l-a-b" ? l with { TypeId = "deep" } : l).ToList()
        };

        Assert.DoesNotContain(LaneGraph.Build(deep).Edges, e => e.FromSectorId == "a");
    }

    /// <summary>
    /// A temporal current carries no supply, so it holds no empire together and is not a topology
    /// edge at all — the one-way *direction* rule never gets a chance to apply. That is the shipped
    /// catalog's doing, not this module's, so the rule stays in the code for the day a directed
    /// supply lane exists.
    /// </summary>
    [Fact]
    public void A_temporal_current_holds_nothing_together()
    {
        var world = GraphShapes.Path();
        var oneWay = world with
        {
            Lanes = world.Lanes.Select(l => l.LaneId == "l-a-b" ? l with { TypeId = "one-way" } : l).ToList()
        };

        Assert.DoesNotContain(LaneGraph.Build(oneWay).Edges, e => e.FromSectorId == "a" || e.ToSectorId == "a");
    }

    [Fact]
    public void A_filter_scopes_the_graph_to_the_sectors_you_asked_about()
    {
        var graph = LaneGraph.Build(GraphShapes.Path(), new HashSet<string>(StringComparer.Ordinal) { "a", "b" });

        Assert.Equal(new[] { "a", "b" }, graph.Sectors);
        Assert.All(graph.Edges, e => Assert.NotEqual("c", e.ToSectorId));
    }

    [Fact]
    public void Sectors_and_edges_come_out_in_ordinal_order_whatever_order_they_went_in()
    {
        var forward = LaneGraph.Build(GraphShapes.Barbell());
        var reversed = LaneGraph.Build(GraphShapes.Reversed(GraphShapes.Barbell()));

        Assert.Equal(forward.Sectors, reversed.Sectors);
        Assert.Equal(forward.Edges, reversed.Edges);
    }
}

public class AllPairsCostTests
{
    static AllPairsCost Of(WorldState world, IReadOnlySet<string>? include = null) =>
        AllPairsCost.Compute(LaneGraph.Build(world, include));

    [Fact]
    public void Getting_nowhere_costs_nothing()
    {
        Assert.Equal(0, Of(GraphShapes.Path()).Between("b", "b"));
    }

    [Fact]
    public void One_lane_costs_one_lane_and_two_cost_two()
    {
        var cost = Of(GraphShapes.Path());

        Assert.Equal(1000, cost.Between("a", "b"));
        Assert.Equal(2000, cost.Between("a", "c"));
        Assert.Equal(2000, cost.Between("c", "a"));   // symmetric on two-way lanes
    }

    [Fact]
    public void The_cheapest_route_wins_not_the_first_one_found()
    {
        // a–c direct is one lane; a–b–c is two. The long way round must not be reported.
        Assert.Equal(1000, Of(GraphShapes.Cycle()).Between("a", "c"));
    }

    [Fact]
    public void Islands_are_reported_as_islands_rather_than_pretended_away()
    {
        var cost = Of(GraphShapes.TwoIslands());

        Assert.True(cost.Reachable("a", "b"));
        Assert.False(cost.Reachable("a", "z"));
        Assert.Equal(AllPairsCost.Unreachable, cost.Between("a", "z"));
    }

    [Fact]
    public void Unreachable_stays_addable_so_a_sum_of_them_cannot_overflow()
    {
        // ReconnectionCost sums deltas across every pair; the sentinel has to survive that.
        var total = 0L;
        for (var i = 0; i < 10_000; i++) total += AllPairsCost.Unreachable;
        Assert.True(total > 0);
        Assert.True(AllPairsCost.Unreachable + AllPairsCost.Unreachable > 0);
    }

    [Fact]
    public void Cutting_the_neck_of_a_barbell_separates_the_two_halves()
    {
        var whole = Of(GraphShapes.Barbell());
        Assert.True(whole.Reachable("a", "f"));

        var cut = Of(GraphShapes.Sever(GraphShapes.Barbell(), "l-c-d"));
        Assert.False(cut.Reachable("a", "f"));
        Assert.True(cut.Reachable("a", "b"));   // the west cluster is still whole
    }

    [Fact]
    public void A_star_makes_every_spoke_two_lanes_from_every_other()
    {
        var cost = Of(GraphShapes.Star());

        Assert.Equal(1000, cost.Between("hub", "x"));
        Assert.Equal(2000, cost.Between("x", "z"));
    }

    [Fact]
    public void Longer_lanes_cost_more()
    {
        Assert.Equal(600, AllPairsCost
            .Compute(LaneGraph.Build(GraphShapes.From(300, "a-b", "b-c")))
            .Between("a", "c"));
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_the_world_was_written_in()
    {
        var forward = Of(GraphShapes.Barbell());
        var reversed = Of(GraphShapes.Reversed(GraphShapes.Barbell()));

        foreach (var a in new[] { "a", "b", "c", "d", "e", "f" })
        foreach (var b in new[] { "a", "b", "c", "d", "e", "f" })
            Assert.Equal(forward.Between(a, b), reversed.Between(a, b));
    }

    [Fact]
    public void A_sector_outside_the_filter_is_not_in_the_answer_at_all()
    {
        var cost = Of(GraphShapes.Path(), new HashSet<string>(StringComparer.Ordinal) { "a", "b" });

        Assert.True(cost.Reachable("a", "b"));
        Assert.Throws<KeyNotFoundException>(() => cost.Between("a", "c"));
    }
}
