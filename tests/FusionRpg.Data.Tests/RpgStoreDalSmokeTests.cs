using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("Category", "DiskSemantics")]
public class RpgStoreDalSmokeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _dir;

    public RpgStoreDalSmokeTests()
    {
        // File-bound: this class asserts WAL journal mode and real hot/media files, so it keeps a real
        // dir -- through the leak-proof helper (R2/R3).
        _testStore = DataTestStore.CreateFileBacked();
        _store = _testStore.Store;
        _dir = _testStore.DataDir!;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Init_leaves_journal_mode_wal_on_hot_and_media()
    {
        AssertWal(_store.HotPath);
        AssertWal(_store.MediaPath);
    }

    static void AssertWal(string path)
    {
        using var db = SqliteConnectionFactory.Open(path);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = Convert.ToString(cmd.ExecuteScalar());
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public void IRpgDb_Init_works()
    {
        IRpgDb db = _store;
        db.Init();
        Assert.True(File.Exists(_store.HotPath));
        Assert.True(File.Exists(_store.MediaPath));
    }

    [Fact]
    public void Icon_png_upsert_and_get_roundtrip()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
        var n = _store.UpsertTypeIconLayers("zombie", 7, new (string Name, string? Source, int Width, int Height, byte[] Png)[]
        {
            ("base", "test", 16, 16, png)
        });
        Assert.True(n > 0);
        Assert.True(_store.HasTypeIconDump("zombie", 7));
        var dump = _store.GetTypeIconDump("zombie", 7);
        Assert.NotNull(dump);
        Assert.Contains(dump!.Layers, l => l.Name == "base");
        var got = _store.GetTypeIconLayerPng("zombie", 7, "base");
        Assert.NotNull(got);
        Assert.Equal(png, got);

        Assert.True(LegacyMonoMigrator.TableExists(_store.MediaPath, "type_icon_layers"));
        Assert.False(LegacyMonoMigrator.TableExists(_store.HotPath, "type_icon_layers"));
        Assert.False(LegacyMonoMigrator.TableExists(_store.HotPath, "type_icons"));
    }

    [Fact]
    public void Rift_asset_provenance_validates_pvz_dump_key_and_hashes_bytes_in_media_db()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01 };
        _store.UpsertTypeIconLayers("plant", 9, new (string Name, string? Source, int Width, int Height, byte[] Png)[]
        {
            ("base", "captured", 32, 32, png)
        });

        Assert.False(_store.UpsertRiftAssetSource(
            "onboarding.rift.storySprite", "storySprite", "pvz_dump", "plant", 9, "missing",
            "dump://plant/9/missing", png));

        Assert.True(_store.UpsertRiftAssetSource(
            "onboarding.rift.storySprite", "storySprite", "pvz_dump", "PLANT", 9, "base",
            "dump://plant/9/base", png, revision: 1, capturedUtc: "2026-01-01T00:00:00Z"));
        var row = _store.GetRiftAssetSource("onboarding.rift.storySprite", "storySprite");
        Assert.NotNull(row);
        Assert.Equal("plant", row!.Side);
        Assert.Equal("base", row.Layer);
        Assert.Equal("pvz_dump", row.SourceKind);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant(), row.Sha256);

        using var media = SqliteConnectionFactory.Open(_store.MediaPath);
        Assert.Equal(1L, Convert.ToInt64(new SqliteCommand("SELECT COUNT(*) FROM rift_asset_sources;", media).ExecuteScalar()));
        Assert.False(LegacyMonoMigrator.TableExists(_store.HotPath, "rift_asset_sources"));
    }

    [Fact]
    public void Almanac_text_dump_upsert_promotes_types_on_hot()
    {
        _store.UpsertAlmanacTextDump(
            "plant",
            3,
            new Dictionary<string, string?>
            {
                ["name"] = "Peashooter",
                ["enumName"] = "Peashooter"
            },
            new Dictionary<string, string> { ["name"] = "almanac" });
        Assert.True(_store.HasAlmanacTextDump("plant", 3));
        Assert.True(LegacyMonoMigrator.TableExists(_store.MediaPath, "type_almanac_dump"));
        Assert.False(LegacyMonoMigrator.TableExists(_store.HotPath, "type_almanac_dump"));

        var types = _store.ListTypes("plant");
        var row = Assert.Single(types, t => t.Type == 3);
        Assert.Equal("Peashooter", row.DisplayName);
        Assert.Equal("Peashooter", row.TypeName);
    }

    [Fact]
    public void Activity_append_updates_rollup()
    {
        var player = _store.CreatePlayer("ActivitySmoke");
        var result = _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchStarted,
            DedupeKey = "act-match-1"
        });
        Assert.Equal(1, result.Rollup.MatchesStarted);
        var rollup = _store.GetPvzActivityRollup(player.Id);
        Assert.NotNull(rollup);
        Assert.Equal(1, rollup!.MatchesStarted);

        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieKilled,
            PayloadJson = """{"type":1}""",
            DedupeKey = "act-zk-1"
        });
        rollup = _store.GetPvzActivityRollup(player.Id);
        Assert.NotNull(rollup);
        Assert.Equal(1, rollup!.ZombiesKilled);
    }

    [Fact]
    public void Progression_seed_lists_actors()
    {
        var player = _store.CreatePlayer("ProgSmoke");
        var summary = _store.SeedRpgProgressionDemo(player.Id);
        Assert.NotNull(summary);
        var list = _store.ListRpgProgression(player.Id, null);
        Assert.NotNull(list);
        Assert.True(list!.Total > 0);
        Assert.NotEmpty(list.Items);
    }
}
