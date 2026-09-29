using Microsoft.Data.Sqlite;

namespace FusionRpg.Data.Sqlite;

/// <summary>
/// One-shot legacy mono DB → hot + media. Never deletes <c>rpg.sqlite</c>.
/// Also heals orphan media tables left on hot after a partial migrate.
/// </summary>
public static class LegacyMonoMigrator
{
    public const string LegacyFileName = "rpg.sqlite";
    public const string HotFileName = "rpg-hot.sqlite";
    public const string MediaFileName = "rpg-media.sqlite";
    public const string BakSuffix = ".pre-dal.bak";

    public static readonly string[] MediaTables =
    {
        "type_icon_layers",
        "type_icons",
        "type_almanac_dump"
    };

    /// <summary>
    /// If hot is missing and legacy exists: backup (once), copy/split into hot+media.
    /// Returns true when a migration ran.
    /// </summary>
    public static bool TryMigrate(string dataDir, TextWriter? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        Directory.CreateDirectory(dataDir);

        var hotPath = Path.Combine(dataDir, HotFileName);
        var mediaPath = Path.Combine(dataDir, MediaFileName);
        var legacyPath = Path.Combine(dataDir, LegacyFileName);

        if (File.Exists(hotPath))
            return false;
        if (!File.Exists(legacyPath))
            return false;

        var bakPath = legacyPath + BakSuffix;
        Checkpoint(legacyPath);
        if (!File.Exists(bakPath))
            File.Copy(legacyPath, bakPath);

        File.Copy(legacyPath, hotPath, overwrite: false);
        // overwrite:true — stale/partial media must not throw when hot is being rebuilt from legacy
        File.Copy(legacyPath, mediaPath, overwrite: true);

        DropTables(hotPath, MediaTables);
        DropNonMediaTables(mediaPath);

        log?.WriteLine($"[data] migrated legacy {LegacyFileName} → {HotFileName}+{MediaFileName} (bak={Path.GetFileName(bakPath)})");
        return true;
    }

    /// <summary>
    /// If hot still holds media tables (partial migrate / stranded layout), copy rows into media then drop from hot.
    /// </summary>
    public static bool HealOrphanMediaTables(string dataDir, TextWriter? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        var hotPath = Path.Combine(dataDir, HotFileName);
        var mediaPath = Path.Combine(dataDir, MediaFileName);

        if (!File.Exists(hotPath))
            return false;

        var orphans = MediaTables.Where(t => TableExists(hotPath, t)).ToArray();
        if (orphans.Length == 0)
            return false;

        Directory.CreateDirectory(dataDir);
        EnsureMediaSchemaFile(mediaPath);

        using (var media = SqliteConnectionFactory.Open(mediaPath))
        {
            var attachPath = hotPath.Replace("'", "''", StringComparison.Ordinal);
            using (var attach = media.CreateCommand())
            {
                attach.CommandText = $"ATTACH DATABASE '{attachPath}' AS hot;";
                attach.ExecuteNonQuery();
            }

            foreach (var table in orphans)
            {
                using var copy = media.CreateCommand();
                copy.CommandText = $"INSERT OR REPLACE INTO {table} SELECT * FROM hot.{table};";
                copy.ExecuteNonQuery();
            }

            using (var detach = media.CreateCommand())
            {
                detach.CommandText = "DETACH DATABASE hot;";
                detach.ExecuteNonQuery();
            }
        }

        DropTables(hotPath, orphans);
        log?.WriteLine("[data] healed orphan media tables from hot → media");
        return true;
    }

    public static bool TableExists(string dbPath, string table)
    {
        if (!File.Exists(dbPath)) return false;
        using var db = SqliteConnectionFactory.Open(dbPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() != null;
    }

    static void EnsureMediaSchemaFile(string mediaPath)
    {
        using var db = SqliteConnectionFactory.Open(mediaPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS type_icon_layers (
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
            CREATE INDEX IF NOT EXISTS ix_type_icon_layers_side ON type_icon_layers(side, type_id);
            CREATE TABLE IF NOT EXISTS type_icons (
              side TEXT NOT NULL,
              type_id INTEGER NOT NULL,
              png BLOB NOT NULL,
              recipe_json TEXT,
              updated_utc TEXT NOT NULL,
              PRIMARY KEY (side, type_id)
            );
            CREATE TABLE IF NOT EXISTS type_almanac_dump (
              side TEXT NOT NULL,
              type_id INTEGER NOT NULL,
              fields_json TEXT NOT NULL,
              sources_json TEXT,
              captured_utc TEXT NOT NULL,
              PRIMARY KEY (side, type_id)
            );
            CREATE INDEX IF NOT EXISTS ix_type_almanac_dump_side ON type_almanac_dump(side, type_id);
            PRAGMA journal_mode=WAL;
            """;
        cmd.ExecuteNonQuery();
    }

    static void Checkpoint(string dbPath)
    {
        using var db = SqliteConnectionFactory.Open(dbPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    static void DropTables(string dbPath, IEnumerable<string> tables)
    {
        using var db = SqliteConnectionFactory.Open(dbPath);
        foreach (var table in tables)
        {
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"DROP TABLE IF EXISTS {table};";
                cmd.ExecuteNonQuery();
            }
            catch { /* ignore */ }
        }
    }

    static void DropNonMediaTables(string mediaPath)
    {
        using var db = SqliteConnectionFactory.Open(mediaPath);
        var keep = new HashSet<string>(MediaTables, StringComparer.OrdinalIgnoreCase);
        var toDrop = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                if (!keep.Contains(name))
                    toDrop.Add(name);
            }
        }
        foreach (var table in toDrop)
        {
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"DROP TABLE IF EXISTS \"{table.Replace("\"", "\"\"")}\";";
                cmd.ExecuteNonQuery();
            }
            catch { /* ignore */ }
        }
        var indexes = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                indexes.Add(r.GetString(0));
        }
        foreach (var ix in indexes)
        {
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"DROP INDEX IF EXISTS \"{ix.Replace("\"", "\"\"")}\";";
                cmd.ExecuteNonQuery();
            }
            catch { /* ignore */ }
        }
    }
}
