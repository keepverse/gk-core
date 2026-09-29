using FusionRpg.Contracts;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("Category", "DiskSemantics")]
public class RpgStoreSmokeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _dir;

    public RpgStoreSmokeTests()
    {
        // File-bound: this class asserts File.Exists on the hot/media files, so it keeps a real dir --
        // through the leak-proof helper (R2/R3).
        _testStore = DataTestStore.CreateFileBacked();
        _store = _testStore.Store;
        _dir = _testStore.DataDir!;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Init_creates_hot_and_media_sqlite()
    {
        Assert.True(File.Exists(_store.HotPath));
        Assert.True(File.Exists(_store.MediaPath));
        Assert.False(File.Exists(Path.Combine(_dir, LegacyMonoMigrator.LegacyFileName)));
    }

    [Fact]
    public void Roundtrip_player_and_setting()
    {
        var p = _store.CreatePlayer("SliceB");
        Assert.True(p.Id > 0);
        Assert.True(_store.SetCurrentPlayer(p.Id));
        Assert.Equal(p.Id, _store.GetCurrentPlayerId());
        var cur = _store.GetCurrentPlayer();
        Assert.NotNull(cur);
        Assert.Equal("SliceB", cur!.Name);
    }

    [Fact]
    public void InsertEvents_board_start_creates_run()
    {
        var before = _store.CountEvents();
        var matchKey = Guid.NewGuid().ToString("N");
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = matchKey,
                Payload = new { levelName = "slice-b", levelType = "test", boardLevel = 1 }
            }
        });
        Assert.True(_store.CountEvents() > before);
        var runs = _store.ListRuns();
        Assert.Contains(runs, r => string.Equals(r.MatchKey, matchKey, StringComparison.Ordinal));
    }

    [Fact]
    public void Reset_clears_for_tests()
    {
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Kind = "board.start",
                MatchKey = Guid.NewGuid().ToString("N"),
                Payload = new { }
            }
        });
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
        _store.UpsertTypeIconLayers("zombie", 1, new (string Name, string? Source, int Width, int Height, byte[] Png)[]
        {
            ("base", null, 8, 8, png)
        });
        _store.UpsertAlmanacTextDump("plant", 1,
            new Dictionary<string, string?> { ["name"] = "X" }, null);
        Assert.True(_store.CountEvents() > 0);
        Assert.True(_store.HasTypeIconDump("zombie", 1));
        Assert.True(_store.HasAlmanacTextDump("plant", 1));

        _store.Reset();
        Assert.Equal(0, _store.CountEvents());
        Assert.NotNull(_store.GetCurrentPlayer());
        Assert.False(_store.HasTypeIconDump("zombie", 1));
        Assert.False(_store.HasAlmanacTextDump("plant", 1));
    }

    [Fact]
    public void Init_recreates_media_when_hot_exists_and_media_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-nomedia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Hot-only layout (media never created in this folder).
            File.Copy(_store.HotPath, Path.Combine(dir, LegacyMonoMigrator.HotFileName));
            Assert.False(File.Exists(Path.Combine(dir, LegacyMonoMigrator.MediaFileName)));

            using var store = new RpgStore(dir);
            store.Init();
            Assert.True(File.Exists(store.HotPath));
            Assert.True(File.Exists(store.MediaPath));
        }
        finally
        {
            // Pooling holds the file handle, so clear pools before deleting, and let a failed delete
            // fail the test -- the swallow here was a live leak (found by the runtime alarm).
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
