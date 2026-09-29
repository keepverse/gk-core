using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Data;
using FusionRpg.Data.Policies;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("Category", "DiskSemantics")]
public class ColdArchiveCompactionTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _dir;

    public ColdArchiveCompactionTests()
    {
        // File-bound: this class's subject IS the on-disk archive (`archive/*.sqlite`), so it keeps a
        // real dir -- through the leak-proof helper, whose dispose clears SQLite's pool before the
        // delete and lets a failed delete fail the test (testing-standard R2/R3).
        _testStore = DataTestStore.CreateFileBacked();
        _store = _testStore.Store;
        _dir = _testStore.DataDir!;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Promote_open_run_refused()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = matchKey,
                Payload = new { levelName = "open" }
            }
        });
        var run = _store.ListRuns().Single(r => r.MatchKey == matchKey);
        Assert.Null(_store.PromoteClosedRunCapture(run.Id));
        Assert.True(CountEventsForRun(run.Id) > 0);
    }

    [Fact]
    public void Promote_closed_run_archives_and_clears_hot_capture()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var notify = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                Payload = new { levelName = "closed" }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "plant.spawn", MatchKey = matchKey,
                Payload = new { ptr = "0x1", type = 1, typeName = "Pea", side = "plant", stats = new { hp = 100 } }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "mower.place", MatchKey = matchKey,
                Payload = new { ptr = "0xm1", type = 0, typeName = "Mower", row = 1 }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.end", MatchKey = matchKey,
                Payload = new { levelName = "closed", summary = new { duration = 1.0 } }
            }
        });
        Assert.NotEmpty(notify.ClosedRunIds);
        var runId = notify.ClosedRunIds[0];
        Assert.True(CountEventsForRun(runId) >= 2);
        Assert.True(CountTableForRun("entities", runId) >= 1);
        Assert.True(CountTableForRun("mowers", runId) >= 1);

        var uri = _store.PromoteClosedRunCapture(runId);
        Assert.False(string.IsNullOrWhiteSpace(uri));
        var abs = Path.Combine(_dir, uri!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs));
        Assert.Equal(0, CountEventsForRun(runId));
        Assert.Equal(0, CountTableForRun("spawn_stats", runId));
        Assert.Equal(0, CountTableForRun("entities", runId));
        Assert.Equal(0, CountTableForRun("mowers", runId));
        Assert.Contains(_store.ListRuns(), r => r.Id == runId);

        using (var cold = SqliteConnectionFactory.Open(abs, readOnly: true))
        {
            using (var cmd = cold.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM events;";
                Assert.True(Convert.ToInt64(cmd.ExecuteScalar()) >= 2);
            }
            using (var cmd = cold.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM entities;";
                Assert.True(Convert.ToInt64(cmd.ExecuteScalar()) >= 1);
            }
            using (var cmd = cold.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM mowers;";
                Assert.True(Convert.ToInt64(cmd.ExecuteScalar()) >= 1);
            }
        }

        Assert.Equal(uri, _store.PromoteClosedRunCapture(runId)); // idempotent
        Assert.Contains(_store.ListArchiveCatalog(), e => e.Uri == uri && e.Kind == "capture");
    }

    [Fact]
    public void Promote_archive_write_fail_leaves_hot_capture_intact()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var notify = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                Payload = new { levelName = "write-fail" }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "plant.spawn", MatchKey = matchKey,
                Payload = new { ptr = "0x1", type = 1, typeName = "Pea", side = "plant", stats = new { hp = 100 } }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.end", MatchKey = matchKey,
                Payload = new { levelName = "write-fail", summary = new { duration = 1.0 } }
            }
        });
        var runId = notify.ClosedRunIds[0];
        var before = CountEventsForRun(runId);
        Assert.True(before >= 2);

        if (Directory.Exists(_store.ArchiveDir))
            Directory.Delete(_store.ArchiveDir, recursive: true);
        // Block CreateDirectory(ArchiveDir) / archive sqlite create — write never succeeds.
        File.WriteAllText(_store.ArchiveDir, "not-a-directory");

        Assert.ThrowsAny<Exception>(() => _store.PromoteClosedRunCapture(runId));
        Assert.Equal(before, CountEventsForRun(runId));
        Assert.Null(_store.ListRuns().Single(r => r.Id == runId).ArchiveUri);
    }

    [Fact]
    public void Xp_overflow_refused_without_watermark_cover()
    {
        var player = _store.CreatePlayer("XpRefuse");
        _store.SetCurrentPlayer(player.Id);
        var retain = SealedCompactionPolicy.XpRetainTailPerActor;
        BulkInsertXpLedger(player.Id, "plant", 9, retain + 2);
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_actor_progression
                SET through_ledger_id=0
                WHERE save_id=$p AND empire_id=$e AND kind='plant' AND type_id=9;
                """;
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(player.Id).Value);
            Assert.True(cmd.ExecuteNonQuery() > 0);
        }

        var before = CountXpLedger(player.Id, "plant", 9);
        _store.CompactAfterRunClosed(1);
        Assert.Equal(before, CountXpLedger(player.Id, "plant", 9));
        Assert.False(Directory.EnumerateFiles(_store.ArchiveDir, "xp-a*.sqlite").Any());
    }

    [Fact]
    public void ClosedRunIds_only_on_board_end()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var start = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                Payload = new { levelName = "notify" }
            }
        });
        Assert.Empty(start.ClosedRunIds);

        var resultOnly = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "match.result", MatchKey = matchKey,
                Payload = new { result = "victory" }
            }
        });
        Assert.Empty(resultOnly.ClosedRunIds);

        var end = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.end", MatchKey = matchKey,
                Payload = new { levelName = "notify", summary = new { } }
            }
        });
        Assert.NotEmpty(end.ClosedRunIds);
    }

    [Fact]
    public void Promote_missing_archive_file_rearchives()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var notify = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                Payload = new { levelName = "heal" }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "plant.spawn", MatchKey = matchKey,
                Payload = new { ptr = "0xheal", type = 2, typeName = "Sun", side = "plant", stats = new { hp = 50 } }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.end", MatchKey = matchKey,
                Payload = new { levelName = "heal", summary = new { } }
            }
        });
        var runId = notify.ClosedRunIds[0];
        var ghostUri = $"archive/capture-run-{runId}.sqlite";
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE runs SET archive_uri=$u WHERE id=$id;";
            cmd.Parameters.AddWithValue("$u", ghostUri);
            cmd.Parameters.AddWithValue("$id", runId);
            cmd.ExecuteNonQuery();
        }
        Assert.True(CountEventsForRun(runId) > 0);
        Assert.False(File.Exists(Path.Combine(_dir, ghostUri.Replace('/', Path.DirectorySeparatorChar))));

        var uri = _store.PromoteClosedRunCapture(runId);
        Assert.False(string.IsNullOrWhiteSpace(uri));
        var abs = Path.Combine(_dir, uri!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs));
        Assert.Equal(0, CountEventsForRun(runId));
        Assert.Equal(0, CountTableForRun("entities", runId));
    }

    [Fact]
    public void Reset_clears_archive_dir_and_catalog()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var notify = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                Payload = new { levelName = "reset" }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.end", MatchKey = matchKey,
                Payload = new { levelName = "reset", summary = new { } }
            }
        });
        Assert.NotNull(_store.PromoteClosedRunCapture(notify.ClosedRunIds[0]));
        Assert.True(Directory.EnumerateFiles(_store.ArchiveDir).Any());
        Assert.NotEmpty(_store.ListArchiveCatalog());

        _store.Reset();
        Assert.Empty(_store.ListArchiveCatalog());
        Assert.Empty(Directory.EnumerateFiles(_store.ArchiveDir, "*.sqlite"));
    }

    [Fact]
    public void Activity_overflow_trims_when_watermark_covers()
    {
        var player = _store.CreatePlayer("ActTrim");
        _store.SetCurrentPlayer(player.Id);
        var retain = SealedCompactionPolicy.ActivityRetainTail;
        var total = retain + 3;
        BulkInsertActivityFacts(player.Id, total);
        StampActivityThrough(player.Id);

        var beforeRollup = _store.GetPvzActivityRollup(player.Id);
        Assert.NotNull(beforeRollup);

        _store.CompactAfterRunClosed(1);
        Assert.Equal(retain, CountActivityFacts(player.Id));
        Assert.True(Directory.EnumerateFiles(_store.ArchiveDir, "activity-p*.sqlite").Any());

        var after = _store.GetPvzActivityRollup(player.Id);
        Assert.NotNull(after);
        Assert.Equal(beforeRollup!.Revision, after!.Revision);
        Assert.Equal(beforeRollup.MatchesStarted, after.MatchesStarted);
    }

    [Fact]
    public void TrimHotTailsNow_trims_activity_overflow_when_watermark_covers()
    {
        var player = _store.CreatePlayer("ActTrimNow");
        _store.SetCurrentPlayer(player.Id);
        var retain = SealedCompactionPolicy.ActivityRetainTail;
        BulkInsertActivityFacts(player.Id, retain + 3);
        StampActivityThrough(player.Id);

        _store.TrimHotTailsNow();
        Assert.Equal(retain, CountActivityFacts(player.Id));
        Assert.False(_store.GetStorageSummary().ActivityOverTail);
    }

    [Fact]
    public void Activity_overflow_refused_without_watermark_cover()
    {
        var player = _store.CreatePlayer("ActRefuse");
        _store.SetCurrentPlayer(player.Id);
        var retain = SealedCompactionPolicy.ActivityRetainTail;
        BulkInsertActivityFacts(player.Id, retain + 2);
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO pvz_activity_rollups(player_id, revision, counters_json, updated_utc, through_fact_id, schema_version)
                VALUES($p, 1, '{}', $t, 0, $sv)
                ON CONFLICT(player_id) DO UPDATE SET through_fact_id=0;
                """;
            cmd.Parameters.AddWithValue("$p", player.Id);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$sv", SealedCompactionPolicy.ActivitySnapshotSchemaVersion);
            cmd.ExecuteNonQuery();
        }

        var before = CountActivityFacts(player.Id);
        _store.CompactAfterRunClosed(1);
        Assert.Equal(before, CountActivityFacts(player.Id));
        Assert.False(Directory.EnumerateFiles(_store.ArchiveDir, "activity-p*.sqlite").Any());
    }

    [Fact]
    public void Xp_overflow_trims_when_watermark_covers()
    {
        var player = _store.CreatePlayer("XpTrim");
        _store.SetCurrentPlayer(player.Id);
        var retain = SealedCompactionPolicy.XpRetainTailPerActor;
        BulkInsertXpLedger(player.Id, "plant", 7, retain + 2);
        StampXpThrough(player.Id, "plant", 7);

        var beforeStats = _store.GetRpgProgressionStats(player.Id);
        Assert.NotNull(beforeStats);
        Assert.NotEmpty(beforeStats!.XpByReason);

        _store.CompactAfterRunClosed(1);
        Assert.Equal(retain, CountXpLedger(player.Id, "plant", 7));
        Assert.True(Directory.EnumerateFiles(_store.ArchiveDir, "xp-a*.sqlite").Any());

        var after = _store.GetRpgProgressionStats(player.Id);
        Assert.NotNull(after);
        Assert.NotEmpty(after!.XpByReason);
    }

    [Fact]
    public void KeepLastN_promotes_oldest_closed_leaves_open()
    {
        var limit = SealedCompactionPolicy.KeepLastNFullCaptureRuns;
        for (var i = 0; i < limit + 2; i++)
            CloseRun($"keep-{i}");

        var openKey = "keep-open";
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = openKey,
                Payload = new { levelName = "open" }
            }
        });

        _store.CompactAfterRunClosed(1);

        long stillHotClosed;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM runs
                WHERE ended_utc IS NOT NULL AND (archive_uri IS NULL OR archive_uri = '');
                """;
            stillHotClosed = Convert.ToInt64(cmd.ExecuteScalar());
        }
        Assert.Equal(limit, stillHotClosed);

        var open = _store.ListRuns().Single(r => r.MatchKey == openKey);
        Assert.True(string.IsNullOrEmpty(GetArchiveUri(open.Id)));
        Assert.True(CountEventsForRun(open.Id) > 0);
    }

    void CloseRun(string matchKey)
    {
        var t = DateTime.UtcNow.ToString("o");
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                Payload = new { levelName = matchKey }
            },
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.end", MatchKey = matchKey,
                Payload = new { levelName = matchKey, summary = new { } }
            }
        });
    }

    void BulkInsertActivityFacts(long playerId, int count)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO pvz_activity_facts(player_id, run_id, t, kind, plugin_id, source_kind, source_id, payload_json, dedupe_key)
                VALUES($p, 0, $t, $k, 'test', 'test', $s, '{}', $d);
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$k", PvzActivityKinds.MatchStarted);
            cmd.Parameters.AddWithValue("$s", $"s{i}");
            cmd.Parameters.AddWithValue("$d", $"d{i}");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    void StampActivityThrough(long playerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        long maxId;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(id),0) FROM pvz_activity_facts WHERE player_id=$p;";
            cmd.Parameters.AddWithValue("$p", playerId);
            maxId = Convert.ToInt64(cmd.ExecuteScalar());
        }
        var counters = new PvzActivityRollupCounters { MatchesStarted = maxId };
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO pvz_activity_rollups(player_id, revision, counters_json, updated_utc, through_fact_id, schema_version)
                VALUES($p, 1, $c, $t, $f, $sv)
                ON CONFLICT(player_id) DO UPDATE SET
                  counters_json=excluded.counters_json,
                  through_fact_id=excluded.through_fact_id,
                  schema_version=excluded.schema_version,
                  updated_utc=excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$c", System.Text.Json.JsonSerializer.Serialize(counters));
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$f", maxId);
            cmd.Parameters.AddWithValue("$sv", SealedCompactionPolicy.ActivitySnapshotSchemaVersion);
            cmd.ExecuteNonQuery();
        }
    }

    void BulkInsertXpLedger(long playerId, string kind, int typeId, int count)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO rpg_xp_ledger(
                  save_id, empire_id, kind, type_id, run_id, t, delta, reason, activity_fact_id,
                  level_before, xp_before, level_after, xp_after, demotion_before, demotion_after, dedupe_key)
                VALUES($p, $e, $k, $ty, 0, $t, 1, 'test', NULL, 1, $xp, 1, $xp2, 0, 0, $d);
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$ty", typeId);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$xp", (double)i);
            cmd.Parameters.AddWithValue("$xp2", (double)(i + 1));
            cmd.Parameters.AddWithValue("$d", $"xp{i}");
            cmd.ExecuteNonQuery();
        }
        using (var prog = db.CreateCommand())
        {
            prog.Transaction = tx;
            prog.CommandText = """
                INSERT INTO rpg_actor_progression(
                  save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc,
                  through_ledger_id, xp_by_reason_json)
                VALUES($p, $e, $k, $ty, 1, $xp, 1, 0, 1, $t, 0, $b)
                ON CONFLICT(save_id, empire_id, kind, type_id) DO UPDATE SET xp=excluded.xp;
                """;
            prog.Parameters.AddWithValue("$p", playerId);
            prog.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
            prog.Parameters.AddWithValue("$k", kind);
            prog.Parameters.AddWithValue("$ty", typeId);
            prog.Parameters.AddWithValue("$xp", (double)count);
            prog.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            prog.Parameters.AddWithValue("$b", "{\"test\":{\"Sum\":1,\"Count\":1}}");
            prog.ExecuteNonQuery();
        }
        tx.Commit();
    }

    void StampXpThrough(long playerId, string kind, int typeId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        long maxId;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(MAX(id),0) FROM rpg_xp_ledger
                WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$t", typeId);
            maxId = Convert.ToInt64(cmd.ExecuteScalar());
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_actor_progression
                SET through_ledger_id=$f,
                    xp_by_reason_json=$b
                WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
                """;
            cmd.Parameters.AddWithValue("$f", maxId);
            cmd.Parameters.AddWithValue("$b", "{\"test\":{\"Sum\":1,\"Count\":1}}");
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$t", typeId);
            cmd.ExecuteNonQuery();
        }
    }

    long CountEventsForRun(long runId) => CountTableForRun("events", runId);

    long CountTableForRun(string table, long runId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE run_id=$r;";
        cmd.Parameters.AddWithValue("$r", runId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    long CountActivityFacts(long playerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pvz_activity_facts WHERE player_id=$p;";
        cmd.Parameters.AddWithValue("$p", playerId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    long CountXpLedger(long playerId, string kind, int typeId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM rpg_xp_ledger
            WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", typeId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    string? GetArchiveUri(long runId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT archive_uri FROM runs WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", runId);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v);
    }
}
