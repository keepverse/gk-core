using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>
/// npc-story-events NR2.23 (spec-delve-live-rooms.md): `EnterRoomWithDraw` is the ONE call that enters a
/// room with its draw — the move, the room's mark and every persisted seen scope, in one transaction.
///
/// <para>The three clauses this file exists for: one call does all three writes; a room that already
/// carries an `event_id` writes NOTHING and returns the stored id (spec §8's "written at the draw, not at
/// extraction"); and a fault planted after the mark leaves neither the move, the mark nor a seen row —
/// which is what makes the composition one transaction rather than three hopeful ones.</para>
///
/// <para>The store is the in-memory plan (`DataTestStore`), per the test-substrate standard; the room and
/// door catalogs are the real committed registries.</para>
/// </summary>
public class EnterRoomWithDrawTests : IDisposable
{
    const string WorldId = "delve-entry";
    const string DomainId = "domain.fire-shallow-001";
    const string DrawnEvent = "event.hunt-8h-001";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;

    public EnterRoomWithDrawTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var registryDir = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(registryDir, "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    /// <summary>fight -> cache (gated to boss), one party in the first room — the same minimal shape
    /// `DelveScopeTests` uses, so this file tests the entry, not the graph.</summary>
    static WorldState BuildRolledGraph(string worldId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = 42UL, CurrentTurn = 0,
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = null },
        },
        Sectors = new[]
        {
            new WorldSector { SectorId = "r0c0", TypeId = "fight", Climate = null, OwnerFactionId = "dave" },
            new WorldSector { SectorId = "r1c0", TypeId = "cache", Climate = null },
            new WorldSector { SectorId = "r2c0", TypeId = "boss", Climate = null },
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l0", FromSectorId = "r0c0", ToSectorId = "r1c0", TypeId = "passage" },
            new WorldLane { LaneId = "l1", FromSectorId = "r1c0", ToSectorId = "r2c0", TypeId = "gated", GateKeyId = "key.l1" },
        },
        Entities = new[]
        {
            new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave", AtSectorId = "r0c0" },
        },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r1c0", 1, 0, "cache", "room.cache-none-001", false, false, "l1", null, null, null, "[]", 0),
        new DelveRoomRow("r2c0", 2, 0, "boss", "room.boss-none-001", false, false, null, null, null, null, "[]", 0),
    };

    long CreateDelve(string worldId, string correlationId) =>
        _store.CreateDelve(
            1, DomainId, "solo", "hard", correlationId, null,
            worldId, "layout.short-narrow-linear-001", 1UL,
            BuildRolledGraph(worldId), BuildRooms(), _rooms, _doors).Delve!.DelveId;

    static EventSeenSets Seen(string perDomain, string oncePerPlayer) => new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(new[] { perDomain }, StringComparer.Ordinal),
        new HashSet<string>(new[] { oncePerPlayer }, StringComparer.Ordinal),
        new HashSet<EventFilters.EventCell>());

    string? PartySector(string worldId) =>
        _store.LoadWorldState(worldId)!.Entities.Single(e => e.EntityId == "party-0").AtSectorId;

    DelveRoomRow Room(long delveId, string sectorId) =>
        _store.LoadDelveRooms(delveId).Single(r => r.SectorId == sectorId);

    // ---- one call, three writes --------------------------------------------------------------------

    [Fact]
    public void One_call_moves_the_party_marks_the_room_and_records_every_persisted_scope()
    {
        var delveId = CreateDelve(WorldId, "corr-entry");

        var (ok, reason, eventId) = _store.EnterRoomWithDraw(
            delveId, WorldId, playerId: 1, partyEntityId: "party-0", toSectorId: "r1c0",
            doorCatalog: _doors, drawnEventId: DrawnEvent, domainId: DomainId,
            seen: Seen("event.room-a", "event.once-b"));

        Assert.True(ok, reason);
        Assert.Equal("ok", reason);
        Assert.Equal(DrawnEvent, eventId);
        Assert.Equal("r1c0", PartySector(WorldId));

        var room = Room(delveId, "r1c0");
        Assert.True(room.Visited);
        Assert.Equal(DrawnEvent, room.EventId);

        var (perDomain, oncePerPlayer) = _store.LoadPersistedEventSeen(1, DomainId);
        Assert.Contains("event.room-a", perDomain);
        Assert.Contains("event.once-b", oncePerPlayer);
    }

    [Fact]
    public void A_room_with_a_stored_event_id_writes_nothing_and_returns_it()
    {
        var delveId = CreateDelve(WorldId, "corr-stored");
        _store.EnterRoomWithDraw(
            delveId, WorldId, 1, "party-0", "r1c0", _doors, DrawnEvent, DomainId,
            Seen("event.room-a", "event.once-b"));

        // The party walks back, then re-enters: the room already carries an event, so the second call is
        // a READ — no move, no mark, no new seen row, and the STORED id comes back, not the new one.
        var (back, _) = _store.MoveParty(delveId, WorldId, "party-0", "r0c0", _doors);
        Assert.True(back);

        var (ok, reason, eventId) = _store.EnterRoomWithDraw(
            delveId, WorldId, 1, "party-0", "r1c0", _doors, "event.something-else", DomainId,
            Seen("event.room-c", "event.once-d"));

        Assert.True(ok, reason);
        Assert.Equal("event.already-drawn", reason);
        Assert.Equal(DrawnEvent, eventId);
        Assert.Equal("r0c0", PartySector(WorldId));                 // not moved
        Assert.Equal(DrawnEvent, Room(delveId, "r1c0").EventId);    // not re-marked

        var (perDomain, oncePerPlayer) = _store.LoadPersistedEventSeen(1, DomainId);
        Assert.DoesNotContain("event.room-c", perDomain);
        Assert.DoesNotContain("event.once-d", oncePerPlayer);
    }

    [Fact]
    public void A_fault_after_the_mark_leaves_neither_the_move_the_mark_nor_a_seen_row()
    {
        var delveId = CreateDelve(WorldId, "corr-fault");
        _store.EnterRoomMidTestHook = () => throw new InvalidOperationException("planted fault after the mark");

        try
        {
            Assert.Throws<InvalidOperationException>(() => _store.EnterRoomWithDraw(
                delveId, WorldId, 1, "party-0", "r1c0", _doors, DrawnEvent, DomainId,
                Seen("event.room-a", "event.once-b")));
        }
        finally
        {
            _store.EnterRoomMidTestHook = null;
        }

        Assert.Equal("r0c0", PartySector(WorldId));                  // the move is gone
        var room = Room(delveId, "r1c0");
        Assert.False(room.Visited);                                  // the mark is gone
        Assert.Null(room.EventId);
        var (perDomain, oncePerPlayer) = _store.LoadPersistedEventSeen(1, DomainId);
        Assert.Empty(perDomain);                                     // and so is every seen row
        Assert.Empty(oncePerPlayer);
    }

    [Fact]
    public void A_refused_move_enters_nothing()
    {
        var delveId = CreateDelve(WorldId, "corr-refused");

        // r2c0 is two lanes away from the party's room, so the lane lookup refuses before any write.
        var (ok, reason, eventId) = _store.EnterRoomWithDraw(
            delveId, WorldId, 1, "party-0", "r2c0", _doors, DrawnEvent, DomainId, Seen("a", "b"));

        Assert.False(ok);
        Assert.Equal("lane.unknown", reason);
        Assert.Null(eventId);
        Assert.Equal("r0c0", PartySector(WorldId));
        Assert.Null(Room(delveId, "r2c0").EventId);
        var (perDomain, oncePerPlayer) = _store.LoadPersistedEventSeen(1, DomainId);
        Assert.Empty(perDomain);
        Assert.Empty(oncePerPlayer);
    }

    [Fact]
    public void An_entry_with_no_draw_is_refused_by_the_signature_not_written_halfway()
    {
        var delveId = CreateDelve(WorldId, "corr-nodraw");

        Assert.Throws<ArgumentException>(() => _store.EnterRoomWithDraw(
            delveId, WorldId, 1, "party-0", "r1c0", _doors, drawnEventId: "  ", domainId: DomainId,
            seen: EventSeenSets.Empty));

        Assert.Equal("r0c0", PartySector(WorldId));
        Assert.Null(Room(delveId, "r1c0").EventId);
    }
}
