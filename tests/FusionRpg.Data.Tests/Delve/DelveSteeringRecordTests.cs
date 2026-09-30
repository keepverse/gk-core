using FusionRpg.Core.Delve;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>
/// D2.16a — the owner-ruled durable steering record. These tests use the real in-memory store and
/// real dungeon/world registries: the record is a Data fact, not a delegate supplied by a route.
/// </summary>
public sealed class DelveSteeringRecordTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    int _worldSeq;

    public DelveSteeringRecordTests()
    {
        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        DungeonTuningHub.Configure(DungeonTuningLoader.Parse(
            File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    const ulong Seed = 41UL;

    static WorldState BuildGraph(string worldId, int parties)
    {
        var entities = Enumerable.Range(0, parties)
            .Select(i => new WorldEntity
            {
                EntityId = $"party-{i}",
                Kind = WorldEntityKind.Warband,
                OwnerFactionId = "dave",
                AtSectorId = i == 0 ? "r0c0" : "r1c0",
            })
            .ToArray();

        return new WorldState
        {
            WorldId = worldId,
            TemplateId = "layout.short-narrow-linear-001",
            Seed = Seed,
            CurrentTurn = 0,
            Factions = new[]
            {
                new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
                new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = null },
            },
            Sectors = new[]
            {
                new WorldSector { SectorId = "r0c0", TypeId = "wild", OwnerFactionId = "dave" },
                new WorldSector { SectorId = "r1c0", TypeId = "wild" },
            },
            Lanes = new[] { new WorldLane { LaneId = "l0", FromSectorId = "r0c0", ToSectorId = "r1c0", TypeId = "passage" } },
            Entities = entities,
        };
    }

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "wild", "room.wild-none-001", true, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r1c0", 1, 0, "wild", "room.wild-none-001", false, false, null, null, null, null, "[]", 0),
    };

    long CreateDelve(string raidMode = "solo", int parties = 1)
    {
        var worldId = $"delve-steering-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, reason, delve) = _store.CreateDelve(
            1, "domain.fire-shallow-001", raidMode, "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", Seed, BuildGraph(worldId, parties), BuildRooms(), _rooms, _doors);
        Assert.True(ok, reason);
        return delve!.DelveId;
    }

    [Fact]
    public void A_new_delve_persists_selector_zero_instead_of_inferring_it_from_a_route()
    {
        var delveId = CreateDelve();

        var (ok, reason, steering) = _store.ReadDelveSteering(delveId);

        Assert.True(ok, reason);
        Assert.Equal(0, steering!.PartyIndex);
    }

    [Fact]
    public void A_deliberate_steer_to_none_is_not_read_back_as_selector_zero()
    {
        var delveId = CreateDelve();

        var changed = _store.TrySetDelveSteering(delveId, fromPartyIndex: 0, toPartyIndex: null);
        Assert.True(changed.Ok, changed.Reason);
        var read = _store.ReadDelveSteering(delveId);
        Assert.True(read.Ok, read.Reason);
        Assert.Null(read.Steering!.PartyIndex);
        var (valid, reason) = _store.ValidateDelveSteering(delveId, 0, "r0c0");
        Assert.False(valid);
        Assert.Equal("wild.party-not-steered", reason);
    }

    [Fact]
    public void A_nonzero_configured_party_round_trips_through_a_new_store_handle()
    {
        var delveId = CreateDelve("pair", 2);

        var (changed, reason, steering) = _store.TrySetDelveSteering(delveId, fromPartyIndex: 0, toPartyIndex: 1);
        Assert.True(changed, reason);
        Assert.Equal(1, steering!.PartyIndex);

        using var reopenedStore = _testStore.Reopen();
        var (read, readReason, reread) = reopenedStore.ReadDelveSteering(delveId);
        Assert.True(read, readReason);
        Assert.Equal(1, reread!.PartyIndex);
    }

    [Fact]
    public void An_out_of_range_party_refuses_without_changing_the_record_or_decision_log()
    {
        var delveId = CreateDelve("pair", 2);
        var before = _store.LoadDelve(delveId)!;

        var (changed, reason, steering) = _store.TrySetDelveSteering(delveId, fromPartyIndex: 0, toPartyIndex: 2);

        Assert.False(changed);
        Assert.Equal("delve.steering.party-invalid", reason);
        Assert.Null(steering);
        var after = _store.LoadDelve(delveId)!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.SteeringJson, after.SteeringJson);
        Assert.Equal(before.DecisionsJson, after.DecisionsJson);
    }

    [Fact]
    public void A_stale_from_party_refuses_without_overwriting_the_current_record()
    {
        var delveId = CreateDelve("pair", 2);
        Assert.True(_store.TrySetDelveSteering(delveId, 0, 1).Ok);
        var before = _store.LoadDelve(delveId)!;

        var (changed, reason, steering) = _store.TrySetDelveSteering(delveId, fromPartyIndex: 0, toPartyIndex: 0);

        Assert.False(changed);
        Assert.Equal("delve.steering.stale", reason);
        Assert.Null(steering);
        var after = _store.LoadDelve(delveId)!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.SteeringJson, after.SteeringJson);
        Assert.Equal(before.DecisionsJson, after.DecisionsJson);
    }

    [Fact]
    public void A_closed_delve_refuses_a_new_steering_record()
    {
        var delveId = CreateDelve();
        Assert.True(_store.CloseDelve(delveId, DelveStates.Extracted, archiveNow: false));
        var before = _store.LoadDelve(delveId)!;

        var (changed, reason, steering) = _store.TrySetDelveSteering(delveId, fromPartyIndex: 0, toPartyIndex: 0);

        Assert.False(changed);
        Assert.Equal("delve.steering.closed", reason);
        Assert.Null(steering);
        var after = _store.LoadDelve(delveId)!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.SteeringJson, after.SteeringJson);
    }

    [Fact]
    public void A_malformed_steering_record_refuses_by_name_and_is_not_overwritten()
    {
        var delveId = CreateDelve();
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE rpg_delves SET steering_json = 'not-json' WHERE delve_id = $id;";
            cmd.Parameters.AddWithValue("$id", delveId);
            cmd.ExecuteNonQuery();
        }
        var before = _store.LoadDelve(delveId)!;

        var (read, reason, steering) = _store.ReadDelveSteering(delveId);
        Assert.False(read);
        Assert.Equal("delve.steering.invalid", reason);
        Assert.Null(steering);

        var (changed, changeReason, changedSteering) = _store.TrySetDelveSteering(delveId, 0, 0);
        Assert.False(changed);
        Assert.Equal("delve.steering.invalid", changeReason);
        Assert.Null(changedSteering);
        Assert.Equal(before.SteeringJson, _store.LoadDelve(delveId)!.SteeringJson);
        Assert.Equal(before.Revision, _store.LoadDelve(delveId)!.Revision);
    }

    [Fact]
    public void A_nonzero_record_must_name_a_player_owned_warband_at_the_requested_location()
    {
        var delveId = CreateDelve("pair", 2);
        Assert.True(_store.TrySetDelveSteering(delveId, 0, 1).Ok);

        var (wrongRoom, wrongRoomReason) = _store.ValidateDelveSteering(delveId, 1, "r0c0");
        Assert.False(wrongRoom);
        Assert.Equal("delve.steering.party-location", wrongRoomReason);

        var (rightRoom, rightRoomReason) = _store.ValidateDelveSteering(delveId, 1, "r1c0");
        Assert.True(rightRoom, rightRoomReason);
    }

    [Fact]
    public void An_old_schema_row_gets_the_column_but_does_not_default_to_selector_zero()
    {
        using var test = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var cmd = seed.CreateCommand();
            cmd.CommandText = LegacyDelveDdl;
            cmd.ExecuteNonQuery();
            cmd.CommandText = """
                INSERT INTO rpg_delves
                  (delve_id, player_id, world_id, domain_id, raid_mode, rung_id, seed, state,
                   correlation_id, entered_utc, parties_json, decisions_json)
                VALUES (77, 1, 'legacy-delve', 'domain.fire-shallow-001', 'solo', 'hard', '41',
                        'Active', 'legacy-corr', '2026-01-01T00:00:00Z', '[]', '[]');
                """;
            cmd.ExecuteNonQuery();
        });

        using var columnsDb = SqliteConnectionFactory.Open(test.Store.HotPath);
        using var columns = columnsDb.CreateCommand();
        columns.CommandText = "PRAGMA table_info(rpg_delves);";
        using var reader = columns.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        Assert.Contains("steering_json", names);

        var (read, reason, steering) = test.Store.ReadDelveSteering(77);
        Assert.False(read);
        Assert.Equal("delve.steering.missing", reason);
        Assert.Null(steering);

        var (changed, changeReason, changedSteering) = test.Store.TrySetDelveSteering(77, 0, 0);
        Assert.False(changed);
        Assert.Equal("delve.steering.missing", changeReason);
        Assert.Null(changedSteering);
    }

    const string LegacyDelveDdl = """
        CREATE TABLE rpg_delves (
          delve_id INTEGER PRIMARY KEY AUTOINCREMENT, player_id INTEGER NOT NULL,
          world_id TEXT NOT NULL UNIQUE,
          domain_id TEXT NOT NULL, raid_mode TEXT NOT NULL, rung_id TEXT NOT NULL,
          seed TEXT NOT NULL,
          state TEXT NOT NULL,
          correlation_id TEXT NOT NULL, entered_utc TEXT NOT NULL, closed_utc TEXT,
          parties_json TEXT NOT NULL DEFAULT '[]',
          decisions_json TEXT NOT NULL DEFAULT '[]',
          souls_unbanked INTEGER NOT NULL DEFAULT 0, theta_run INTEGER NOT NULL DEFAULT 0,
          quests_json TEXT NOT NULL DEFAULT '[]',
          content_terms_json TEXT,
          revision INTEGER NOT NULL DEFAULT 0
        );
        """;
}
