using FusionRpg.Contracts;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("Category", "DiskSemantics")]
public class StoragePurgeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _dir;

    public StoragePurgeTests()
    {
        // File-bound: this class asserts purge deletes real archive files, so it keeps a real dir --
        // through the leak-proof helper (R2/R3).
        _testStore = DataTestStore.CreateFileBacked();
        _store = _testStore.Store;
        _dir = _testStore.DataDir!;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Purge_and_delete_refuse_open_runs()
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
        Assert.True(string.IsNullOrEmpty(run.EndedUtc));

        var purge = _store.PurgeClosedRunCapture(new[] { run.Id });
        Assert.Equal(0, purge.Deleted);
        Assert.Equal(1, purge.Refused);
        Assert.True(CountEventsForRun(run.Id) > 0);

        var del = _store.DeleteClosedRuns(new[] { run.Id });
        Assert.Equal(0, del.Deleted);
        Assert.Equal(1, del.Refused);
        Assert.Contains(_store.ListRuns(), r => r.Id == run.Id);
    }

    [Fact]
    public void Purge_closed_run_clears_hot_capture_keeps_run_row()
    {
        var runId = CreateClosedRunWithHotCapture("purge-hot");
        Assert.True(CountEventsForRun(runId) > 0);
        Assert.True(_store.GetStorageSummary().ClosedRunsStillHot >= 1);

        var result = _store.PurgeClosedRunCapture(new[] { runId });
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Refused);
        Assert.Equal(0, CountEventsForRun(runId));
        Assert.Equal(0, CountTableForRun("spawn_stats", runId));
        Assert.Equal(0, CountTableForRun("entities", runId));
        Assert.Equal(0, CountTableForRun("mowers", runId));
        Assert.Contains(_store.ListRuns(), r => r.Id == runId && !string.IsNullOrEmpty(r.EndedUtc));
        Assert.Equal(0, _store.GetStorageSummary().ClosedRunsStillHot);
    }

    [Fact]
    public void Delete_archives_removes_catalog_and_file_path_escape_refused()
    {
        var runId = CreateClosedRunWithHotCapture("archive-del");
        var uri = _store.PromoteClosedRunCapture(runId);
        Assert.False(string.IsNullOrWhiteSpace(uri));
        var abs = Path.Combine(_dir, uri!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs));
        Assert.Contains(_store.ListArchiveCatalog(), e => e.Uri == uri);

        var escape = _store.DeleteArchives(new[] { "../evil.sqlite", uri + "/../../outside.db" });
        Assert.True(escape.Refused >= 1);
        Assert.True(File.Exists(abs));

        var ok = _store.DeleteArchives(new[] { uri });
        Assert.Equal(1, ok.Deleted);
        Assert.Equal(0, ok.Refused);
        Assert.False(File.Exists(abs));
        Assert.DoesNotContain(_store.ListArchiveCatalog(), e => e.Uri == uri);

        var empty = _store.DeleteArchives(Array.Empty<string>());
        Assert.Equal(0, empty.Deleted);
        Assert.Equal(0, empty.Refused);
    }

    [Fact]
    public void Delete_closed_run_removes_row()
    {
        var runId = CreateClosedRunWithHotCapture("delete-run");
        var result = _store.DeleteClosedRuns(new[] { runId });
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Refused);
        Assert.DoesNotContain(_store.ListRuns(), r => r.Id == runId);
        Assert.Equal(0, CountEventsForRun(runId));
    }

    [Fact]
    public void Delete_closed_run_with_archive_removes_catalog_and_file()
    {
        var runId = CreateClosedRunWithHotCapture("delete-archived");
        var uri = _store.PromoteClosedRunCapture(runId);
        Assert.False(string.IsNullOrWhiteSpace(uri));
        var abs = Path.Combine(_dir, uri!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs));
        var listed = _store.ListRuns().Single(r => r.Id == runId);
        Assert.Equal(uri, listed.ArchiveUri);

        var result = _store.DeleteClosedRuns(new[] { runId });
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Refused);
        Assert.DoesNotContain(_store.ListRuns(), r => r.Id == runId);
        Assert.DoesNotContain(_store.ListArchiveCatalog(), e => e.Uri == uri);
        Assert.False(File.Exists(abs));
        Assert.Equal(0, _store.GetStorageSummary().ArchiveCount);
    }

    [Fact]
    public void Summary_counts_archives_and_open_runs()
    {
        var summary0 = _store.GetStorageSummary();
        Assert.Equal(0, summary0.ArchiveCount);
        Assert.Equal(0, summary0.ClosedRunsStillHot);
        Assert.False(summary0.ActivityOverTail);
        Assert.False(summary0.XpOverTail);

        var openKey = Guid.NewGuid().ToString("N");
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = openKey,
                Payload = new { levelName = "still-open" }
            }
        });
        var summary1 = _store.GetStorageSummary();
        Assert.True(summary1.OpenRuns >= 1);

        var runId = CreateClosedRunWithHotCapture("summary-hot");
        Assert.True(_store.GetStorageSummary().ClosedRunsStillHot >= 1);
        var uri = _store.PromoteClosedRunCapture(runId);
        Assert.False(string.IsNullOrWhiteSpace(uri));
        var summary2 = _store.GetStorageSummary();
        Assert.True(summary2.ArchiveCount >= 1);

        // Per-run, not a global zero. This asserted `Assert.Equal(0, summary2.ClosedRunsStillHot)`,
        // which was only ever true because nothing else in this store was closed-and-hot. This test
        // deliberately opens a run ("still-open") first, and the MatchKey self-heal now ENDS that
        // abandoned run when the next board starts — measured: after the second board resolves,
        // openRuns=0 and closedStillHot=2, with the self-healed run keeping its board.start row.
        // Closing an abandoned run is the intended behaviour, so the global count is a reading about
        // the whole store, while what this test proves is about the run it promoted.
        Assert.Equal(0, CountEventsForRun(runId));
        Assert.Equal(0, CountTableForRun("spawn_stats", runId));
        Assert.Equal(0, CountTableForRun("entities", runId));
        Assert.Equal(0, CountTableForRun("mowers", runId));
    }

    [Fact]
    public void TrimHotTailsNow_smoke()
    {
        _ = CreateClosedRunWithHotCapture("trim-smoke");
        _store.TrimHotTailsNow();
        var summary = _store.GetStorageSummary();
        Assert.True(summary.ArchiveCount >= 0);
        Assert.True(summary.ClosedRunsStillHot >= 0);
        Assert.False(summary.ActivityOverTail);
        Assert.False(summary.XpOverTail);
    }

    [Fact]
    public void ActiveBound_refuses_purge_and_delete()
    {
        Assert.False(_store.HasAnyActiveBoundUniqueActors());
        var a = _store.CreateUniqueActor(_store.GetCurrentPlayerId(), "plant", 1);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "purge-guard", "m-pg").Ok);
        Assert.True(_store.TryAckUniqueSpawn("purge-guard", "0xPG", "m-pg").Ok);
        Assert.True(_store.HasAnyActiveBoundUniqueActors());

        var runId = CreateClosedRunWithHotCapture("with-active-bound");
        var purge = _store.PurgeClosedRunCapture(new[] { runId });
        Assert.Equal(0, purge.Deleted);
        Assert.True(purge.Refused >= 1);
        Assert.Equal("unique.active_bound", purge.Reason);
        Assert.True(CountEventsForRun(runId) > 0);

        var del = _store.DeleteClosedRuns(new[] { runId });
        Assert.Equal(0, del.Deleted);
        Assert.Equal("unique.active_bound", del.Reason);
        Assert.Contains(_store.ListRuns(), r => r.Id == runId);
    }

    long CreateClosedRunWithHotCapture(string levelName)
    {
        var matchKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var notify = _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = t, Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                Payload = new { levelName }
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
                Payload = new { levelName, summary = new { duration = 1.0 } }
            }
        });
        Assert.NotEmpty(notify.ClosedRunIds);
        return notify.ClosedRunIds[0];
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
}
