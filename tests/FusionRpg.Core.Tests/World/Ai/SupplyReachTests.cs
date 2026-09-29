using FusionRpg.Core.World.Movement;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W30 (spec-ai-commander.md §Believed supply): the traversal both halves share, tested at the level
/// it actually has rules — seeds, links, and a predicate.
///
/// One rule here is **unreachable through shipped content**: no lane type is both one-way and
/// supply-carrying, so `LinksOf` never produces a one-way link and `SupplyGraph` can never exercise
/// the direction check. Left untested it is a mutant that survives forever; tested here it is
/// defensive code that is known to work on the day somebody authors a supply-carrying current.
/// </summary>
public class SupplyReachTests
{
    static SupplyReach.Link Both(string from, string to) => new(from, to, OneWay: false);
    static SupplyReach.Link Only(string from, string to) => new(from, to, OneWay: true);

    static bool Anything(string _) => true;

    [Fact]
    public void Supply_flows_out_from_every_seed_and_stops_where_the_links_do()
    {
        var reached = SupplyReach.From(new[] { "a" }, new[] { Both("a", "b"), Both("b", "c") }, Anything);

        Assert.Equal(new[] { "a", "b", "c" }, reached.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void A_one_way_link_carries_supply_the_way_it_flows_and_not_back()
    {
        // Unreachable through the shipped lane catalog — a temporal current carries no supply at
        // all — so this is the only place the rule is ever exercised.
        Assert.Contains("b", SupplyReach.From(new[] { "a" }, new[] { Only("a", "b") }, Anything));
        Assert.DoesNotContain("a", SupplyReach.From(new[] { "b" }, new[] { Only("a", "b") }, Anything));
    }

    [Fact]
    public void Ground_the_predicate_rejects_is_neither_entered_nor_crossed()
    {
        // Not merely excluded from the answer — impassable. A sector an enemy holds must not be a
        // waypoint to the sector behind it, which is the whole point of a zone of control.
        var links = new[] { Both("a", "b"), Both("b", "c") };
        var reached = SupplyReach.From(new[] { "a" }, links, id => id != "b");

        Assert.Equal(new[] { "a" }, reached);
    }

    [Fact]
    public void A_seed_the_predicate_rejects_never_starts_a_chain()
    {
        Assert.Empty(SupplyReach.From(new[] { "a" }, new[] { Both("a", "b") }, _ => false));
    }

    [Fact]
    public void No_seeds_means_no_network_rather_than_the_whole_map()
    {
        // The wild never had a capital, so they do not starve for want of one: empty means "not
        // applicable", and a caller that read it as "everything is cut" would bleed them dry.
        Assert.Empty(SupplyReach.From(Array.Empty<string>(), new[] { Both("a", "b") }, Anything));
    }

    [Fact]
    public void The_walk_is_reproducible_whatever_order_it_is_handed()
    {
        var links = new[] { Both("a", "b"), Both("b", "c"), Both("a", "c") };

        Assert.Equal(
            SupplyReach.From(new[] { "a" }, links, Anything).OrderBy(id => id, StringComparer.Ordinal),
            SupplyReach.From(new[] { "a" }, links.Reverse().ToArray(), Anything)
                .OrderBy(id => id, StringComparer.Ordinal));
    }
}
