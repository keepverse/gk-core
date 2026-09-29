using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W2 acceptance (spec-world-model.md): the authored template builds deterministically, the model
/// keeps stable order, and each of the seven creation rules rejects its own malformed world.
/// </summary>
public class WorldTemplateTests
{
    static WorldState FirstLight() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 42);

    [Fact]
    public void First_light_builds_deterministically_and_validates()
    {
        var a = FirstLight();
        var b = FirstLight();

        Assert.Equal(WorldCanonical.Write(a), WorldCanonical.Write(b));
        WorldValidation.Validate(a); // throws if the authored template is malformed
        Assert.Equal(42UL, a.Seed);
        Assert.Equal(6, a.Sectors.Count);
    }

    [Fact]
    public void First_light_teaches_the_shapes_wave_one_needs()
    {
        var w = FirstLight();

        // A no-base sector: traversable ground that can never become a capital.
        Assert.Contains(w.Sectors, s =>
            SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.NoBase));

        // At least one guarded slot, so `clear` has something to do.
        Assert.Contains(w.Sectors.SelectMany(s => s.Slots), sl =>
            sl.GuardState == GuardState.Intact && !string.IsNullOrEmpty(sl.GuardWaveId));

        // The homeworld exists, is Dave's, and holds a Seat.
        var home = w.Sectors.Single(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home));
        Assert.Equal(w.Factions.Single(f => f.Kind == WorldFactionKind.Player).FactionId, home.OwnerFactionId);
        Assert.Contains(home.Slots, sl => sl.SlotTypeId == SlotTypeCatalog.SeatSlotTypeId);

        // Something of Dave's stands on the map at turn zero.
        Assert.Contains(w.Entities, e => e.Kind == WorldEntityKind.Legion);
    }

    [Fact]
    public void Collections_are_in_stable_sorted_order()
    {
        var w = FirstLight();

        Assert.Equal(w.Sectors.Select(s => s.SectorId).OrderBy(x => x, StringComparer.Ordinal), w.Sectors.Select(s => s.SectorId));
        Assert.Equal(w.Lanes.Select(l => l.LaneId).OrderBy(x => x, StringComparer.Ordinal), w.Lanes.Select(l => l.LaneId));
        Assert.Equal(w.Entities.Select(e => e.EntityId).OrderBy(x => x, StringComparer.Ordinal), w.Entities.Select(e => e.EntityId));
        Assert.Equal(w.Factions.Select(f => f.FactionId).OrderBy(x => x, StringComparer.Ordinal), w.Factions.Select(f => f.FactionId));
        foreach (var s in w.Sectors)
            Assert.Equal(Enumerable.Range(0, s.Slots.Count), s.Slots.Select(sl => sl.SlotIndex));
    }

    [Fact]
    public void Unknown_template_ids_name_the_id()
    {
        var ex = Assert.Throws<ArgumentException>(() => WorldTemplateCatalog.Build("no-such-template", 1));
        Assert.Contains("no-such-template", ex.Message);
    }

    // ---- the seven creation rules, one rejecting case each ----

    [Fact]
    public void Rule1_unknown_catalog_ids_reject()
    {
        var w = FirstLight();
        var broken = w with
        {
            Sectors = Replace(w.Sectors, 0, s => s with { TypeId = "not-a-sector-type" })
        };
        Assert.Contains("not-a-sector-type", Throws(broken));
    }

    [Fact]
    public void Rule2_lane_shape_rejects_self_lanes_dangling_ends_and_duplicates()
    {
        var w = FirstLight();
        var first = w.Lanes[0];

        var selfLane = w with { Lanes = Replace(w.Lanes, 0, l => l with { ToSectorId = l.FromSectorId }) };
        Assert.Contains("itself", Throws(selfLane));

        var dangling = w with { Lanes = Replace(w.Lanes, 0, l => l with { ToSectorId = "nowhere" }) };
        Assert.Contains("nowhere", Throws(dangling));

        var duplicate = w with
        {
            Lanes = w.Lanes.Append(first with
            {
                LaneId = "zz-duplicate",
                FromSectorId = first.ToSectorId,   // reversed pair — still the same undirected edge
                ToSectorId = first.FromSectorId
            }).OrderBy(l => l.LaneId, StringComparer.Ordinal).ToList()
        };
        Assert.Contains("Duplicate lane", Throws(duplicate));
    }

    [Fact]
    public void Rule3_an_unreachable_sector_rejects()
    {
        var w = FirstLight();
        var orphaned = w with { Lanes = w.Lanes.Where(l => l.FromSectorId != "verdant-shelf" && l.ToSectorId != "verdant-shelf").ToList() };
        Assert.Contains("verdant-shelf", Throws(orphaned));
    }

    [Fact]
    public void Rule4_the_world_needs_exactly_one_player_owned_homeworld()
    {
        var w = FirstLight();

        var homeless = w with
        {
            Sectors = Replace(w.Sectors, IndexOfHome(w), s => s with { TypeId = "stable" })
        };
        Assert.Contains("homeworld", Throws(homeless), StringComparison.OrdinalIgnoreCase);

        var notDaves = w with
        {
            Sectors = Replace(w.Sectors, IndexOfHome(w), s => s with { OwnerFactionId = null })
        };
        Assert.Contains("player", Throws(notDaves), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule5_seat_counts_follow_the_sector_type()
    {
        var w = FirstLight();

        // A base-capable sector with its Seat removed.
        var seatless = w with
        {
            Sectors = Replace(w.Sectors, IndexOfHome(w),
                s => s with { Slots = Reindex(s.Slots.Where(sl => sl.SlotTypeId != SlotTypeCatalog.SeatSlotTypeId)) })
        };
        Assert.Contains("Seat", Throws(seatless));

        // A no-base sector handed a Seat.
        var noBaseIndex = w.Sectors.ToList().FindIndex(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.NoBase));
        var illegalSeat = w with
        {
            Sectors = Replace(w.Sectors, noBaseIndex, s => s with
            {
                Slots = Reindex(s.Slots.Append(new WorldSlot
                {
                    SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId
                }))
            })
        };
        Assert.Contains("Seat", Throws(illegalSeat));
    }

    [Fact]
    public void Rule6_slot_indexes_are_contiguous_and_the_type_must_allow_the_slot()
    {
        var w = FirstLight();

        var gappy = w with
        {
            Sectors = Replace(w.Sectors, 0, s => s with
            {
                Slots = s.Slots.Select((sl, i) => i == 0 ? sl with { SlotIndex = 7 } : sl).ToList()
            })
        };
        Assert.Contains("contiguous", Throws(gappy), StringComparison.OrdinalIgnoreCase);

        var homeIndex = IndexOfHome(w);
        var disallowed = w with
        {
            Sectors = Replace(w.Sectors, homeIndex, s => s with
            {
                Slots = Reindex(s.Slots.Append(new WorldSlot { SlotTypeId = "shard-vein" }))
            })
        };
        Assert.Contains("shard-vein", Throws(disallowed));
    }

    [Fact]
    public void Rule7_an_entity_is_at_a_sector_or_on_a_lane_never_both_and_never_neither()
    {
        var w = FirstLight();

        var nowhere = w with { Entities = Replace(w.Entities, 0, e => e with { AtSectorId = null, OnLaneId = null }) };
        Assert.Contains("neither", Throws(nowhere), StringComparison.OrdinalIgnoreCase);

        var both = w with { Entities = Replace(w.Entities, 0, e => e with { OnLaneId = w.Lanes[0].LaneId }) };
        Assert.Contains("both", Throws(both), StringComparison.OrdinalIgnoreCase);

        var ghostSector = w with { Entities = Replace(w.Entities, 0, e => e with { AtSectorId = "nowhere" }) };
        Assert.Contains("nowhere", Throws(ghostSector));
    }

    // ---- helpers ----

    static string Throws(WorldState w) =>
        Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(w)).Message;

    static int IndexOfHome(WorldState w) =>
        w.Sectors.ToList().FindIndex(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home));

    static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> source, int index, Func<T, T> edit) =>
        source.Select((item, i) => i == index ? edit(item) : item).ToList();

    static IReadOnlyList<WorldSlot> Reindex(IEnumerable<WorldSlot> slots) =>
        slots.Select((sl, i) => sl with { SlotIndex = i }).ToList();
}
