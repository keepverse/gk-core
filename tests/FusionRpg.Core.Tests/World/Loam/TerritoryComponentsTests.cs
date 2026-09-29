using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L6 acceptance (spec-loam-calc.md #2): a hand-built fixture, not `first-light` — ordinary ground
/// where a-b-c-d is one faction's territory and e is neighbouring but unowned.
/// </summary>
public class TerritoryComponentsTests
{
    static WorldSector Sector(string id, string? owner) => new() { SectorId = id, TypeId = "stable", OwnerFactionId = owner };

    static WorldLane Lane(string id, string from, string to, LaneState state = LaneState.Open) => new()
    {
        LaneId = id, FromSectorId = from, ToSectorId = to, TypeId = LaneTypeCatalog.RiftLaneTypeId, State = state
    };

    /// <summary>a-b-c-d owned by "f1", chained by lanes; e is owned by nobody and hangs off a.</summary>
    static WorldState Fixture(LaneState bcState = LaneState.Open) => new()
    {
        WorldId = "fixture", TemplateId = "test", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = new[]
        {
            Sector("a", "f1"), Sector("b", "f1"), Sector("c", "f1"), Sector("d", "f1"), Sector("e", null)
        },
        Lanes = new[]
        {
            Lane("l-ab", "a", "b"), Lane("l-bc", "b", "c", bcState), Lane("l-cd", "c", "d"), Lane("l-ae", "a", "e")
        }
    };

    static IReadOnlyList<IReadOnlyList<string>> ComponentsOf(WorldState w, string factionId) =>
        TerritoryComponents.For(w, factionId);

    [Fact]
    public void One_unbroken_chain_is_a_single_component_of_every_member()
    {
        var components = ComponentsOf(Fixture(), "f1");

        var one = Assert.Single(components);
        Assert.Equal(new[] { "a", "b", "c", "d" }, one);
    }

    [Fact]
    public void An_unowned_sector_never_joins_a_component_even_when_adjacent()
    {
        var components = ComponentsOf(Fixture(), "f1");
        Assert.DoesNotContain(components, c => c.Contains("e"));
    }

    [Fact]
    public void A_severed_lane_splits_one_territory_into_two_components()
    {
        var components = ComponentsOf(Fixture(bcState: LaneState.Severed), "f1");

        Assert.Equal(2, components.Count);
        Assert.Equal(new[] { "a", "b" }, components[0]);
        Assert.Equal(new[] { "c", "d" }, components[1]);
    }

    [Fact]
    public void The_whole_collection_is_ordered_by_each_components_lowest_member()
    {
        var severed = Fixture(bcState: LaneState.Severed);
        // Reversing input order must not change the output order — the fixture already lists "a"
        // before "c", so this proves the ordering comes from the algorithm, not from input luck.
        var reversedSectors = severed with { Sectors = severed.Sectors.Reverse().ToList() };

        var components = ComponentsOf(reversedSectors, "f1");
        Assert.Equal("a", components[0][0]);
        Assert.Equal("c", components[1][0]);
    }

    [Fact]
    public void Reversing_sectors_or_lanes_changes_neither_contents_nor_order()
    {
        var baseline = ComponentsOf(Fixture(), "f1");

        var reversedLanes = Fixture() with { Lanes = Fixture().Lanes.Reverse().ToList() };
        var reversedSectors = Fixture() with { Sectors = Fixture().Sectors.Reverse().ToList() };

        Assert.Equal(baseline, ComponentsOf(reversedLanes, "f1"));
        Assert.Equal(baseline, ComponentsOf(reversedSectors, "f1"));
    }

    [Fact]
    public void A_faction_with_no_holdings_at_all_has_no_components()
    {
        Assert.Empty(ComponentsOf(Fixture(), "nobody"));
    }

    [Fact]
    public void An_isolated_holding_is_its_own_singleton_component()
    {
        // d loses its only link (c-d), so it stands alone: a-b-c is one component, d is another.
        var isolated = Fixture() with
        {
            Lanes = Fixture().Lanes.Where(l => l.LaneId != "l-cd").ToList()
        };

        var components = ComponentsOf(isolated, "f1");
        Assert.Equal(2, components.Count);
        Assert.Equal(new[] { "a", "b", "c" }, components[0]);
        Assert.Equal(new[] { "d" }, components[1]);
    }

    [Fact]
    public void The_belief_overload_agrees_with_the_truth_overload_on_the_same_data()
    {
        var world = Fixture(bcState: LaneState.Severed);
        var truth = TerritoryComponents.For(world, "f1");

        var ownedIds = world.Sectors.Where(s => s.OwnerFactionId == "f1").Select(s => s.SectorId);
        var belief = TerritoryComponents.For(ownedIds, SupplyReach.LinksOf(world.Lanes));

        Assert.Equal(truth, belief);
    }

    /// <summary>
    /// The distinction the spec insists on: same graph, different question. A faction with sectors
    /// but no Seat anywhere has no supply network at all (`SupplyGraph.ConnectedSectors` is empty,
    /// by design — "the wild do not starve for want of a capital they never had"), but its ground is
    /// still one connected block that can pool loam among itself. Merging the two would make loam
    /// vanish for exactly the factions the wild-hazard exemption (G-C) exists to describe.
    /// </summary>
    [Fact]
    public void This_is_not_supply_graph_connected_sectors()
    {
        var noSeatAnywhere = Fixture(); // none of a/b/c/d carry a Seat slot

        var supply = SupplyGraph.ConnectedSectors(noSeatAnywhere, "f1");
        var territory = TerritoryComponents.For(noSeatAnywhere, "f1");

        Assert.Empty(supply);
        Assert.Equal(new[] { "a", "b", "c", "d" }, Assert.Single(territory));
    }
}
