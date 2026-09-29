using FusionRpg.Core.Delve;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.Delve;

/// <summary>
/// D5.2 (spec-delve-stage.md §5, §18 ask 1; spec-delve-scope.md:347-350) — <see cref="DelveProjection.For"/>,
/// the Core-layer half of the one Server-assembled projection `delve-stage` reads. Fixtures build a
/// small three-room linear chain (r0c0 — r0c1 — r0c2) directly, matching
/// <c>DelveWildEndpointsTests.cs</c>'s own established `WorldState` fixture convention rather than the
/// map-side `WorldTemplateCatalog` fixtures (a delve world is never that shape). `DungeonTuningHub` is
/// already configured for this whole assembly by `DungeonHubTestBootstrap`'s own `[ModuleInitializer]`,
/// which loads the REAL `gk-core/data/tuning/dungeon.v1.json` — confirmed `sight.lanes: 1`, `sight.scoutLanes:
/// 2`, so one lane away from a party is a glimpse and two lanes away is nothing, exactly
/// <c>VisibilityTests.cs</c>'s own map-side assertions for the identical constant.
/// </summary>
public class DelveProjectionTests
{
    const long Owner = 1;
    static DungeonTuning Tuning => DungeonTuningHub.Tuning;

    static WorldState Chain(params (string EntityId, string AtSectorId)[] parties) => new()
    {
        WorldId = "delve-projection-test", TemplateId = "layout.test", Seed = 1, CurrentTurn = 0,
        Sectors = new[]
        {
            new WorldSector { SectorId = "r0c0", TypeId = "fight" },
            new WorldSector { SectorId = "r0c1", TypeId = "elite" },
            new WorldSector { SectorId = "r0c2", TypeId = "cache" },
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l01", FromSectorId = "r0c0", ToSectorId = "r0c1", TypeId = "passage" },
            new WorldLane { LaneId = "l12", FromSectorId = "r0c1", ToSectorId = "r0c2", TypeId = "gated", GateKeyId = "key-1" },
        },
        // OwnerFactionId is deliberately NOT Owner's own id: DelveSight.ForParty's "this party's own
        // room is Full" step matches by EntityId alone (confirmed by reading DelveSight.cs directly),
        // so a party is found regardless of faction. Using a non-matching faction here means
        // Visibility.SeenBy's own floor (which DOES scan by OwnerFactionId) contributes NOTHING at
        // these sectors, isolating tests that specifically target DelveProjection.For's own per-party
        // merge from the floor's own, unrelated "never demote" protection -- also arguably more
        // representative of today's real state, where nothing wires a delve party's OwnerFactionId to
        // the player's own id at all (this class's own DelveProjection.For doc comment).
        Entities = parties
            .Select(p => new WorldEntity { EntityId = p.EntityId, Kind = WorldEntityKind.Warband, OwnerFactionId = "unwired-party-body", AtSectorId = p.AtSectorId })
            .ToList(),
    };

    static IReadOnlyList<DelveRoomStateFact> Rooms() => new[]
    {
        new DelveRoomStateFact("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, "evt-0", "resolved-fight", "resolved-arch-0", "[{\"a\":0}]", 3),
        new DelveRoomStateFact("r0c1", 0, 1, "elite", "room.elite-none-001", false, false, "key-1", "evt-1", "resolved-elite", "resolved-arch-1", "[{\"a\":1}]", 9),
        new DelveRoomStateFact("r0c2", 0, 2, "cache", "room.cache-none-001", false, false, null, "evt-2", "resolved-cache", "resolved-arch-2", "[{\"a\":2}]", 1),
    };

    static DelveProjectionRoom Room(DelveProjectionResult result, string sectorId) =>
        result.Rooms.Single(r => r.SectorId == sectorId);

    // ---- ownership ----------------------------------------------------------------------------

    [Fact]
    public void Wrong_player_returns_null()
    {
        var result = DelveProjection.For(
            requestingPlayerId: 2, delveOwnerPlayerId: Owner, delveRevision: 1,
            partyEntityIds: Array.Empty<long>(), rooms: Rooms(), world: Chain(), tuning: Tuning);

        Assert.Null(result);
    }

    [Fact]
    public void The_owning_player_gets_a_real_result()
    {
        var result = DelveProjection.For(
            requestingPlayerId: Owner, delveOwnerPlayerId: Owner, delveRevision: 1,
            partyEntityIds: Array.Empty<long>(), rooms: Rooms(), world: Chain(), tuning: Tuning);

        Assert.NotNull(result);
    }

    // ---- sight-gated content redaction (mutation targets) --------------------------------------

    [Fact]
    public void The_partys_own_room_is_Full_and_shows_everything()
    {
        var result = DelveProjection.For(
            Owner, Owner, delveRevision: 1, new long[] { 100 }, Rooms(),
            Chain((EntityId: "100", AtSectorId: "r0c0")), Tuning)!;

        var r = Room(result, "r0c0");
        Assert.Equal(SectorSight.Full, r.Sight);
        Assert.Equal("fight", r.Kind);
        Assert.Equal("room.fight-none-001", r.ArchetypeId);
        Assert.Equal("evt-0", r.EventId);
        Assert.Equal("resolved-fight", r.ResolvedKind);
        Assert.Equal("resolved-arch-0", r.ResolvedArchetypeId);
        Assert.Equal("[{\"a\":0}]", r.FloorJson);
        // Structural fields are never gated by sight.
        Assert.True(r.Visited);
        Assert.False(r.Cleared);
        Assert.Equal(0, r.RowIndex);
        Assert.Equal(0, r.ColIndex);
    }

    [Fact]
    public void A_glimpsed_room_names_its_kind_only()
    {
        // Party stands at r0c0; r0c1 is exactly one lane out -- a glimpse (sight.lanes: 1).
        var result = DelveProjection.For(
            Owner, Owner, delveRevision: 1, new long[] { 100 }, Rooms(),
            Chain((EntityId: "100", AtSectorId: "r0c0")), Tuning)!;

        var r = Room(result, "r0c1");
        Assert.Equal(SectorSight.Glimpse, r.Sight);
        Assert.Equal("elite", r.Kind); // spec-supplies-and-objects.md:362: "a glimpsed room names its kind only"
        Assert.Null(r.ArchetypeId);
        Assert.Null(r.EventId);
        Assert.Null(r.ResolvedKind);
        Assert.Null(r.ResolvedArchetypeId);
        Assert.Null(r.FloorJson);
        // Structural fields still survive a mere glimpse.
        Assert.Equal("key-1", r.KeyForLaneId);
    }

    [Fact]
    public void An_unlit_room_reveals_position_and_lanes_only_never_its_kind()
    {
        // r0c2 is two lanes from the party's own r0c0 -- past sight.lanes: 1, so SectorSight.None.
        var result = DelveProjection.For(
            Owner, Owner, delveRevision: 1, new long[] { 100 }, Rooms(),
            Chain((EntityId: "100", AtSectorId: "r0c0")), Tuning)!;

        var r = Room(result, "r0c2");
        Assert.Equal(SectorSight.None, r.Sight);
        Assert.Null(r.Kind); // SectorSight.None's own doc comment: "position and lanes only"
        Assert.Null(r.ArchetypeId);
        Assert.Null(r.EventId);
        Assert.Null(r.ResolvedKind);
        Assert.Null(r.ResolvedArchetypeId);
        Assert.Null(r.FloorJson);
        // "Position" survives -- the room still appears, it just carries no contents.
        Assert.Equal("r0c2", r.SectorId);
        Assert.Equal(0, r.RowIndex);
        Assert.Equal(2, r.ColIndex);
    }

    [Fact]
    public void Zero_parties_still_returns_a_well_defined_all_unlit_floor()
    {
        var result = DelveProjection.For(
            Owner, Owner, delveRevision: 1, Array.Empty<long>(), Rooms(), Chain(), Tuning)!;

        Assert.Equal(3, result.Rooms.Count);
        Assert.All(result.Rooms, r => Assert.Equal(SectorSight.None, r.Sight));
        Assert.All(result.Rooms, r => Assert.Null(r.Kind));
    }

    // ---- doors: never sight-gated ---------------------------------------------------------------

    [Fact]
    public void Doors_are_never_redacted_even_with_zero_parties()
    {
        var result = DelveProjection.For(
            Owner, Owner, delveRevision: 1, Array.Empty<long>(), Rooms(), Chain(), Tuning)!;

        Assert.Equal(2, result.Doors.Count);
        var gated = result.Doors.Single(d => d.LaneId == "l12");
        Assert.Equal("gated", gated.TypeId);
        Assert.Equal("key-1", gated.GateKeyId);
    }

    // ---- party positions --------------------------------------------------------------------

    [Fact]
    public void Party_position_reflects_the_entitys_real_sector()
    {
        var result = DelveProjection.For(
            Owner, Owner, delveRevision: 1, new long[] { 100 }, Rooms(),
            Chain((EntityId: "100", AtSectorId: "r0c1")), Tuning)!;

        var pos = result.Parties.Single(p => p.EntityId == 100);
        Assert.Equal("r0c1", pos.AtSectorId);
        Assert.Null(pos.OnLaneId);
    }

    [Fact]
    public void A_party_with_no_matching_world_entity_gets_a_null_position_not_a_crash()
    {
        // The real production gap this module names honestly: nothing today wires a WorldEntity for a
        // delve party (confirmed absent under Core/Delve/**), so a party id with no entity is the
        // realistic case, not an edge case -- must fail safe, never throw.
        var result = DelveProjection.For(
            Owner, Owner, delveRevision: 1, new long[] { 999 }, Rooms(), Chain(), Tuning)!;

        var pos = result.Parties.Single(p => p.EntityId == 999);
        Assert.Null(pos.AtSectorId);
        Assert.Null(pos.OnLaneId);
    }

    // ---- merging multiple parties' sight (mutation target) --------------------------------------

    [Fact]
    public void A_second_partys_weaker_overlay_never_downgrades_a_room_another_party_already_sees_Full()
    {
        // Party 100 at r0c0 (Full there, Glimpse at r0c1, None at r0c2).
        // Party 200 at r0c1 (Full there, Glimpse at r0c0 AND r0c2).
        // r0c0 must stay Full (100's own room) even though 200's own overlay only glimpses it.
        var world = Chain((EntityId: "100", AtSectorId: "r0c0"), (EntityId: "200", AtSectorId: "r0c1"));
        var result = DelveProjection.For(Owner, Owner, delveRevision: 1, new long[] { 100, 200 }, Rooms(), world, Tuning)!;

        Assert.Equal(SectorSight.Full, Room(result, "r0c0").Sight);
        Assert.Equal(SectorSight.Full, Room(result, "r0c1").Sight);
        Assert.Equal(SectorSight.Glimpse, Room(result, "r0c2").Sight); // one lane from 200's own room only
    }

    [Fact]
    public void Merge_order_does_not_matter_the_stronger_reading_always_wins()
    {
        var world = Chain((EntityId: "100", AtSectorId: "r0c0"), (EntityId: "200", AtSectorId: "r0c1"));
        var forward = DelveProjection.For(Owner, Owner, 1, new long[] { 100, 200 }, Rooms(), world, Tuning)!;
        var reversed = DelveProjection.For(Owner, Owner, 1, new long[] { 200, 100 }, Rooms(), world, Tuning)!;

        Assert.Equal(Room(forward, "r0c0").Sight, Room(reversed, "r0c0").Sight);
        Assert.Equal(Room(forward, "r0c1").Sight, Room(reversed, "r0c1").Sight);
        Assert.Equal(Room(forward, "r0c2").Sight, Room(reversed, "r0c2").Sight);
    }

    // ---- revision (mutation target) --------------------------------------------------------------

    [Fact]
    public void Revision_is_the_delve_revision_when_no_room_outranks_it()
    {
        var result = DelveProjection.For(Owner, Owner, delveRevision: 50, Array.Empty<long>(), Rooms(), Chain(), Tuning)!;

        // Rooms() carries revisions {3, 9, 1} -- all below 50.
        Assert.Equal(50, result.Revision);
    }

    [Fact]
    public void Revision_is_the_highest_room_revision_when_a_room_only_mutation_outranks_the_delve_row()
    {
        // MarkRoom bumps ONLY rpg_delve_rooms.revision, never rpg_delves.revision (RpgStore.Delve.cs) --
        // a stamp that read delveRevision alone would silently miss a room-only mutation.
        var result = DelveProjection.For(Owner, Owner, delveRevision: 2, Array.Empty<long>(), Rooms(), Chain(), Tuning)!;

        // Rooms() carries a revision of 9 on r0c1, above the delve's own 2.
        Assert.Equal(9, result.Revision);
    }

    // ---- null-arg guards --------------------------------------------------------------------------

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DelveProjection.For(Owner, Owner, 1, null!, Rooms(), Chain(), Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            DelveProjection.For(Owner, Owner, 1, Array.Empty<long>(), null!, Chain(), Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            DelveProjection.For(Owner, Owner, 1, Array.Empty<long>(), Rooms(), null!, Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            DelveProjection.For(Owner, Owner, 1, Array.Empty<long>(), Rooms(), Chain(), null!));
    }
}
