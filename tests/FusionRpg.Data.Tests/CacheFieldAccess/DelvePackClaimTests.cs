using System.Text.Json;
using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.CacheFieldAccess;

/// <summary>
/// Task 4C.4 part 1 (`cache-field-access` §2 delve pack-claim — deployment-hierarchy module 5):
/// <c>ClaimCorpseCacheIntoPackUnlocked</c> moves a reachable <c>delve_room</c> cache into the claiming
/// party's live <c>PackGrid</c> via <c>PackArranger.Arrange</c> (spill to <c>floor_json</c> when full),
/// deleting claimed rows in the same transaction (move-never-copy). Retrieval-mission module 6 is a
/// separate follow-up and is NOT built here. All stores are in-memory
/// (<see cref="DataTestStore.Create"/>); no temp dir, nothing to delete.
/// </summary>
[Trait("VerificationId", "data.cache-field-access")]
public class DelvePackClaimTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    readonly DungeonTuning _tuning;
    int _worldSeq;

    public DelvePackClaimTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
        _tuning = DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries);
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static WorldState BuildGraph(string worldId) => new()
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
            new WorldLane { LaneId = "l1", FromSectorId = "r1c0", ToSectorId = "r2c0", TypeId = "passage" },
        },
        Entities = new[]
        {
            new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave", AtSectorId = "r0c0" },
        },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r1c0", 1, 0, "cache", "room.cache-none-001", false, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r2c0", 2, 0, "boss", "room.boss-none-001", false, false, null, null, null, null, "[]", 0),
    };

    DelveRow CreateDelve(long playerId = 1)
    {
        var worldId = $"delve-pack-claim-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, _, delve) = _store.CreateDelve(
            playerId, "domain.fire-shallow-001", "solo", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", 1UL, BuildGraph(worldId), BuildRooms(), _rooms, _doors);
        Assert.True(ok);
        return delve!;
    }

    // ---- fixture helpers -------------------------------------------------------------

    string SeedDelveCache(
        long delveId, int row, int col,
        (string Kind, string? Instance, string? Container, long? Qty)[] rows,
        int inVoid = 0, string placeKind = "delve_room", string? placeRefOverride = null)
    {
        var cacheId = "cc_" + Guid.NewGuid().ToString("N");
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using (var ins = db.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache
                  (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision)
                VALUES ($id, $k, $r, 'death', $now, NULL, $v, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRefOverride ?? $"delve:{delveId}:r{row}:c{col}");
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            ins.Parameters.AddWithValue("$v", inVoid);
            Assert.Equal(1, ins.ExecuteNonQuery());
        }
        for (var i = 0; i < rows.Length; i++)
        {
            using var item = db.CreateCommand();
            item.CommandText = """
                INSERT INTO rpg_corpse_cache_item
                  (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
                VALUES ($id, $seq, $kind, $inst, $cont, $qty, 'dead-specimen');
                """;
            item.Parameters.AddWithValue("$id", cacheId);
            item.Parameters.AddWithValue("$seq", i);
            item.Parameters.AddWithValue("$kind", rows[i].Kind);
            item.Parameters.AddWithValue("$inst", (object?)rows[i].Instance ?? DBNull.Value);
            item.Parameters.AddWithValue("$cont", (object?)rows[i].Container ?? DBNull.Value);
            item.Parameters.AddWithValue("$qty", (object?)rows[i].Qty ?? DBNull.Value);
            Assert.Equal(1, item.ExecuteNonQuery());
        }
        return cacheId;
    }

    // The spec §Real-gap probe in test form: every row resolves to a 1×1 footprint; stacks keep
    // their container id as the RefId, instances resolve to a test base type.
    static CorpseCachePackShape UnitShape(CorpseCachePackItem row) =>
        new(row.Kind == "stack" ? row.ContainerId ?? "cont-?" : "test-gear", 1, 1);

    static string NewInstanceTag() => "inst-" + Guid.NewGuid().ToString("N");

    int CacheItemCount(string cacheId) => _store.ListCorpseCacheItems(cacheId).Count;

    IReadOnlyList<PackCell> PackCellsOf(long delveId, long entityId) =>
        _store.LoadDelve(delveId)!.Parties.Single(p => p.EntityId == entityId).Pack?.Cells
        ?? (IReadOnlyList<PackCell>)Array.Empty<PackCell>();

    bool HasPack(long delveId, long entityId) =>
        _store.LoadDelve(delveId)!.Parties.Single(p => p.EntityId == entityId).Pack is not null;

    List<PackItem> FloorOf(long delveId, string sectorId) =>
        JsonSerializer.Deserialize<List<PackItem>>(
            _store.LoadDelveRooms(delveId).Single(r => r.SectorId == sectorId).FloorJson)
        ?? new List<PackItem>();

    int ClaimLogRowCount()
    {
        try
        {
            using var db = SqliteConnectionFactory.Open(_store.HotPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rpg_corpse_cache_claim_log;";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table"))
        {
            return 0; // no claim (not even a refusal past the correlation check) has run yet
        }
    }

    // ---- AC1: full claim moves every row into the pack, cache empties -----------------

    [Fact]
    public void Full_claim_moves_every_row_into_pack_and_empties_cache()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        var inst0 = NewInstanceTag();
        var inst1 = NewInstanceTag();
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)inst0, (string?)null, (long?)null),
            ("stack", (string?)null, (string?)"cont-rations", (long?)7),
            ("instance", (string?)inst1, (string?)null, (long?)null),
        });

        var result = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-full-1", UnitShape);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { 0, 1, 2 }, result.ClaimedSeqs);
        Assert.Equal(new[] { 0, 1, 2 }, result.PlacedSeqs);
        Assert.Empty(result.FloorSeqs);

        // Move-never-copy, proven by count: cache empties, pack gains an equal number of cells.
        Assert.Equal(0, CacheItemCount(cacheId));
        var cells = PackCellsOf(delve.DelveId, 0);
        Assert.Equal(3, cells.Count);
        Assert.Equal(inst0, cells[0].Item.InstanceId);
        Assert.Equal(PackItemOrigin.Haul, cells[0].Item.Origin); // ordinary haul from here on (spec §5)
        Assert.Equal("cont-rations", cells[1].Item.RefId);
        Assert.Equal(7, cells[1].Item.Qty);
        Assert.Equal(inst1, cells[2].Item.InstanceId);
        Assert.Empty(FloorOf(delve.DelveId, "r1c0"));

        // Claimed instances are pack-locked (already owned — no AcquireItem, no assignment write).
        Assert.True(_store.IsPackLocked(inst0));
        Assert.True(_store.IsPackLocked(inst1));

        // One audit entry on the generic decision log + one replay-safety row.
        var decisions = _store.LoadDelve(delve.DelveId)!.DecisionsJson;
        Assert.Contains("cache.claim", decisions);
        using var doc = JsonDocument.Parse(decisions);
        var entry = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "cache.claim");
        Assert.Equal(3, entry.GetProperty("payload").GetProperty("claimedSeqs").GetArrayLength());
        Assert.Equal(1, ClaimLogRowCount());
    }

    // ---- AC2: over-full spills to the floor, never loses, never refuses ---------------

    [Fact]
    public void Overfull_pack_spills_overflow_to_floor_and_empties_cache()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        // 39 of 40 cells occupied: exactly one free cell left for a 3-row claim.
        var fill = Enumerable.Range(0, 39)
            .Select(i => new PackCell(
                i / 10, i % 10,
                new PackItem("Material", $"fill-{i}", null, 1, 1, 1, GrantIndex: i, PackItemOrigin.Haul)))
            .ToList();
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10, fill));
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });

        var result = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-spill-1", UnitShape);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { 0, 1, 2 }, result.ClaimedSeqs); // never a whole-claim refusal
        Assert.Equal(new[] { 0 }, result.PlacedSeqs); // seq order: first takes the last free cell
        Assert.Equal(new[] { 1, 2 }, result.FloorSeqs);

        // Nothing lost: cache empties, pack +1, floor +2 — Arrange's own contract (every input in
        // exactly one of Grid/Floor) holds end to end.
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(40, PackCellsOf(delve.DelveId, 0).Count);
        var floor = FloorOf(delve.DelveId, "r1c0");
        Assert.Equal(2, floor.Count);
        Assert.All(floor, item => Assert.Equal(PackItemOrigin.Haul, item.Origin));
    }

    [Fact]
    public void Oversize_footprint_throws_like_a_reveal_and_writes_nothing()
    {
        // Evidence for the spill-vs-refuse decision: Arrange's only non-spill path is the
        // pack.footprint-exceeds-grid throw (PackArranger.cs:22). The claim propagates it — a loud
        // content error, never a silent skip or partial move.
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10, Array.Empty<PackCell>()));
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });

        Assert.Throws<PackRejection>(() => _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-huge-1",
            _ => new CorpseCachePackShape("test-gear", 11, 1))); // wider than the 10-col grid

        Assert.Equal(1, CacheItemCount(cacheId)); // untouched — the transaction rolled back
        Assert.Empty(PackCellsOf(delve.DelveId, 0));
        Assert.Empty(FloorOf(delve.DelveId, "r1c0"));
        Assert.Equal(0, ClaimLogRowCount());
    }

    // ---- AC3: unreachable party refused, zero writes -----------------------------------

    [Fact]
    public void Party_elsewhere_refuses_unreachable_and_leaves_state_intact()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0" }); // never reached r1c0
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });

        var refused = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-far-1", UnitShape);

        Assert.False(refused.Ok);
        Assert.Equal("cache.unreachable", refused.Reason);
        Assert.Equal(1, CacheItemCount(cacheId));
        Assert.False(HasPack(delve.DelveId, 0)); // no pack row conjured by a refusal
        Assert.Equal(0, ClaimLogRowCount());
    }

    [Fact]
    public void Closed_raid_refuses_before_any_row_is_touched()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });
        Assert.True(_store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false));

        var refused = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-closed-1", UnitShape);

        Assert.False(refused.Ok);
        Assert.Equal("cache.unreachable", refused.Reason);
        Assert.Equal(1, CacheItemCount(cacheId));
        Assert.Equal(0, ClaimLogRowCount());
    }

    [Fact]
    public void Forged_voided_and_wrong_kind_caches_refuse_unreachable()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });

        var forged = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, "cc_does_not_exist", "corr-pack-forge-1", UnitShape);
        Assert.False(forged.Ok);
        Assert.Equal("cache.unreachable", forged.Reason);

        var voided = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        }, inVoid: 1);
        var refusedVoid = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, voided, "corr-pack-void-1", UnitShape);
        Assert.False(refusedVoid.Ok);
        Assert.Equal("cache.unreachable", refusedVoid.Reason);
        Assert.Equal(1, CacheItemCount(voided)); // module 6's rows, never this verb's

        // Structural exclusion: a lawn cache is never returned by the delve-room path.
        var lawn = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        }, placeKind: "lawn", placeRefOverride: "match-1");
        var refusedLawn = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, lawn, "corr-pack-lawn-1", UnitShape);
        Assert.False(refusedLawn.Ok);
        Assert.Equal("cache.unreachable", refusedLawn.Reason);
        Assert.Equal(1, CacheItemCount(lawn));
    }

    [Fact]
    public void Missing_correlation_refuses_before_any_work()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });

        var refused = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "   ", UnitShape);

        Assert.False(refused.Ok);
        Assert.Equal("correlation.missing", refused.Reason);
        Assert.Equal(1, CacheItemCount(cacheId));
        Assert.Equal(0, ClaimLogRowCount());
    }

    // ---- Replay safety + empty + second party ------------------------------------------

    [Fact]
    public void Replayed_claim_returns_identical_result_and_writes_nothing()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
            ("stack", (string?)null, (string?)"cont-rations", (long?)3),
        });

        var first = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-replay-1", UnitShape);
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(2, PackCellsOf(delve.DelveId, 0).Count);
        Assert.Equal(1, ClaimLogRowCount());

        var second = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-replay-1",
            _ => throw new InvalidOperationException("replay must never re-resolve shapes"));

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(2, PackCellsOf(delve.DelveId, 0).Count);
        Assert.Equal(1, ClaimLogRowCount());
    }

    [Fact]
    public void Empty_cache_claim_logs_noop_ok_and_replays_identically()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, Array.Empty<(string, string?, string?, long?)>());

        var first = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-empty-1", UnitShape);

        Assert.True(first.Ok, first.Reason);
        Assert.Empty(first.ClaimedSeqs);
        var second = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-empty-1", UnitShape);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(1, ClaimLogRowCount());
    }

    [Fact]
    public void Second_party_claims_only_what_the_first_left_never_a_duplicate()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 7, new[] { "r1c0" }); // present
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0" }); // absent
        var inst0 = NewInstanceTag();
        var inst1 = NewInstanceTag();
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)inst0, (string?)null, (long?)null),
            ("instance", (string?)inst1, (string?)null, (long?)null),
        });

        var first = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 7, cacheId, "corr-pack-second-1", UnitShape);
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(new[] { 0, 1 }, first.ClaimedSeqs);

        // The late party reaches the room and gets exactly the remainder (here: nothing) — the
        // spill model always empties the cache, so the proof is no-duplicate, not partial.
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        var late = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-second-2", UnitShape);
        Assert.True(late.Ok, late.Reason);
        Assert.Empty(late.ClaimedSeqs);

        var allIds = PackCellsOf(delve.DelveId, 7).Select(c => c.Item.InstanceId)
            .Concat(PackCellsOf(delve.DelveId, 0).Select(c => c.Item.InstanceId))
            .Concat(FloorOf(delve.DelveId, "r1c0").Select(i => i.InstanceId))
            .Where(id => id is not null)
            .ToList();
        Assert.Equal(2, allIds.Count);
        Assert.Contains(inst0, allIds);
        Assert.Contains(inst1, allIds);
    }

    // ---- Claimed haul extracts like ordinary haul (no auto-re-equip) -------------------

    [Fact]
    public void Claimed_haul_unlocks_at_extraction_with_no_assignment_row()
    {
        // The verb writes no rpg_item_assignment row (proven by the source scan below); extraction
        // then treats the claimed cell as ordinary haul: lock released, instance still owned.
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });
        var realId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = "item.test-relic", RollSeed = 1, CatalogRevision = 0, ThetaContent = 20, ContentScaleMilli = 1000,
        }, instanceId: "claim-real-1");
        _store.SaveItem(new RpgItemRow { InstanceId = realId, PlayerId = "1", OriginKind = "drop" });
        var cacheId = SeedDelveCache(delve.DelveId, 1, 0, new[]
        {
            ("instance", (string?)realId, (string?)null, (long?)null),
        });

        var result = _store.ClaimCorpseCacheIntoPack(
            delve.DelveId, 0, cacheId, "corr-pack-extract-1", UnitShape);
        Assert.True(result.Ok, result.Reason);
        Assert.True(_store.IsPackLocked(realId));

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.False(_store.IsPackLocked(realId));
        Assert.NotNull(_store.GetItem(realId)); // still owned — extraction never deletes haul
    }

    // ---- Structural proofs (spec §Testing: the_module_never_rolls + §Boundaries) --------

    [Fact]
    public void The_module_never_rolls_writes_no_assignments_and_reuses_placement()
    {
        // This walk used to look for a directory holding `src/FusionRpg.Injector`, which resolved only
        // while every repository was one tree. The Injector is gk-fusion's now, so from a test output
        // directory inside gk-core the walk passed every ancestor and reached the drive root: `dir`
        // came back null and the test failed on its own `Assert.NotNull(dir)` - reaching NONE of the
        // seven structural assertions below it about the module it exists to prove.
        //
        // The file under test is gk-core's, so the repository is named by the resolver rather than by a
        // marker directory that has since moved to another repository. The existence check is kept and
        // made explicit so a wrong root names the path it wanted, instead of surfacing as a bare
        // FileNotFoundException from ReadAllText.
        var sourcePath = Path.Combine(
            KeepverseRoots.Core(), "src", "FusionRpg.Data", "Sqlite",
            "RpgStore.CacheFieldAccessDelve.cs");
        Assert.True(File.Exists(sourcePath), $"missing the module under test: {sourcePath}");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("System.Random", source); // certain-on-reach: no second roll
        Assert.DoesNotContain("SeededRng", source);
        Assert.DoesNotContain("rpg_item_assignment", source); // never auto-re-equips (§Design 5)
        Assert.DoesNotContain("ApplyMove", source); // closed From vocabulary untouched (§1 note)
        Assert.DoesNotContain("rpg_world_entity_cargo", source); // delve verb never touches cargo
        Assert.Contains("PackArranger.Arrange", source); // placement reused, never re-implemented
        Assert.Contains("cache.claim", source); // additive audit kind on the generic log
    }
}
