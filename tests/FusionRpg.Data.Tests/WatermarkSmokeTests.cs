using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

public class WatermarkSmokeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WatermarkSmokeTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Activity_append_stamps_monotonic_through_fact_id()
    {
        var player = _store.CreatePlayer("WmAct");
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchStarted,
            DedupeKey = "wm-m1"
        });
        var through1 = ReadThroughFactId(player.Id);
        Assert.True(through1 > 0);

        var r2 = _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieKilled,
            PayloadJson = """{"type":1}""",
            DedupeKey = "wm-zk1"
        });
        var through2 = ReadThroughFactId(player.Id);
        Assert.True(through2 > through1);
        Assert.Equal(1, r2.Rollup.MatchesStarted);
        Assert.Equal(1, r2.Rollup.ZombiesKilled);
        Assert.Equal(SealedSchemaVersion(), ReadSchemaVersion(player.Id));
    }

    [Fact]
    public void Activity_repair_rebuilds_when_rollup_missing()
    {
        var player = _store.CreatePlayer("WmRepair");
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchStarted,
            DedupeKey = "rep-m1"
        });
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieKilled,
            DedupeKey = "rep-zk1"
        });

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM pvz_activity_rollups WHERE player_id=$p;";
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.ExecuteNonQuery();
        }

        var rollup = _store.GetPvzActivityRollup(player.Id);
        Assert.NotNull(rollup);
        Assert.Equal(1, rollup!.MatchesStarted);
        Assert.Equal(1, rollup.ZombiesKilled);
        Assert.True(ReadThroughFactId(player.Id) > 0);
        Assert.Equal(SealedSchemaVersion(), ReadSchemaVersion(player.Id));
    }

    [Fact]
    public void Xp_buckets_survive_ledger_trim()
    {
        var player = _store.CreatePlayer("WmXp");
        _store.SeedRpgProgressionDemo(player.Id);
        var before = _store.GetRpgProgressionStats(player.Id);
        Assert.NotNull(before);
        Assert.NotEmpty(before!.XpByReason);

        long maxLedger;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(through_ledger_id),0) FROM rpg_actor_progression WHERE save_id=$p AND empire_id=$e;";
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(player.Id).Value);
            maxLedger = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        Assert.True(maxLedger > 0);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM rpg_xp_ledger WHERE save_id=$p AND empire_id=$e;";
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(player.Id).Value);
            Assert.True(cmd.ExecuteNonQuery() > 0);
        }

        var after = _store.GetRpgProgressionStats(player.Id);
        Assert.NotNull(after);
        Assert.Equal(before.XpByReason.Count, after!.XpByReason.Count);
        foreach (var row in before.XpByReason)
        {
            var match = after.XpByReason.Single(x => x.Reason == row.Reason);
            Assert.Equal(row.SumDelta, match.SumDelta);
            Assert.Equal(row.Count, match.Count);
        }
    }

    [Fact]
    public void Activity_schema_version_zero_forces_rebuild()
    {
        var player = _store.CreatePlayer("WmSchema0");
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchStarted,
            DedupeKey = "s0-m1"
        });
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieKilled,
            DedupeKey = "s0-zk1"
        });

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE pvz_activity_rollups SET schema_version=0 WHERE player_id=$p;";
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.ExecuteNonQuery();
        }

        var r = _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.PlantPlaced,
            DedupeKey = "s0-pp1"
        });
        Assert.Equal(1, r.Rollup.MatchesStarted);
        Assert.Equal(1, r.Rollup.ZombiesKilled);
        Assert.Equal(1, r.Rollup.PlantsPlaced);
        Assert.Equal(SealedSchemaVersion(), ReadSchemaVersion(player.Id));
    }

    [Fact]
    public void Activity_dedupe_does_not_change_watermark_or_counters()
    {
        var player = _store.CreatePlayer("WmDedupe");
        var first = _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchStarted,
            DedupeKey = "dup-key"
        });
        var through = ReadThroughFactId(player.Id);
        var second = _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchStarted,
            DedupeKey = "dup-key"
        });
        Assert.Equal(through, ReadThroughFactId(player.Id));
        Assert.Equal(first.Rollup.MatchesStarted, second.Rollup.MatchesStarted);
        Assert.Equal(1, second.Rollup.MatchesStarted);
    }

    [Fact]
    public void Activity_corrupt_counters_json_rebuilds_on_append()
    {
        var player = _store.CreatePlayer("WmCorrupt");
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchStarted,
            DedupeKey = "c-m1"
        });
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieKilled,
            DedupeKey = "c-zk1"
        });

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE pvz_activity_rollups SET counters_json='NOT-JSON' WHERE player_id=$p;";
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.ExecuteNonQuery();
        }

        var r = _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.PlantLost,
            DedupeKey = "c-pl1"
        });
        Assert.Equal(1, r.Rollup.MatchesStarted);
        Assert.Equal(1, r.Rollup.ZombiesKilled);
        Assert.Equal(1, r.Rollup.PlantsLost);
        Assert.Equal(SealedSchemaVersion(), ReadSchemaVersion(player.Id));
    }

    [Fact]
    public void Xp_legacy_null_buckets_backfill_from_ledger()
    {
        var player = _store.CreatePlayer("WmLegacyXp");
        _store.SeedRpgProgressionDemo(player.Id);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_actor_progression
                SET xp_by_reason_json=NULL, through_ledger_id=0
                WHERE save_id=$p AND empire_id=$e;
                """;
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(player.Id).Value);
            Assert.True(cmd.ExecuteNonQuery() > 0);
        }

        var stats = _store.GetRpgProgressionStats(player.Id);
        Assert.NotNull(stats);
        Assert.NotEmpty(stats!.XpByReason);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM rpg_actor_progression
                WHERE save_id=$p AND empire_id=$e AND xp_by_reason_json IS NOT NULL AND xp_by_reason_json != '' AND xp_by_reason_json != '{}';
                """;
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(player.Id).Value);
            Assert.True(Convert.ToInt64(cmd.ExecuteScalar()) > 0);
        }
    }

    long ReadThroughFactId(long playerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT through_fact_id FROM pvz_activity_rollups WHERE player_id=$p;";
        cmd.Parameters.AddWithValue("$p", playerId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    int ReadSchemaVersion(long playerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT schema_version FROM pvz_activity_rollups WHERE player_id=$p;";
        cmd.Parameters.AddWithValue("$p", playerId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    static int SealedSchemaVersion() => FusionRpg.Data.Policies.SealedCompactionPolicy.ActivitySnapshotSchemaVersion;
}
