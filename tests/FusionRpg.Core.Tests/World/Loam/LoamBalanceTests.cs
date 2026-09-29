using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>L8 acceptance (spec-loam-calc.md #4): the S3 resolution and ideal §12.4's central claim.</summary>
public class LoamBalanceTests
{
    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSector Sector(string id, string? owner, int development = 0, int danger = 0, IReadOnlyList<WorldSlot>? slots = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = owner,
            DevelopmentLevel = development, DangerBand = danger,
            Slots = slots ?? Array.Empty<WorldSlot>()
        };

    static WorldLane Lane(string id, string from, string to, LaneState state = LaneState.Open) => new()
    {
        LaneId = id, FromSectorId = from, ToSectorId = to, TypeId = LaneTypeCatalog.RiftLaneTypeId, State = state
    };

    [Fact]
    public void Per_sector_balance_is_production_minus_upkeep()
    {
        var rich = Sector("s", "f1", slots: new[] { Rootbed(0) });
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { rich }
        };

        var expected = LoamProduction.For(rich) - LoamUpkeep.For(world, rich);
        Assert.Equal(expected, LoamBalance.PerSector(world, rich));
    }

    [Fact]
    public void Ordinary_ground_with_no_rootbed_runs_a_deficit()
    {
        // ideal §12.4's central claim, asserted rather than believed: a sector must still pay
        // upkeep even when it produces nothing, because it has no rootbed at all.
        var ordinary = Sector("s", "f1", development: 2, danger: 1);
        var withSource = Sector("s2", "f1", slots: new[] { Rootbed(0) }); // keeps the faction habitable (G-C)
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { ordinary, withSource }
        };

        Assert.True(LoamBalance.PerSector(world, ordinary) < 0);
    }

    [Fact]
    public void A_severed_territory_stops_a_rich_half_subsidising_a_poor_half()
    {
        // a-b is rich (a rootbed each); c-d is ordinary ground with no source of its own. Connected,
        // the poor half draws on the rich half's surplus (this is `loam-turn`'s job, not this
        // calculator's — but the *component* balance must show it, or nothing downstream can act on
        // it). Severed, the poor half's component balance is its own deficit alone.
        var a = Sector("a", "f1", slots: new[] { Rootbed(0) });
        var b = Sector("b", "f1", slots: new[] { Rootbed(0) });
        var c = Sector("c", "f1", development: 3, danger: 2);
        var d = Sector("d", "f1", development: 3, danger: 2);

        WorldState World(LaneState bcState) => new()
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { a, b, c, d },
            Lanes = new[] { Lane("l-ab", "a", "b"), Lane("l-bc", "b", "c", bcState), Lane("l-cd", "c", "d") }
        };

        var whole = World(LaneState.Open);
        var wholeComponent = Assert.Single(TerritoryComponents.For(whole, "f1"));
        var wholeBalance = LoamBalance.PerComponent(whole, wholeComponent);
        Assert.True(wholeBalance > 0, "a-b's surplus must outweigh c-d's deficit while connected");

        var severed = World(LaneState.Severed);
        var severedComponents = TerritoryComponents.For(severed, "f1");
        Assert.Equal(2, severedComponents.Count);

        var richHalf = LoamBalance.PerComponent(severed, severedComponents[0]);
        var poorHalf = LoamBalance.PerComponent(severed, severedComponents[1]);

        Assert.True(richHalf > 0, "the rootbed half must stand on its own once cut off");
        Assert.True(poorHalf < 0, "the source-less half must run its own deficit once cut off, no longer subsidised");
    }

    [Fact]
    public void Per_faction_balance_is_the_sum_of_every_components_balance()
    {
        var a = Sector("a", "f1", slots: new[] { Rootbed(0) });
        var b = Sector("b", "f1", development: 2); // isolated: no lane to a
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { a, b }
        };

        var components = TerritoryComponents.For(world, "f1");
        var expected = components.Sum(c => LoamBalance.PerComponent(world, c));

        Assert.Equal(expected, LoamBalance.PerFaction(world, "f1"));
    }
}
