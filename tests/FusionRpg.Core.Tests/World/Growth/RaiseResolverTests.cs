using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Growth;

/// <summary>
/// world-map W51 acceptance: `raise` is rejected with its own reason for each illegal case (not
/// yours at Snapshot, no Seat slot, a hostile entity standing in it, `RecruitStock &lt;
/// RaiseCostPoints`); a raised legion's id is derived and stable across replay; no hard cap on
/// legion count exists anywhere in `src/`. Direct-call, resolver-level tests — <see
/// cref="RaiseThreadingTests"/> proves the same mechanism reached through a real
/// <see cref="TurnEngine.Step"/> commit.
/// </summary>
public class RaiseResolverTests
{
    const string Phase = "Test";
    const int Turn = 7;
    const long RaiseCost = 100;

    static WorldSlot Seat(string? owner = null) =>
        new() { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId, OwnerFactionId = owner };

    static WorldSector Sector(
        string id, string? owner, IReadOnlyList<WorldSlot>? slots = null, long recruitStock = RaiseCost,
        ElementTypeId? climate = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = owner, Climate = climate,
            Slots = slots ?? new[] { Seat(owner) }, RecruitStock = recruitStock
        };

    static WorldState World(IReadOnlyList<WorldSector> sectors, IReadOnlyList<WorldEntity>? entities = null) => new()
    {
        WorldId = "w", TemplateId = "test", Seed = 1, CurrentTurn = Turn - 1,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = sectors.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),
        Entities = (entities ?? Array.Empty<WorldEntity>()).OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
    };

    static WorldCommand Raise(string commander, string commandId, string sectorId) => new()
    {
        CommanderId = commander, CommandId = commandId, Kind = WorldCommandKinds.Raise, SectorId = sectorId
    };

    static WorldEntity Hostile(string sectorId, string owner = "wild") => new()
    {
        EntityId = "e-" + owner + "-1", Kind = WorldEntityKind.Warband, OwnerFactionId = owner, AtSectorId = sectorId
    };

    [Fact]
    public void An_affordable_owned_uncontested_seat_founds_a_legion_and_spends_the_stock()
    {
        var world = World(new[] { Sector("s1", "f1") });
        var report = new TurnReport();

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, report, Phase, Turn);

        var legion = Assert.Single(result.Entities);
        Assert.Equal("e-f1-legion-7-s1", legion.EntityId);
        Assert.Equal(WorldEntityKind.Legion, legion.Kind);
        Assert.Equal("f1", legion.OwnerFactionId);
        Assert.Equal("s1", legion.AtSectorId);
        Assert.Equal(0, result.Sectors.Single().RecruitStock);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.Event && e.Detail == "raise.founded:e-f1-legion-7-s1");
        Assert.Empty(report.Dropped);
    }

    [Fact]
    public void The_founded_legions_id_is_derived_from_its_cause_and_stable_across_replay()
    {
        var world = World(new[] { Sector("s1", "f1") });

        var first = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, new TurnReport(), Phase, Turn);
        var replay = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, new TurnReport(), Phase, Turn);

        Assert.Equal(first.Entities.Single().EntityId, replay.Entities.Single().EntityId);
    }

    [Fact]
    public void A_sector_the_commander_no_longer_owns_at_resolution_is_refused_not_yours()
    {
        var world = World(new[] { Sector("s1", "someone-else") });
        var report = new TurnReport();

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, report, Phase, Turn);

        Assert.Empty(result.Entities);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "raise.not-yours");
    }

    [Fact]
    public void A_sector_with_no_seat_slot_is_refused_no_seat()
    {
        var world = World(new[]
        {
            Sector("s1", "f1", slots: new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = "wildland" } })
        });
        var report = new TurnReport();

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, report, Phase, Turn);

        Assert.Empty(result.Entities);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "raise.no-seat");
    }

    [Fact]
    public void A_hostile_entity_standing_in_the_sector_is_refused_contested()
    {
        var world = World(new[] { Sector("s1", "f1") }, new[] { Hostile("s1") });
        var report = new TurnReport();

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, report, Phase, Turn);

        Assert.Empty(result.Entities.Where(e => e.OwnerFactionId == "f1"));
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "raise.contested");
    }

    [Fact]
    public void An_own_entity_standing_in_the_sector_is_not_hostile()
    {
        var friendly = new WorldEntity
        {
            EntityId = "e-f1-legion-1", Kind = WorldEntityKind.Legion, OwnerFactionId = "f1", AtSectorId = "s1"
        };
        var world = World(new[] { Sector("s1", "f1") }, new[] { friendly });
        var report = new TurnReport();

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, report, Phase, Turn);

        Assert.Equal(2, result.Entities.Count);
        Assert.Empty(report.Dropped);
    }

    [Fact]
    public void Insufficient_recruit_stock_is_refused_cannot_afford()
    {
        var world = World(new[] { Sector("s1", "f1", recruitStock: RaiseCost - 1) });
        var report = new TurnReport();

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "s1") }, report, Phase, Turn);

        Assert.Empty(result.Entities);
        Assert.Equal(RaiseCost - 1, result.Sectors.Single().RecruitStock);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "raise.cannot-afford");
    }

    [Fact]
    public void A_failed_raise_attempt_does_not_block_a_later_legal_one_at_the_same_sector()
    {
        // Regression: the one-per-sector-per-turn guard must trip only on an actual *success*, never
        // on a mere attempt — otherwise an illegal first order (wrong commander, here) would
        // incorrectly poison a later, legal order at the same sector with "raise.already-founded"
        // instead of letting it found the legion it is actually entitled to.
        var world = World(new[] { Sector("s1", "f1") });
        var report = new TurnReport();

        var result = RaiseResolver.Run(
            world,
            new[] { Raise("someone-else", "c1", "s1"), Raise("f1", "c2", "s1") },
            report, Phase, Turn);

        var legion = Assert.Single(result.Entities);
        Assert.Equal("f1", legion.OwnerFactionId);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "raise.not-yours");
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("raise.founded:"));
        Assert.DoesNotContain(report.Entries, e => e.Detail == "raise.already-founded");
    }

    [Fact]
    public void An_unnamed_or_unknown_sector_is_refused_sector_missing()
    {
        var world = World(new[] { Sector("s1", "f1") });
        var report = new TurnReport();

        var noSector = new WorldCommand { CommanderId = "f1", CommandId = "c1", Kind = WorldCommandKinds.Raise, SectorId = null };
        var unknownSector = Raise("f1", "c2", "nowhere");

        var result = RaiseResolver.Run(world, new[] { noSector, unknownSector }, report, Phase, Turn);

        Assert.Empty(result.Entities);
        Assert.Equal(2, report.Dropped.Count(e => e.Detail == "sector.missing"));
    }

    [Fact]
    public void Two_raise_orders_against_the_same_sector_in_one_turn_never_collide_the_second_is_dropped()
    {
        // A real, guarded defect: without an explicit one-per-sector-per-turn gate, a sector deep
        // enough in stock to afford two raises would produce two entities sharing the identical
        // derived id (`e-{faction}-legion-{turn}-{sector}`), which `WorldValidation`'s stable-order
        // rule rejects outright as a duplicate. This proves the guard, not merely assumes it.
        var world = World(new[] { Sector("s1", "f1", recruitStock: RaiseCost * 2) });
        var report = new TurnReport();

        var result = RaiseResolver.Run(
            world, new[] { Raise("f1", "c1", "s1"), Raise("f1", "c2", "s1") }, report, Phase, Turn);

        var legion = Assert.Single(result.Entities);
        Assert.Equal("e-f1-legion-7-s1", legion.EntityId);
        Assert.Equal(RaiseCost, result.Sectors.Single().RecruitStock); // spent exactly once
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "raise.already-founded");
    }

    [Fact]
    public void Raising_at_several_different_sectors_in_one_turn_has_no_legion_count_cap()
    {
        var world = World(new[] { Sector("s1", "f1"), Sector("s2", "f1"), Sector("s3", "f1") });
        var report = new TurnReport();

        var result = RaiseResolver.Run(
            world,
            new[] { Raise("f1", "c1", "s1"), Raise("f1", "c2", "s2"), Raise("f1", "c3", "s3") },
            report, Phase, Turn);

        Assert.Equal(3, result.Entities.Count);
        Assert.Empty(report.Dropped);
    }

    [Theory]
    [InlineData("ash-waste-like-earth", ElementTypeId.Earth, "bucketzombie")]
    [InlineData("ember-hollow-like-fire", ElementTypeId.Fire, "cherrynutzombie")]
    [InlineData("frost-mire-like-ice", ElementTypeId.Ice, "conezombie")]
    public void The_founded_legions_one_member_is_the_sectors_climate_species_lowest_by_ordinal(
        string sectorId, ElementTypeId climate, string expectedSpeciesId)
    {
        var world = World(new[] { Sector(sectorId, "f1", climate: climate) });

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", sectorId) }, new TurnReport(), Phase, Turn);

        var member = Assert.Single(Assert.Single(result.Entities).Members);
        Assert.Equal(expectedSpeciesId, member.SpeciesId);
        Assert.Equal(1, member.Level);
        Assert.Equal(RecruitPolicy.RaiseMemberHp, member.Hp);
    }

    [Fact]
    public void A_sector_with_no_climate_the_homeworld_shape_falls_back_deterministically()
    {
        var world = World(new[] { Sector("homeworld-like", "f1", climate: null) });

        var result = RaiseResolver.Run(world, new[] { Raise("f1", "c1", "homeworld-like") }, new TurnReport(), Phase, Turn);

        var member = Assert.Single(Assert.Single(result.Entities).Members);
        Assert.Equal("bucketnutzombie", member.SpeciesId); // lowest-ordinal zombie-side Dark species
    }
}
