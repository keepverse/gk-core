using FusionRpg.Core.World;
using FusionRpg.Core.World.Topology;
using Xunit;

namespace FusionRpg.Core.Tests.World.Topology;

/// <summary>
/// W16 (spec-world-topology.md): which sectors are load-bearing.
///
/// Articulation points answer it as a boolean — does losing this cut the empire. Reconnection cost
/// answers it as a number that also says *how much worse* everything gets, which is the number a
/// garrison decision actually wants.
/// </summary>
public class ArticulationPointsTests
{
    static IReadOnlySet<string> Of(WorldState world) =>
        ArticulationPoints.Find(LaneGraph.Build(world));

    [Fact]
    public void The_middle_of_a_path_is_the_only_thing_holding_it_together()
    {
        Assert.Equal(new[] { "b" }, Of(GraphShapes.Path()).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void A_cycle_has_no_critical_sector_at_all()
    {
        Assert.Empty(Of(GraphShapes.Cycle()));
    }

    [Fact]
    public void Both_ends_of_a_barbells_neck_are_critical_and_nothing_else_is()
    {
        Assert.Equal(new[] { "c", "d" }, Of(GraphShapes.Barbell()).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void A_stars_hub_cuts_everything_and_its_spokes_cut_nothing()
    {
        Assert.Equal(new[] { "hub" }, Of(GraphShapes.Star()).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void A_graph_already_in_pieces_has_no_articulation_points_within_a_pair()
    {
        // Two separate two-sector islands: neither member of a pair cuts anything further.
        Assert.Empty(Of(GraphShapes.TwoIslands()));
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_the_world_was_written_in()
    {
        Assert.Equal(
            Of(GraphShapes.Barbell()).OrderBy(s => s, StringComparer.Ordinal),
            Of(GraphShapes.Reversed(GraphShapes.Barbell())).OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// `first-light` funnels its whole eastern frontier through ash-waste. The homeworld, despite
    /// being the capital, is *not* critical — ember-hollow and frost-mire reach each other round
    /// through ash. Being important and being load-bearing are different things, which is the
    /// distinction this module exists to draw.
    /// </summary>
    [Fact]
    public void In_first_light_two_sectors_are_load_bearing()
    {
        // Was ash-waste alone. Since 2026-08-22 Verdant Shelf hangs off Black Gate instead of Ash
        // Waste, so black-gate became a chokepoint too — the map now has two places worth holding
        // rather than one hub that saw and connected everything.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

        Assert.Equal(new[] { "ash-waste", "black-gate" }, Of(world).OrderBy(s => s, StringComparer.Ordinal));
    }
}

public class ReconnectionCostTests
{
    static IReadOnlyDictionary<string, long> Of(WorldState world, IReadOnlySet<string>? include = null) =>
        ReconnectionCost.For(world, include);

    [Fact]
    public void Losing_a_junction_costs_incomparably_more_than_losing_a_leaf()
    {
        var cost = Of(GraphShapes.Barbell());

        Assert.True(cost["c"] > cost["a"], "the neck must outrank a cluster member");
        Assert.True(cost["d"] > cost["b"]);
        Assert.True(cost["c"] > 1_000_000, "cutting the empire in two should be an enormous number, not a nudge");
    }

    [Fact]
    public void The_two_ends_of_a_symmetric_neck_cost_the_same()
    {
        var cost = Of(GraphShapes.Barbell());
        Assert.Equal(cost["c"], cost["d"]);
    }

    /// <summary>
    /// A ring never splits, but it does stretch: with a member gone the survivors take the long way
    /// round. Real cost, no disconnection — which is exactly the middle reading the number exists to
    /// express, and the one a boolean cannot say.
    /// </summary>
    [Fact]
    public void A_ring_costs_something_to_lose_but_never_splits()
    {
        var cost = Of(GraphShapes.Ring());

        Assert.All(cost.Values, v => Assert.True(v is > 0 and < 1_000_000, $"expected a real but finite cost, got {v}"));
        Assert.Empty(ArticulationPoints.Find(LaneGraph.Build(GraphShapes.Ring())));
    }

    /// <summary>
    /// Worth pinning because it is mildly counter-intuitive: in a triangle, losing a corner costs
    /// nothing at all. The other two were already touching, so no journey gets longer.
    /// </summary>
    [Fact]
    public void A_triangle_costs_nothing_to_lose_a_corner()
    {
        Assert.All(Of(GraphShapes.Cycle()).Values, v => Assert.Equal(0, v));
    }

    [Fact]
    public void A_leaf_costs_nothing_to_lose_because_nothing_routed_through_it()
    {
        var cost = Of(GraphShapes.Star());

        Assert.Equal(0, cost["x"]);
        Assert.True(cost["hub"] > 1_000_000);
    }

    [Fact]
    public void Nothing_is_cached_so_severing_a_lane_changes_the_next_answer()
    {
        var whole = Of(GraphShapes.Barbell());
        var cut = Of(GraphShapes.Sever(GraphShapes.Barbell(), "l-c-d"));

        // With the neck already gone, losing c no longer splits anything that was still joined.
        Assert.True(cut["c"] < whole["c"]);
    }

    [Fact]
    public void A_filter_asks_about_one_empire_rather_than_the_whole_map()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var holdings = new HashSet<string>(StringComparer.Ordinal) { "homeworld", "ember-hollow", "ash-waste" };

        var cost = Of(world, holdings);

        Assert.Equal(holdings.OrderBy(s => s, StringComparer.Ordinal), cost.Keys.OrderBy(s => s, StringComparer.Ordinal));
        Assert.True(cost["ember-hollow"] > cost["ash-waste"],
            "within this empire ember-hollow is the neck — ash-waste is the far end");
    }

    [Fact]
    public void An_empire_of_one_sector_has_nothing_to_disconnect()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var cost = Of(world, new HashSet<string>(StringComparer.Ordinal) { "homeworld" });

        Assert.Equal(0, cost["homeworld"]);
    }

    [Fact]
    public void Every_articulation_point_costs_more_than_every_sector_that_is_not_one()
    {
        var world = GraphShapes.Barbell();
        var critical = ArticulationPoints.Find(LaneGraph.Build(world));
        var cost = Of(world);

        var worstSafe = cost.Where(kv => !critical.Contains(kv.Key)).Max(kv => kv.Value);
        var bestCritical = cost.Where(kv => critical.Contains(kv.Key)).Min(kv => kv.Value);

        Assert.True(bestCritical > worstSafe,
            "the boolean and the number have to agree, or one of them is lying");
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_the_world_was_written_in()
    {
        var forward = Of(GraphShapes.Barbell());
        var reversed = Of(GraphShapes.Reversed(GraphShapes.Barbell()));

        Assert.Equal(
            forward.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)),
            reversed.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)));
    }

    [Fact]
    public void Ash_waste_is_the_most_expensive_sector_in_first_light_to_lose()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var cost = Of(world);

        Assert.Equal("ash-waste", cost.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key);
    }
}
