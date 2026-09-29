using System.Text.Json;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.CacheRetrieval;

/// <summary>
/// Task 4C.4 part 2 (`cache-retrieval-mission`, deployment-hierarchy module 6): voided-cache
/// retrieval missions — filing against <c>in_void = 1</c> caches (lawn/delve/wipe kinds, never a
/// place/source branch), resolution on world-turn commit, reward as a live read of surviving rows
/// into the filing legion's cargo via `legion-cargo`'s own reused gates, plus expiry/failure paths.
/// Part 1 (delve pack verb, <c>RpgStore.CacheFieldAccessDelve.cs</c>, <c>in_void = 0</c>-only) is
/// consumed as a boundary, never overlapped: every test below that touches the partition proves it.
/// All stores are in-memory (<see cref="DataTestStore.Create"/>); no temp dir, nothing to delete.
/// <para>Shares the <c>StructureCatalogSwap</c> xUnit collection with the cargo suites for a
/// reason this suite cannot opt out of: <c>RpgStore.TestCargoWeightProbe</c> is one static
/// field, this suite sets it non-null, and <c>CargoCommandResolveTests</c>/
/// <c>ClaimPricingTests</c>/<c>BudgetDebitTests</c> set it null. Outside the collection they
/// run in parallel and overwrite each other mid-test, which fails in both directions: a
/// retrieval reads <c>cargo.weight-unknown</c> it should not, and a cargo command is accepted
/// where it should refuse. Equal tuning values are not enough here, unlike
/// <see cref="ScopedInventoryPolicy"/> below, because the two sides want different answers.</para>
/// </summary>
[Collection("StructureCatalogSwap")]
[Trait("VerificationId", "data.cache-field-access")]
public class CacheRetrievalTests : IDisposable
{
    const string WorldId = "w-cache-retrieval";
    const string Commander = "dave";
    const string DaveLegion = "e-dave-legion-1"; // template: standing at "homeworld"
    const long WeightEach = 10;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CacheRetrievalTests()
    {
        // Same tuning every cargo suite configures (100 mass / 2 slots per member): identical
        // values keep the suites order-independent under parallel runs.
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));
        RpgStore.TestCargoWeightProbe = _ => WeightEach;

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);
        var (ok, reason, _) = _store.CreateWorld(_store.GetCurrentPlayerId(), built);
        Assert.True(ok, reason);
    }

    public void Dispose()
    {
        // Restore the shared probe: `Unknown_weight_fails_with_zero_writes` sets it to a null-answer
        // supplier, and leaving that behind would make the NEXT suite's rows unresolvable.
        RpgStore.TestCargoWeightProbe = null;
        _testStore.Dispose();
    }

    // ---- fixture helpers (raw SQL against the in-memory store — the cache tables are owned by
    // sibling modules, not by this mission; the mission's own table is never seeded by hand) ----

    string SeedVoidedCache(
        string placeKind, string placeRef, string sourceKind, int rows,
        int inVoid = 1, bool withItems = true)
    {
        var cacheId = "cc_" + Guid.NewGuid().ToString("N");
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using (var ins = db.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache
                  (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision)
                VALUES ($id, $k, $r, $s, $now, $now, $v, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRef);
            ins.Parameters.AddWithValue("$s", sourceKind);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            ins.Parameters.AddWithValue("$v", inVoid);
            Assert.Equal(1, ins.ExecuteNonQuery());
        }
        if (withItems)
        {
            for (var i = 0; i < rows; i++)
            {
                using var item = db.CreateCommand();
                item.CommandText = """
                    INSERT INTO rpg_corpse_cache_item
                      (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
                    VALUES ($id, $seq, 'instance', $inst, NULL, NULL, 'dead-specimen');
                    """;
                item.Parameters.AddWithValue("$id", cacheId);
                item.Parameters.AddWithValue("$seq", i);
                item.Parameters.AddWithValue("$inst", "inst-" + Guid.NewGuid().ToString("N"));
                Assert.Equal(1, item.ExecuteNonQuery());
            }
        }
        return cacheId;
    }

    // The `origin_theta` column is ensured by filing (the mission's own additive migration), so a
    // test stamps it after filing, never before — mirroring how a future producer write would land.
    void SetOriginTheta(string cacheId, int theta)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE rpg_corpse_cache SET origin_theta = $t WHERE cache_id = $id;";
        cmd.Parameters.AddWithValue("$t", theta);
        cmd.Parameters.AddWithValue("$id", cacheId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    int CacheItemCount(string cacheId) => _store.ListCorpseCacheItems(cacheId).Count;

    int CargoCount() => _store.ListCargo(WorldId, DaveLegion).Count;

    ulong WorldSeed() => _store.LoadWorldState(WorldId)!.Seed;

    WorldTurnCommitResult CommitAll()
    {
        // The human goes last (WorldTurnCommitTests pattern): AI factions fill first so the turn
        // actually advances on Dave's commit instead of parking at "waiting".
        _store.CommitWorldTurn(WorldId, "wild", 0);
        _store.CommitWorldTurn(WorldId, "zomboss", 0);
        return _store.CommitWorldTurn(WorldId, Commander, 0);
    }

    // Mission ids are caller correlation: scanning a few ids for a deterministic pass/fail pins no
    // literal, so the suite survives any template-seed change that keeps the world buildable.
    string ScanMissionId(string prefix, int dueTurn, long milli, bool wantPass)
    {
        var seed = WorldSeed();
        for (var i = 0; i < 50; i++)
        {
            var id = $"{prefix}-{i}";
            if (RpgStore.RetrievalContestPass(seed, id, dueTurn, milli) == wantPass)
                return id;
        }
        Assert.Fail($"no {(wantPass ? "passing" : "failing")} mission id found in 50 tries");
        throw new InvalidOperationException("unreachable: Assert.Fail always throws");
    }

    static RetrievalMissionOutcome OutcomeOf(RetrievalMissionRow mission)
    {
        Assert.NotNull(mission.ResultJson);
        return JsonSerializer.Deserialize<RetrievalMissionOutcome>(mission.ResultJson)!;
    }

    // ---- filing: the in_void gate (spec Testing strategy) ---------------------------------------

    [Fact]
    public void File_mission_against_voided_lawn_cache()
    {
        var cacheId = SeedVoidedCache("lawn", "match-1", "death", rows: 2);

        var (ok, reason, mission) = _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, "m-file-1", retrieverTheta: 20);

        Assert.True(ok, reason);
        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Filed, mission.State);
        Assert.Equal(0, mission.FiledTurn);
        Assert.Equal(1, mission.DueTurn);
        Assert.Null(mission.ResolvedTurn);
        var reread = _store.TryGetRetrievalMission("m-file-1");
        Assert.NotNull(reread);
        Assert.Equal(cacheId, reread.CacheId);
    }

    [Fact]
    public void Filing_against_live_cache_refuses_not_void()
    {
        // Both sibling-module targets refuse here: a delve-room cache (delve pack verb's domain)
        // and a world-sector cache (cargo verb's domain) are equally unreachable to retrieval.
        var delveLive = SeedVoidedCache("delve_room", "delve:9:r0:c0", "death", rows: 1, inVoid: 0);
        var sectorLive = SeedVoidedCache("world_sector", "homeworld", "legion_death", rows: 1, inVoid: 0);

        var (okDelve, reasonDelve, _) = _store.FileRetrievalMission(WorldId, DaveLegion, delveLive, "m-live-1", 20);
        var (okSector, reasonSector, _) = _store.FileRetrievalMission(WorldId, DaveLegion, sectorLive, "m-live-2", 20);

        Assert.False(okDelve);
        Assert.Equal("cache.not-void", reasonDelve);
        Assert.False(okSector);
        Assert.Equal("cache.not-void", reasonSector);
        Assert.Null(_store.TryGetRetrievalMission("m-live-1"));
        // Nothing was written anywhere: the live caches keep their rows for their own verbs.
        Assert.Equal(1, CacheItemCount(delveLive));
        Assert.Equal(1, CacheItemCount(sectorLive));
    }

    [Fact]
    public void Filing_unknown_cache_or_missing_correlation_refuses()
    {
        var (okUnknown, reasonUnknown, _) =
            _store.FileRetrievalMission(WorldId, DaveLegion, "cc_no_such_cache", "m-unknown-1", 20);
        Assert.False(okUnknown);
        Assert.Equal("cache.unknown", reasonUnknown);

        var (okCorr, reasonCorr, _) =
            _store.FileRetrievalMission(WorldId, DaveLegion, "cc_no_such_cache", "  ", 20);
        Assert.False(okCorr);
        Assert.Equal("correlation.missing", reasonCorr);
    }

    [Fact]
    public void Replay_same_mission_id_returns_recorded_row()
    {
        var cacheId = SeedVoidedCache("lawn", "match-1", "death", rows: 1);

        var (ok1, _, first) = _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, "m-replay-1", 20);
        var (ok2, reason2, second) = _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, "m-replay-1", 999);

        Assert.True(ok1);
        Assert.True(ok2);
        Assert.Equal("replay", reason2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        // The replay returns the SAME recorded mission — the second filing's retrieverTheta (999)
        // never overwrote the first (20).
        Assert.Equal(first.MissionId, second.MissionId);
        Assert.Equal(20, second.RetrieverTheta);
        Assert.Equal(RetrievalMissionStates.Filed, second.State);
    }

    // ---- pure contest: bounds + determinism (no store) ------------------------------------------

    [Fact]
    public void Contest_curve_is_bounded_and_deterministic()
    {
        // Equal Theta is the documented coin flip; extremes pin floor/cap (never 0/1000).
        Assert.Equal(500, RpgStore.RetrievalSuccessMilli(20, 20));
        Assert.Equal(50, RpgStore.RetrievalSuccessMilli(-10000, 10000));
        Assert.Equal(950, RpgStore.RetrievalSuccessMilli(10000, -10000));

        // Determinism: the same (seed, mission, due, milli) always agrees with itself.
        var a = RpgStore.RetrievalContestPass(11UL, "m-det", 1, 500);
        var b = RpgStore.RetrievalContestPass(11UL, "m-det", 1, 500);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Contest_reads_origin_for_fixed_retriever()
    {
        // The cache's own depth drives the curve: same retriever, different origins differ.
        var shallow = RpgStore.RetrievalSuccessMilli(20, 20);
        var deep = RpgStore.RetrievalSuccessMilli(20, 200);
        Assert.NotEqual(shallow, deep);
        Assert.True(deep < shallow);
    }

    // ---- resolution on commit: file -> resolve -> contents land ----------------------------------

    [Fact]
    public void Filed_mission_stays_pending_until_commit()
    {
        var cacheId = SeedVoidedCache("lawn", "match-1", "death", rows: 2);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, "m-pending-1", 20);

        var mission = _store.TryGetRetrievalMission("m-pending-1");

        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Filed, mission.State);
        Assert.Equal(2, CacheItemCount(cacheId));
        Assert.Equal(0, CargoCount());
    }

    [Fact]
    public void Commit_resolves_pass_and_contents_land_in_cargo()
    {
        var cacheId = SeedVoidedCache("lawn", "match-1", "death", rows: 3);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var missionId = ScanMissionId("m-pass", dueTurn: 1, milli, wantPass: true);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, missionId, retrieverTheta: 20);

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        var mission = _store.TryGetRetrievalMission(missionId);
        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Succeeded, mission.State);
        Assert.Equal(1, mission.ResolvedTurn);
        var outcome = OutcomeOf(mission);
        Assert.True(outcome.Ok);
        Assert.Equal(new[] { 0, 1, 2 }, outcome.ClaimedSeqs.OrderBy(s => s).ToArray());
        Assert.Empty(outcome.SkippedSeqs);
        // Move-never-copy, proven by count on both sides.
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(3, CargoCount());
        var report = _store.GetWorldTurnReport(WorldId, 0)!;
        Assert.Contains(report.Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail == $"cache.retrieved:3+0");
    }

    [Fact]
    public void Contest_failure_leaves_cache_untouched()
    {
        var cacheId = SeedVoidedCache("delve_room", "delve:7:r1:c0", "death", rows: 2);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var missionId = ScanMissionId("m-fail", dueTurn: 1, milli, wantPass: false);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, missionId, retrieverTheta: 20);

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        var mission = _store.TryGetRetrievalMission(missionId);
        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Failed, mission.State);
        var outcome = OutcomeOf(mission);
        Assert.False(outcome.Ok);
        Assert.Equal("contest.failed", outcome.Reason);
        // The priced-shape bite without the price: the mission is spent, the cache is whole.
        Assert.Equal(2, CacheItemCount(cacheId));
        Assert.Equal(0, CargoCount());
    }

    [Fact]
    public void Two_missions_racing_one_cache_partition_rows_exactly_once()
    {
        var cacheId = SeedVoidedCache("lawn", "match-9", "wipe", rows: 3);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var firstId = ScanMissionId("m-race-a", dueTurn: 1, milli, wantPass: true);
        var secondId = ScanMissionId("m-race-b", dueTurn: 1, milli, wantPass: true);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, firstId, 20);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, secondId, 20);

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        var first = OutcomeOf(_store.TryGetRetrievalMission(firstId)!);
        var second = OutcomeOf(_store.TryGetRetrievalMission(secondId)!);
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        // Exactly one winner per row by construction of the DELETE-count claim key: the union of
        // both missions' claimed seqs is the original set, each exactly once — and the loser is an
        // honest empty haul, never an error.
        var union = first.ClaimedSeqs.Concat(second.ClaimedSeqs).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, union);
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(3, CargoCount());
    }

    [Fact]
    public void Delve_and_wipe_kinds_resolve_like_lawn()
    {
        // No place/source branch: a voided delve-room death cache and a voided lawn wipe cache file
        // and resolve through the identical path.
        var delveCache = SeedVoidedCache("delve_room", "delve:3:r0:c1", "death", rows: 1);
        var wipeCache = SeedVoidedCache("lawn", "match-4", "wipe", rows: 1);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var delveId = ScanMissionId("m-kind-d", dueTurn: 1, milli, wantPass: true);
        var wipeId = ScanMissionId("m-kind-w", dueTurn: 1, milli, wantPass: true);
        _store.FileRetrievalMission(WorldId, DaveLegion, delveCache, delveId, 20);
        _store.FileRetrievalMission(WorldId, DaveLegion, wipeCache, wipeId, 20);

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        Assert.Equal(RetrievalMissionStates.Succeeded, _store.TryGetRetrievalMission(delveId)!.State);
        Assert.Equal(RetrievalMissionStates.Succeeded, _store.TryGetRetrievalMission(wipeId)!.State);
        Assert.Equal(0, CacheItemCount(delveCache));
        Assert.Equal(0, CacheItemCount(wipeCache));
        Assert.Equal(2, CargoCount());
    }

    [Fact]
    public void Origin_theta_stamped_on_cache_drives_resolution()
    {
        // A deep cache (origin 200) against a weak retriever (20) reads floor odds — the scanned
        // failing id proves the stamped origin, not the fallback, decided the draw.
        var cacheId = SeedVoidedCache("lawn", "match-deep", "death", rows: 1);
        var milli = RpgStore.RetrievalSuccessMilli(20, 200);
        Assert.Equal(50, milli);
        var missionId = ScanMissionId("m-origin", dueTurn: 1, milli, wantPass: false);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, missionId, retrieverTheta: 20);
        SetOriginTheta(cacheId, 200);

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        Assert.Equal(RetrievalMissionStates.Failed, _store.TryGetRetrievalMission(missionId)!.State);
        Assert.Equal(1, CacheItemCount(cacheId));
    }

    [Fact]
    public void Overfull_legion_skips_rows_but_still_succeeds()
    {
        // A legion with room for some rows claims those in seq order; the rest stay uncleared for a
        // later claim — never a whole-claim refusal, never a partial-row insert (§2a discipline).
        var slots = _store.LegionSlotCapacity(WorldId, DaveLegion);
        Assert.True(slots > 0);
        var cacheId = SeedVoidedCache("lawn", "match-full", "death", rows: slots + 2);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var missionId = ScanMissionId("m-full", dueTurn: 1, milli, wantPass: true);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, missionId, 20);

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        var mission = _store.TryGetRetrievalMission(missionId);
        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Succeeded, mission.State);
        var outcome = OutcomeOf(mission);
        Assert.NotEmpty(outcome.ClaimedSeqs);
        Assert.NotEmpty(outcome.SkippedSeqs);
        Assert.Equal(slots + 2, outcome.ClaimedSeqs.Count + outcome.SkippedSeqs.Count);
        Assert.Equal(outcome.ClaimedSeqs.Count, CargoCount());
        Assert.Equal(outcome.SkippedSeqs.Count, CacheItemCount(cacheId));
    }

    // ---- expiry paths: target gone, carrier gone ------------------------------------------------

    [Fact]
    public void Expired_when_target_header_deleted()
    {
        var cacheId = SeedVoidedCache("lawn", "match-gone", "death", rows: 1);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var missionId = ScanMissionId("m-exp-t", dueTurn: 1, milli, wantPass: true);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, missionId, 20);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var del = db.CreateCommand())
        {
            del.CommandText = "DELETE FROM rpg_corpse_cache WHERE cache_id = $id;";
            del.Parameters.AddWithValue("$id", cacheId);
            Assert.Equal(1, del.ExecuteNonQuery());
        }

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        var mission = _store.TryGetRetrievalMission(missionId);
        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Expired, mission.State);
        Assert.Equal("target.gone", OutcomeOf(mission).Reason);
    }

    [Fact]
    public void Expired_when_filing_legion_deleted()
    {
        var cacheId = SeedVoidedCache("lawn", "match-rout", "death", rows: 2);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var missionId = ScanMissionId("m-exp-l", dueTurn: 1, milli, wantPass: true);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, missionId, 20);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var del = db.CreateCommand())
        {
            del.CommandText = "DELETE FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;";
            del.Parameters.AddWithValue("$w", WorldId);
            del.Parameters.AddWithValue("$e", DaveLegion);
            Assert.Equal(1, del.ExecuteNonQuery());
        }

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        var mission = _store.TryGetRetrievalMission(missionId);
        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Expired, mission.State);
        Assert.Equal("entity.gone", OutcomeOf(mission).Reason);
        // Nowhere to land means nothing moved: the cache is whole.
        Assert.Equal(2, CacheItemCount(cacheId));
    }

    // ---- the partition, proven from this side ---------------------------------------------------

    [Fact]
    public void Voided_caches_are_invisible_to_field_verbs()
    {
        // Module 5's own reads never surface a voided row, so filing is the only path that sees it:
        // a voided lawn cache is absent from the sector/lane list, and the cargo verb refuses it.
        var cacheId = SeedVoidedCache("lawn", "match-void", "death", rows: 1);

        Assert.Empty(_store.ListClaimableCaches(WorldId, DaveLegion));

        var claim = _store.ClaimCorpseCacheIntoCargo(
            WorldId, DaveLegion, _store.GetCurrentPlayerId(), cacheId, "void-partition-1", _ => WeightEach);
        Assert.False(claim.Ok);
        Assert.Equal("cache.unreachable", claim.Reason);
        Assert.Equal(1, CacheItemCount(cacheId));
    }

    [Fact]
    public void Unknown_weight_fails_with_zero_writes()
    {
        // The whole-act pre-flight (`ResolveClaimUnlocked` precedent): one unresolvable row fails
        // the mission before any row moves — never a half-moved transfer.
        RpgStore.TestCargoWeightProbe = _ => null;
        var cacheId = SeedVoidedCache("lawn", "match-mass", "death", rows: 2);
        var milli = RpgStore.RetrievalSuccessMilli(20, 20);
        var missionId = ScanMissionId("m-mass", dueTurn: 1, milli, wantPass: true);
        _store.FileRetrievalMission(WorldId, DaveLegion, cacheId, missionId, 20);

        var commit = CommitAll();

        Assert.True(commit.Advanced, commit.Reason);
        var mission = _store.TryGetRetrievalMission(missionId);
        Assert.NotNull(mission);
        Assert.Equal(RetrievalMissionStates.Failed, mission.State);
        Assert.Equal("cargo.weight-unknown", OutcomeOf(mission).Reason);
        Assert.Equal(2, CacheItemCount(cacheId));
        Assert.Equal(0, CargoCount());
    }
}
