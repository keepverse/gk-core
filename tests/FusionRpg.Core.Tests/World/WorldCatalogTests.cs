using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W1 acceptance (spec-world-model.md §Catalogs): the four world catalogs validate themselves at
/// bootstrap, cross-reference each other, and reject unknown ids loudly.
/// </summary>
public class WorldCatalogTests
{
    [Fact]
    public void All_catalogs_validate_on_first_touch()
    {
        Assert.NotEmpty(SectorTypeCatalog.All);   // touching All runs Validate()
        Assert.NotEmpty(SlotTypeCatalog.All);
        Assert.NotEmpty(LaneTypeCatalog.All);
        Assert.NotEmpty(FactionKindCatalog.All);
    }

    [Fact]
    public void Sector_type_slot_references_resolve_in_the_slot_catalog()
    {
        foreach (var sector in SectorTypeCatalog.All)
            foreach (var slotTypeId in sector.AllowedSlotTypes)
                Assert.True(SlotTypeCatalog.IsKnown(slotTypeId),
                    $"Sector type '{sector.TypeId}' allows unknown slot type '{slotTypeId}'.");
    }

    [Fact]
    public void A_type_that_forbids_bases_can_never_host_a_seat()
    {
        foreach (var sector in SectorTypeCatalog.All)
            if (sector.Flags.HasFlag(SectorTypeFlags.NoBase))
                Assert.False(sector.CanHostSeat, $"'{sector.TypeId}' is no-base yet claims a Seat.");

        // And the rule is enforced, not merely satisfied by today's content.
        var bad = new[]
        {
            new SectorTypeDef
            {
                TypeId = "bad-nobase-seat", Name = "Bad", BaseDangerBand = 1,
                CanHostSeat = true, Flags = SectorTypeFlags.NoBase,
                AllowedSlotTypes = new[] { "seat" }
            }
        };
        var ex = Assert.Throws<InvalidOperationException>(() => SectorTypeCatalog.Validate(bad));
        Assert.Contains("bad-nobase-seat", ex.Message);
    }

    [Fact]
    public void A_seat_hosting_type_must_allow_the_seat_slot()
    {
        foreach (var sector in SectorTypeCatalog.All.Where(s => s.CanHostSeat))
            Assert.Contains(SlotTypeCatalog.SeatSlotTypeId, sector.AllowedSlotTypes);

        var bad = new[]
        {
            new SectorTypeDef
            {
                TypeId = "seat-without-slot", Name = "Bad", BaseDangerBand = 1,
                CanHostSeat = true, Flags = SectorTypeFlags.None,
                AllowedSlotTypes = new[] { "wildland" }
            }
        };
        Assert.Throws<InvalidOperationException>(() => SectorTypeCatalog.Validate(bad));
    }

    [Fact]
    public void Duplicate_and_malformed_ids_reject()
    {
        var dupe = new[]
        {
            new SectorTypeDef { TypeId = "stable", Name = "A", AllowedSlotTypes = new[] { "wildland" } },
            new SectorTypeDef { TypeId = "stable", Name = "B", AllowedSlotTypes = new[] { "wildland" } }
        };
        Assert.Contains("Duplicate", Assert.Throws<InvalidOperationException>(
            () => SectorTypeCatalog.Validate(dupe)).Message);

        var shouty = new[]
        {
            new SectorTypeDef { TypeId = "Stable", Name = "A", AllowedSlotTypes = new[] { "wildland" } }
        };
        Assert.Throws<InvalidOperationException>(() => SectorTypeCatalog.Validate(shouty));
    }

    [Fact]
    public void Every_slot_kind_has_exactly_one_catalog_entry()
    {
        foreach (var kind in Enum.GetValues<SlotKind>())
            Assert.Single(SlotTypeCatalog.All, s => s.Kind == kind);
    }

    [Fact]
    public void Seat_and_hazard_slots_carry_their_structural_rules()
    {
        var seat = SlotTypeCatalog.Get(SlotTypeCatalog.SeatSlotTypeId);
        Assert.Equal(SlotKind.Seat, seat.Kind);
        Assert.True(seat.Buildable);
        Assert.False(seat.Yields);

        var hazard = SlotTypeCatalog.All.Single(s => s.Kind == SlotKind.Hazard);
        Assert.False(hazard.Buildable);   // must be cleared into wildland first
    }

    [Fact]
    public void Lane_types_are_costed_and_a_corridor_beats_a_plain_rift()
    {
        foreach (var lane in LaneTypeCatalog.All)
            Assert.True(lane.CostMultiplierMilli > 0, $"Lane '{lane.LaneTypeId}' has non-positive cost.");

        Assert.True(LaneTypeCatalog.Get("corridor").CostMultiplierMilli
                    < LaneTypeCatalog.Get("rift").CostMultiplierMilli,
            "A stabilized corridor must be cheaper to march than a raw rift lane.");
    }

    [Fact]
    public void Ward_and_severed_are_lane_state_not_lane_types()
    {
        Assert.False(LaneTypeCatalog.IsKnown("warded"));
        Assert.False(LaneTypeCatalog.IsKnown("severed"));
    }

    [Fact]
    public void Faction_kinds_cover_the_fixed_set()
    {
        foreach (var kind in Enum.GetValues<WorldFactionKind>())
            Assert.True(FactionKindCatalog.IsKnown(FactionKindCatalog.IdOf(kind)));

        Assert.Equal(WorldFactionKind.Zomboss, FactionKindCatalog.Parse("zomboss"));
    }

    [Theory]
    [InlineData("nope-sector")]
    [InlineData("")]
    public void Unknown_sector_type_lookups_name_the_id(string id)
    {
        Assert.False(SectorTypeCatalog.IsKnown(id));
        var ex = Assert.Throws<ArgumentException>(() => SectorTypeCatalog.Get(id));
        Assert.Contains(id, ex.Message);
    }

    [Fact]
    public void Unknown_lookups_throw_across_every_catalog()
    {
        Assert.Throws<ArgumentException>(() => SlotTypeCatalog.Get("nope-slot"));
        Assert.Throws<ArgumentException>(() => LaneTypeCatalog.Get("nope-lane"));
        Assert.Throws<ArgumentException>(() => FactionKindCatalog.Parse("nope-faction"));
        Assert.False(SectorTypeCatalog.IsKnown(null));
        Assert.False(SlotTypeCatalog.IsKnown(null));
        Assert.False(LaneTypeCatalog.IsKnown(null));
    }
}
