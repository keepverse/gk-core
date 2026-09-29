using System.Text.Json;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.CacheFieldAccess;

/// <summary>
/// Tasks 0.3 + 0.3b (`cache-field-access`, deployment-hierarchy module 5): the §1a reachability
/// read (a querying legion's live <c>WorldEntity.AtSectorId</c>/<c>.OnLaneId</c> must match the
/// cache's <c>place_ref</c>) and the §2a claim-into-cargo write
/// (<c>ClaimCorpseCacheIntoCargoUnlocked</c> — move into <c>rpg_world_entity_cargo</c> under
/// `legion-cargo`'s own reused capacity gates). All stores are in-memory
/// (<see cref="DataTestStore.Create"/>); no temp dir, nothing to delete.
/// </summary>
[Trait("VerificationId", "data.cache-field-access")]
public class CacheFieldAccessTests : IDisposable
{
    const string WorldId = "w-cache-access";
    const string DaveLegion = "e-dave-legion-1"; // template: standing at "homeworld"
    const string HomeSector = "homeworld";
    const string OtherSector = "ash-waste";
    const string HomeLane = "l-home-ember"; // homeworld <-> ember-hollow
    const string OtherLane = "l-home-frost"; // homeworld <-> frost-mire

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CacheFieldAccessTests()
    {
        // Same tuning the legion-cargo suite configures (weight 100/member, slots 2/member):
        // identical values keep the two suites order-independent under parallel runs.
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);
        var (ok, reason, _) = _store.CreateWorld(_store.GetCurrentPlayerId(), built);
        Assert.True(ok, reason);
    }

    public void Dispose() => _testStore.Dispose();

    // ---- fixture helpers (raw SQL against the in-memory store, same seam the legion-cargo
    // suite uses — these tables are owned by sibling modules, not by this read) ---------------

    string SeedCache(string placeKind, string placeRef, bool withItem = true, int inVoid = 0)
    {
        var cacheId = "cc_" + Guid.NewGuid().ToString("N");
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using (var ins = db.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache
                  (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision)
                VALUES ($id, $k, $r, 'legion_death', $now, NULL, $v, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRef);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            ins.Parameters.AddWithValue("$v", inVoid);
            Assert.Equal(1, ins.ExecuteNonQuery());
        }
        if (withItem)
        {
            using var item = db.CreateCommand();
            item.CommandText = """
                INSERT INTO rpg_corpse_cache_item
                  (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
                VALUES ($id, 0, 'instance', $inst, NULL, NULL, 'e-dead-legion');
                """;
            item.Parameters.AddWithValue("$id", cacheId);
            item.Parameters.AddWithValue("$inst", "inst-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(1, item.ExecuteNonQuery());
        }
        return cacheId;
    }

    void MoveLegionToSector(string entityId, string sectorId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_entities
            SET at_sector_id = $s, on_lane_id = NULL,
                on_lane_toward_sector_id = NULL, lane_progress_milli = 0
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    void MoveLegionToLane(string entityId, string laneId, string towardSectorId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_entities
            SET at_sector_id = NULL, on_lane_id = $l,
                on_lane_toward_sector_id = $t, lane_progress_milli = 500
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$l", laneId);
        cmd.Parameters.AddWithValue("$t", towardSectorId);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ---- AC 1: a legion standing at a sector lists (only) the world_sector cache pinned there --

    [Fact]
    public void Sector_standing_legion_lists_world_sector_cache()
    {
        var cacheId = SeedCache("world_sector", HomeSector);

        var listed = _store.ListClaimableCaches(WorldId, DaveLegion);

        var row = Assert.Single(listed);
        Assert.Equal(cacheId, row.CacheId);
        Assert.Equal("world_sector", row.PlaceKind);
        Assert.Equal(HomeSector, row.PlaceRef);
    }

    [Fact]
    public void Legion_elsewhere_cannot_list_sector_cache()
    {
        SeedCache("world_sector", HomeSector);
        MoveLegionToSector(DaveLegion, OtherSector);

        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));
    }

    // ---- AC 2: a legion on a lane lists the world_lane cache; other positions cannot -----------

    [Fact]
    public void Lane_legion_lists_world_lane_cache()
    {
        var cacheId = SeedCache("world_lane", HomeLane);
        MoveLegionToLane(DaveLegion, HomeLane, "ember-hollow");

        var listed = _store.ListClaimableCaches(WorldId, DaveLegion);

        var row = Assert.Single(listed);
        Assert.Equal(cacheId, row.CacheId);
        Assert.Equal("world_lane", row.PlaceKind);
        Assert.Equal(HomeLane, row.PlaceRef);
    }

    [Fact]
    public void Different_lane_legion_cannot_list_lane_cache()
    {
        SeedCache("world_lane", HomeLane);
        MoveLegionToLane(DaveLegion, OtherLane, "frost-mire");

        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));
    }

    [Fact]
    public void At_sector_legion_cannot_list_lane_cache()
    {
        SeedCache("world_lane", HomeLane);

        // DaveLegion stands at "homeworld" — on no lane at all, even one touching its sector.
        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));
    }

    [Fact]
    public void On_lane_legion_cannot_list_sector_cache()
    {
        SeedCache("world_sector", HomeSector);
        MoveLegionToLane(DaveLegion, HomeLane, "ember-hollow");

        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));
    }

    // ---- AC 3: the reachability read is correct standalone -------------------------------------

    [Fact]
    public void Unknown_legion_sees_nothing()
    {
        SeedCache("world_sector", HomeSector);

        Assert.Empty(_store.ListClaimableCaches(WorldId, "e-no-such-legion"));
    }

    [Fact]
    public void Empty_cache_is_not_listed()
    {
        // Header row with every item already claimed (spec §1's EXISTS clause): reachable place,
        // nothing to show — no error, just nothing listed.
        SeedCache("world_sector", HomeSector, withItem: false);

        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));
    }

    [Fact]
    public void Corrupt_both_set_position_sees_nothing()
    {
        // Guards WorldState.cs:292-295's "never both" invariant: a row claiming to be at a sector
        // AND on a lane has no valid live position, so the read refuses to guess.
        SeedCache("world_sector", HomeSector);
        SeedCache("world_lane", HomeLane);
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_world_entities
                SET at_sector_id = $s, on_lane_id = $l
                WHERE world_id = $w AND entity_id = $e;
                """;
            cmd.Parameters.AddWithValue("$s", HomeSector);
            cmd.Parameters.AddWithValue("$l", HomeLane);
            cmd.Parameters.AddWithValue("$w", WorldId);
            cmd.Parameters.AddWithValue("$e", DaveLegion);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));
    }

    // ---- Task 0.3b (§2a claim into legion cargo) fixture helpers -----------------------------

    /// <summary>
    /// Seeds one cache header plus an explicit multi-row body (seqs assigned in order), so claim
    /// tests control the exact row set — including <c>stack</c> rows, which prove never-partial
    /// inserts by their surviving <c>qty</c>.
    /// </summary>
    string SeedCacheWithRows(
        string placeKind, string placeRef,
        (string Kind, string? Instance, string? Container, long? Qty)[] rows, int inVoid = 0)
    {
        var cacheId = "cc_" + Guid.NewGuid().ToString("N");
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using (var ins = db.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache
                  (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision)
                VALUES ($id, $k, $r, 'legion_death', $now, NULL, $v, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRef);
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
                VALUES ($id, $seq, $kind, $inst, $cont, $qty, 'e-dead-legion');
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

    string NewInstanceTag() => "inst-" + Guid.NewGuid().ToString("N");

    int CacheItemCount(string cacheId) => _store.ListCorpseCacheItems(cacheId).Count;

    int CargoCount() => _store.ListCargo(WorldId, DaveLegion).Count;

    int ClaimLogRowCount()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_corpse_cache_claim_log;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    void AddMemberRow(string entityId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        // Same seam the legion-cargo suite uses: capacity must track a joining member live.
        cmd.CommandText = """
            INSERT INTO rpg_world_entity_members
              (world_id, entity_id, member_index, instance_id, species_id, level, hp, wounds, role)
            SELECT $w, $e, COALESCE(MAX(member_index), -1) + 1, NULL, 'peashooterzombie', 1, 110, 0, 'Fighter'
            FROM rpg_world_entity_members WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ---- AC 0.3b-1: headroom for every row → all claimed, cache empties, cargo grows ----------

    [Fact]
    public void Full_claim_moves_every_row_into_cargo_and_empties_cache()
    {
        // DaveLegion ships 3 members → 300 weight / 6 slots: three weight-10 rows fit easily.
        var inst0 = NewInstanceTag();
        var inst1 = NewInstanceTag();
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)inst0, (string?)null, (long?)null),
            ("stack", (string?)null, (string?)"cont-rations", (long?)7),
            ("instance", (string?)inst1, (string?)null, (long?)null),
        });

        var result = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-full-1",
            _ => 10);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { 0, 1, 2 }, result.ClaimedSeqs);
        Assert.Empty(result.SkippedSeqs);

        // Move-never-copy, proven by count: cache empties, cargo gains an equal number of rows.
        Assert.Equal(0, CacheItemCount(cacheId));
        var cargo = _store.ListCargo(WorldId, DaveLegion);
        Assert.Equal(3, cargo.Count);
        Assert.Equal(inst0, cargo[0].InstanceId);
        Assert.Equal("stack", cargo[1].Kind);
        Assert.Equal("cont-rations", cargo[1].ContainerId);
        Assert.Equal(7, cargo[1].Qty);
        Assert.Equal(inst1, cargo[2].InstanceId);
        // weight_each is the caller-supplied snapshot, captured once per row.
        Assert.All(cargo, row => Assert.Equal(10, row.WeightEach));
        // The emptied header is no longer listed (spec §1's EXISTS clause).
        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));
    }

    [Fact]
    public void Lane_cache_claims_into_cargo()
    {
        MoveLegionToLane(DaveLegion, HomeLane, "ember-hollow");
        var inst = NewInstanceTag();
        var cacheId = SeedCacheWithRows("world_lane", HomeLane, new[]
        {
            ("instance", (string?)inst, (string?)null, (long?)null),
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });

        var result = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-lane-1",
            _ => 10);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { 0, 1 }, result.ClaimedSeqs);
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(2, CargoCount());
    }

    // ---- AC 0.3b-2: room for some → exactly those in seq order, rest stay ---------------------

    [Fact]
    public void Overweight_row_is_skipped_while_later_rows_claim_in_seq_order()
    {
        // One over-capacity stack first (whole qty must survive — never a partial-row insert),
        // then two small rows that must still claim: never a whole-claim refusal.
        var cap = _store.LegionWeightCapacity(WorldId, DaveLegion);
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("stack", (string?)null, (string?)"cont-heavy", (long?)5),
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });

        var result = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-partial-1",
            row => row.ContainerId == "cont-heavy" ? cap + 1 : 10);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { 1, 2 }, result.ClaimedSeqs);
        Assert.Equal(new[] { 0 }, result.SkippedSeqs);

        var remaining = _store.ListCorpseCacheItems(cacheId);
        var leftover = Assert.Single(remaining);
        Assert.Equal(0, leftover.Seq);
        Assert.Equal("cont-heavy", leftover.ContainerId);
        Assert.Equal(5, leftover.Qty); // the whole stack stays — never a partial qty moved
        Assert.Equal(2, CargoCount());
    }

    [Fact]
    public void Slot_gate_skips_rows_beyond_capacity_in_seq_order()
    {
        // 3 members × 2 slots = 6 slots, plenty of weight headroom: 8 weight-1 rows claim the
        // first 6 in seq order and leave 2 behind.
        Assert.Equal(6, _store.LegionSlotCapacity(WorldId, DaveLegion));
        var rows = Enumerable.Range(0, 8)
            .Select(_ => ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null))
            .ToArray();
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, rows);

        var result = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-slots-1",
            _ => 1);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, result.ClaimedSeqs);
        Assert.Equal(new[] { 6, 7 }, result.SkippedSeqs);
        Assert.Equal(2, CacheItemCount(cacheId));
        Assert.Equal(6, CargoCount());
    }

    // ---- AC 0.3b-3: reachability re-runs at claim time ----------------------------------------

    [Fact]
    public void Stale_position_refuses_unreachable_and_leaves_state_intact()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });
        Assert.Single(_store.ListClaimableCaches(WorldId, DaveLegion));

        // Legion marches away: the previously listed cacheId is now stale.
        MoveLegionToSector(DaveLegion, OtherSector);

        var refused = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-stale-1",
            _ => 10);

        Assert.False(refused.Ok);
        Assert.Equal("cache.unreachable", refused.Reason);
        Assert.Equal(1, CacheItemCount(cacheId)); // untouched
        Assert.Equal(0, CargoCount()); // nothing written

        // Marching back restores the claim — the refusal was positional, not destructive.
        MoveLegionToSector(DaveLegion, HomeSector);
        var claimed = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-stale-2",
            _ => 10);
        Assert.True(claimed.Ok, claimed.Reason);
        Assert.Equal(new[] { 0 }, claimed.ClaimedSeqs);
    }

    [Fact]
    public void Forged_or_voided_cache_refuses_unreachable()
    {
        var forged = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), "cc_does_not_exist", "corr-forge-1",
            _ => 10);
        Assert.False(forged.Ok);
        Assert.Equal("cache.unreachable", forged.Reason);

        var voided = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        }, inVoid: 1);
        var refused = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), voided, "corr-void-1",
            _ => 10);
        Assert.False(refused.Ok);
        Assert.Equal("cache.unreachable", refused.Reason);
        Assert.Equal(1, CacheItemCount(voided));
    }

    [Fact]
    public void Missing_correlation_refuses_before_any_work()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });

        var refused = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "   ",
            _ => 10);

        Assert.False(refused.Ok);
        Assert.Equal("correlation.missing", refused.Reason);
        Assert.Equal(1, CacheItemCount(cacheId));
        Assert.Equal(0, CargoCount());
    }

    // ---- AC 0.3b-4: replay safety -------------------------------------------------------------

    [Fact]
    public void Replayed_claim_returns_identical_result_and_writes_nothing()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
            ("stack", (string?)null, (string?)"cont-rations", (long?)3),
        });

        var first = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-replay-1",
            _ => 10);
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(2, CargoCount());
        Assert.Equal(1, ClaimLogRowCount());

        var second = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-replay-1",
            _ => throw new InvalidOperationException("replay must never re-resolve weights"));

        // Byte-identical result (compared as serialized JSON — record list props are not
        // value-equal by reference), and the second call wrote nothing.
        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(2, CargoCount());
        Assert.Equal(1, ClaimLogRowCount());
    }

    [Fact]
    public void Empty_cache_claim_logs_noop_ok_and_replays_identically()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, Array.Empty<(string, string?, string?, long?)>());

        var first = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-empty-1",
            _ => 10);

        Assert.True(first.Ok, first.Reason);
        Assert.Empty(first.ClaimedSeqs);
        var second = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "corr-empty-1",
            _ => 10);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(1, ClaimLogRowCount());
    }

    // ---- AC 0.3b-5: legion-cargo capacity reused, never redefined ------------------------------

    [Fact]
    public void Capacity_tracks_live_membership_through_legion_cargo_gates()
    {
        // Fill all 6 slots via a first claim, so the next claim has no room under the CURRENT
        // capacity — then grow the legion and prove the same cache becomes claimable with no
        // code change: capacity is legion-cargo's live memberCount × tuning, not a copy here.
        var full = SeedCacheWithRows("world_sector", HomeSector, Enumerable.Range(0, 6)
            .Select(_ => ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null))
            .ToArray());
        var first = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), full, "corr-cap-1",
            _ => 1);
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(6, CargoCount());

        var tight = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)NewInstanceTag(), (string?)null, (long?)null),
        });
        var skipped = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), tight, "corr-cap-2",
            _ => 1);
        Assert.True(skipped.Ok, skipped.Reason);
        Assert.Empty(skipped.ClaimedSeqs);
        Assert.Equal(new[] { 0 }, skipped.SkippedSeqs);

        AddMemberRow(DaveLegion); // 3 → 4 members: slots 6 → 8, weight 300 → 400.
        Assert.Equal(8, _store.LegionSlotCapacity(WorldId, DaveLegion));

        var claimed = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), tight, "corr-cap-3",
            _ => 1);
        Assert.True(claimed.Ok, claimed.Reason);
        Assert.Equal(new[] { 0 }, claimed.ClaimedSeqs);
        Assert.Equal(7, CargoCount());
    }
}
