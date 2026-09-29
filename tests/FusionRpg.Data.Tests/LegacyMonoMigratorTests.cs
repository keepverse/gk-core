using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("Category", "DiskSemantics")]
public class LegacyMonoMigratorTests
{
    [Fact]
    public void Migrates_legacy_mono_to_hot_and_media_without_deleting_original()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var legacyPath = Path.Combine(dir, LegacyMonoMigrator.LegacyFileName);
            SeedLegacyMono(legacyPath);

            var store = new RpgStore(dir);
            store.Init();

            Assert.True(File.Exists(store.HotPath));
            Assert.True(File.Exists(store.MediaPath));
            Assert.True(File.Exists(legacyPath), "original rpg.sqlite must remain");
            Assert.True(File.Exists(legacyPath + LegacyMonoMigrator.BakSuffix));

            Assert.NotNull(store.GetCurrentPlayer());
            Assert.True(store.HasTypeIconDump("zombie", 9));
            var png = store.GetTypeIconLayerPng("zombie", 9, "base");
            Assert.NotNull(png);
            Assert.Equal(10, png!.Length);
            Assert.True(store.HasAlmanacTextDump("plant", 2));
            var almanac = store.GetAlmanacTextDump("plant", 2);
            Assert.Equal("Sunflower", almanac!.Fields["name"]);

            Assert.False(LegacyMonoMigrator.TableExists(store.HotPath, "type_icon_layers"));
            Assert.False(LegacyMonoMigrator.TableExists(store.MediaPath, "players"));
        }
        finally
        {
            SqliteConnection.ClearAllPools(); Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryMigrate_is_noop_when_hot_already_exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-mig2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, LegacyMonoMigrator.HotFileName), "placeholder");
            File.WriteAllBytes(Path.Combine(dir, LegacyMonoMigrator.LegacyFileName), new byte[] { 1, 2, 3 });
            Assert.False(LegacyMonoMigrator.TryMigrate(dir));
        }
        finally
        {
            SqliteConnection.ClearAllPools(); Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryMigrate_when_media_exists_and_hot_missing_overwrites_media()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-mig3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var legacyPath = Path.Combine(dir, LegacyMonoMigrator.LegacyFileName);
            SeedLegacyMono(legacyPath);
            // Stale partial media file (not a valid split) — must not throw.
            File.WriteAllBytes(Path.Combine(dir, LegacyMonoMigrator.MediaFileName), new byte[] { 1, 2, 3, 4 });

            Assert.True(LegacyMonoMigrator.TryMigrate(dir));
            Assert.True(File.Exists(Path.Combine(dir, LegacyMonoMigrator.HotFileName)));
            Assert.True(File.Exists(Path.Combine(dir, LegacyMonoMigrator.MediaFileName)));
            Assert.True(LegacyMonoMigrator.TableExists(Path.Combine(dir, LegacyMonoMigrator.MediaFileName), "type_icon_layers"));
            Assert.False(LegacyMonoMigrator.TableExists(Path.Combine(dir, LegacyMonoMigrator.HotFileName), "type_icon_layers"));
        }
        finally
        {
            SqliteConnection.ClearAllPools(); Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Heal_moves_orphan_media_tables_from_hot_to_media()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-heal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Simulate partial migrate: hot is a full mono copy; media missing.
            var hotPath = Path.Combine(dir, LegacyMonoMigrator.HotFileName);
            SeedLegacyMono(hotPath);
            Assert.True(LegacyMonoMigrator.TableExists(hotPath, "type_icon_layers"));
            Assert.False(File.Exists(Path.Combine(dir, LegacyMonoMigrator.MediaFileName)));

            var store = new RpgStore(dir);
            store.Init();

            Assert.False(LegacyMonoMigrator.TableExists(store.HotPath, "type_icon_layers"));
            Assert.True(LegacyMonoMigrator.TableExists(store.MediaPath, "type_icon_layers"));
            Assert.True(store.HasTypeIconDump("zombie", 9));
            Assert.True(store.HasAlmanacTextDump("plant", 2));
            var png = store.GetTypeIconLayerPng("zombie", 9, "base");
            Assert.NotNull(png);
            Assert.Equal(10, png!.Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); Directory.Delete(dir, recursive: true);
        }
    }

    static void SeedLegacyMono(string path)
    {
        using var db = SqliteConnectionFactory.Open(path);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE players (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  name TEXT NOT NULL,
                  created_utc TEXT NOT NULL
                );
                CREATE TABLE settings (
                  key TEXT PRIMARY KEY,
                  json TEXT NOT NULL,
                  updated_utc TEXT NOT NULL
                );
                CREATE TABLE type_icon_layers (
                  side TEXT NOT NULL,
                  type_id INTEGER NOT NULL,
                  layer TEXT NOT NULL,
                  source TEXT,
                  width INTEGER,
                  height INTEGER,
                  png BLOB NOT NULL,
                  captured_utc TEXT NOT NULL,
                  PRIMARY KEY (side, type_id, layer)
                );
                CREATE TABLE type_icons (
                  side TEXT NOT NULL,
                  type_id INTEGER NOT NULL,
                  png BLOB NOT NULL,
                  recipe_json TEXT,
                  updated_utc TEXT NOT NULL,
                  PRIMARY KEY (side, type_id)
                );
                CREATE TABLE type_almanac_dump (
                  side TEXT NOT NULL,
                  type_id INTEGER NOT NULL,
                  fields_json TEXT NOT NULL,
                  sources_json TEXT,
                  captured_utc TEXT NOT NULL,
                  PRIMARY KEY (side, type_id)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        var t = DateTime.UtcNow.ToString("o");
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO players(name, created_utc) VALUES ('Legacy', $t);";
            cmd.Parameters.AddWithValue("$t", t);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO settings(key, json, updated_utc) VALUES ('currentPlayerId', '1', $t);";
            cmd.Parameters.AddWithValue("$t", t);
            cmd.ExecuteNonQuery();
        }
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO type_icon_layers(side, type_id, layer, source, width, height, png, captured_utc)
                VALUES ('zombie', 9, 'base', 'seed', 8, 8, $png, $t);
                """;
            cmd.Parameters.AddWithValue("$png", png);
            cmd.Parameters.AddWithValue("$t", t);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO type_almanac_dump(side, type_id, fields_json, sources_json, captured_utc)
                VALUES ('plant', 2, '{"name":"Sunflower"}', NULL, $t);
                """;
            cmd.Parameters.AddWithValue("$t", t);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
    }
}
