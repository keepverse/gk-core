using FusionRpg.Core.Delve;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>D3.9 (spec-event-deck.md §8; spec-delve-scope.md:326's own "ask first" approval, TAKEN
/// 2026-09-05) — the store side of the two persisted repeat scopes (`per-domain`, `once-per-player`)
/// plus `MarkRoom`'s new `eventId`/`resolvedArchetypeId` writers and the per-delve reader they feed.
/// `EventDeck.Resolve`/`.Answer` (D3.3) have zero production HTTP caller today (D3.11's own
/// investigation, `DelveEndpoints.cs` carries no room-clear/answer route) — every fixture here builds a
/// real delve through the real `CreateDelve`/`MarkRoom` store surface directly, matching this program's
/// own established "provably correct, zero production trigger yet" posture.</summary>
public class EventSeenStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;

    public EventSeenStoreTests()
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

    long CreateOneDelve(long playerId, string domainId, string worldId, string correlationId, ulong seed = 1UL) =>
        _store.CreateDelve(
            playerId, domainId, "solo", "hard", correlationId, null,
            worldId, "layout.short-narrow-linear-001", seed,
            BuildRolledGraph(worldId), BuildRooms(), _rooms, _doors).Delve!.DelveId;

    // -----------------------------------------------------------------------------------------
    // MarkRoom's two new optional parameters -- byte-identical-without-them + the new columns.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void MarkRoom_with_no_arguments_beyond_the_old_shape_leaves_event_columns_null()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-mr-1", "corr-mr-1");
        _store.MarkRoom(delveId, "r0c0", visited: true, cleared: true, resolvedKind: "cage");

        var room = _store.LoadDelveRooms(delveId).Single(r => r.SectorId == "r0c0");
        Assert.True(room.Visited);
        Assert.True(room.Cleared);
        Assert.Equal("cage", room.ResolvedKind);
        Assert.Null(room.EventId);
        Assert.Null(room.ResolvedArchetypeId);
    }

    [Fact]
    public void MarkRoom_writes_eventId_and_resolvedArchetypeId_alongside_the_existing_columns()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-mr-2", "corr-mr-2");
        _store.MarkRoom(delveId, "r0c0", visited: true, resolvedKind: "event",
            eventId: "event.bargain-creature.allpeater-001", resolvedArchetypeId: "room.fight-none-001");

        var room = _store.LoadDelveRooms(delveId).Single(r => r.SectorId == "r0c0");
        Assert.True(room.Visited);
        Assert.Equal("event", room.ResolvedKind);
        Assert.Equal("event.bargain-creature.allpeater-001", room.EventId);
        Assert.Equal("room.fight-none-001", room.ResolvedArchetypeId);
    }

    [Fact]
    public void MarkRoom_can_write_eventId_alone_with_every_other_optional_argument_omitted()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-mr-3", "corr-mr-3");
        _store.MarkRoom(delveId, "r1c0", eventId: "event.a");

        var room = _store.LoadDelveRooms(delveId).Single(r => r.SectorId == "r1c0");
        Assert.False(room.Visited); // untouched -- not supplied
        Assert.False(room.Cleared); // untouched -- not supplied
        Assert.Null(room.ResolvedKind); // untouched -- not supplied
        Assert.Equal("event.a", room.EventId);
    }

    [Fact]
    public void MarkRoom_with_every_argument_null_is_a_true_no_op()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-mr-4", "corr-mr-4");
        var before = _store.LoadDelveRooms(delveId).Single(r => r.SectorId == "r0c0");
        _store.MarkRoom(delveId, "r0c0");
        var after = _store.LoadDelveRooms(delveId).Single(r => r.SectorId == "r0c0");
        Assert.Equal(before.Revision, after.Revision); // the early-return path -- no UPDATE, no revision bump
    }

    // -----------------------------------------------------------------------------------------
    // LoadPerDelveEventSeen -- per-delve scope is derived from rpg_delve_rooms.event_id, no new table.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void LoadPerDelveEventSeen_is_empty_for_a_freshly_created_delve()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-pd-1", "corr-pd-1");
        var seen = _store.LoadPerDelveEventSeen(delveId);
        Assert.Empty(seen);
    }

    [Fact]
    public void LoadPerDelveEventSeen_collects_every_drawn_event_id_across_rooms()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-pd-2", "corr-pd-2");
        _store.MarkRoom(delveId, "r0c0", eventId: "event.a");
        _store.MarkRoom(delveId, "r1c0", eventId: "event.b");
        // r2c0 (boss) never gets an event -- "no event may gate the boss" (spec §9) -- absent, not empty-string.

        var seen = _store.LoadPerDelveEventSeen(delveId);
        Assert.Equal(new HashSet<string> { "event.a", "event.b" }, seen);
    }

    [Fact]
    public void LoadPerDelveEventSeen_never_crosses_delves()
    {
        var delveOne = CreateOneDelve(1, "domain.fire-shallow-001", "delve-pd-3a", "corr-pd-3a");
        var delveTwo = CreateOneDelve(1, "domain.fire-shallow-001", "delve-pd-3b", "corr-pd-3b");
        _store.MarkRoom(delveOne, "r0c0", eventId: "event.a");
        _store.MarkRoom(delveTwo, "r0c0", eventId: "event.b");

        Assert.Equal(new[] { "event.a" }, _store.LoadPerDelveEventSeen(delveOne));
        Assert.Equal(new[] { "event.b" }, _store.LoadPerDelveEventSeen(delveTwo));
    }

    // -----------------------------------------------------------------------------------------
    // RecordEventSeen / LoadPersistedEventSeen -- the two scopes that outlive a delve.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void LoadPersistedEventSeen_is_empty_before_anything_is_recorded()
    {
        var (perDomain, oncePerPlayer) = _store.LoadPersistedEventSeen(1, "domain.fire-shallow-001");
        Assert.Empty(perDomain);
        Assert.Empty(oncePerPlayer);
    }

    [Fact]
    public void RecordEventSeen_perDomain_is_read_back_scoped_to_that_domain_only()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-rs-1", "corr-rs-1");
        _store.RecordEventSeen(1, "per-domain", "domain.fire-shallow-001", "event.a", delveId);

        var (perDomainA, _) = _store.LoadPersistedEventSeen(1, "domain.fire-shallow-001");
        Assert.Contains("event.a", perDomainA);

        var (perDomainOther, _) = _store.LoadPersistedEventSeen(1, "domain.ice-shallow-001");
        Assert.DoesNotContain("event.a", perDomainOther);
    }

    [Fact]
    public void RecordEventSeen_oncePerPlayer_is_read_back_regardless_of_domain()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-rs-2", "corr-rs-2");
        _store.RecordEventSeen(1, "once-per-player", "", "event.b", delveId);

        var (_, oncePerPlayerA) = _store.LoadPersistedEventSeen(1, "domain.fire-shallow-001");
        var (_, oncePerPlayerB) = _store.LoadPersistedEventSeen(1, "domain.ice-shallow-001");
        Assert.Contains("event.b", oncePerPlayerA);
        Assert.Contains("event.b", oncePerPlayerB); // domain-independent, per spec §8
    }

    [Fact]
    public void RecordEventSeen_never_leaks_across_players()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-rs-3", "corr-rs-3");
        _store.RecordEventSeen(1, "once-per-player", "", "event.c", delveId);

        var (_, oncePerPlayerOtherPlayer) = _store.LoadPersistedEventSeen(2, "domain.fire-shallow-001");
        Assert.DoesNotContain("event.c", oncePerPlayerOtherPlayer);
    }

    [Fact]
    public void RecordEventSeen_the_same_event_scope_and_key_twice_from_different_delves_stays_one_row()
    {
        // Spec §8 "Reset: never" -- once seen for (player, scope, scopeKey, eventId), a later draw of the
        // SAME event under the SAME scope in a DIFFERENT delve must not create a second row (delveId is
        // an audit attribute, not part of the uniqueness key).
        var delveOne = CreateOneDelve(1, "domain.fire-shallow-001", "delve-rs-4a", "corr-rs-4a");
        var delveTwo = CreateOneDelve(1, "domain.fire-shallow-001", "delve-rs-4b", "corr-rs-4b");

        _store.RecordEventSeen(1, "per-domain", "domain.fire-shallow-001", "event.d", delveOne);
        _store.RecordEventSeen(1, "per-domain", "domain.fire-shallow-001", "event.d", delveTwo); // must not throw

        var (perDomain, _) = _store.LoadPersistedEventSeen(1, "domain.fire-shallow-001");
        Assert.Single(perDomain); // still exactly one distinct event id, not two rows silently duplicating
        Assert.Contains("event.d", perDomain);
    }

    [Fact]
    public void RecordEventSeen_perDomain_and_oncePerPlayer_are_independent_sets()
    {
        var delveId = CreateOneDelve(1, "domain.fire-shallow-001", "delve-rs-5", "corr-rs-5");
        _store.RecordEventSeen(1, "per-domain", "domain.fire-shallow-001", "event.e", delveId);
        _store.RecordEventSeen(1, "once-per-player", "", "event.f", delveId);

        var (perDomain, oncePerPlayer) = _store.LoadPersistedEventSeen(1, "domain.fire-shallow-001");
        Assert.Contains("event.e", perDomain);
        Assert.DoesNotContain("event.f", perDomain);
        Assert.Contains("event.f", oncePerPlayer);
        Assert.DoesNotContain("event.e", oncePerPlayer);
    }

    [Fact]
    public void RecordEventSeen_rejects_a_blank_scope_or_eventId()
    {
        Assert.Throws<ArgumentException>(() => _store.RecordEventSeen(1, "", "domain.fire-shallow-001", "event.a", 1));
        Assert.Throws<ArgumentException>(() => _store.RecordEventSeen(1, "per-domain", "domain.fire-shallow-001", "", 1));
    }
}
