using Microsoft.Data.Sqlite;

namespace FusionRpg.Data.Sqlite;

/// <summary>Opens SQLite with WAL pragmas on every connection.</summary>
public static class SqliteConnectionFactory
{
    /// <summary>
    /// Builds a named shared-memory database URI (<c>file:{name}?mode=memory&amp;cache=shared</c>).
    /// The database exists only while at least one connection to it stays open, so a caller that
    /// wants it to outlive a single command must hold a "keeper" connection (see <c>RpgStore</c>'s
    /// storage plan). Two connections to the same name share one database.
    /// </summary>
    public static string MemoryUri(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"file:{name}?mode=memory&cache=shared";
    }

    /// <summary>True when <paramref name="path"/> is a memory URI rather than a filesystem path.</summary>
    public static bool IsMemoryUri(string path) =>
        path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        && path.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);

    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        if (IsMemoryUri(path))
            return OpenMemory(path, readOnly);

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var db = new SqliteConnection(cs);
        db.Open();
        ApplyWalPragmas(db);
        return db;
    }

    /// <summary>
    /// Opens a shared-memory database. No filesystem path resolution happens here — a memory URI is
    /// not a path, and <see cref="Path.GetFullPath(string)"/> would rewrite it into one.
    /// </summary>
    static SqliteConnection OpenMemory(string uri, bool readOnly)
    {
        // SQLite cannot open a shared-cache in-memory database read-only; reject it explicitly rather
        // than letting the provider fail with a confusing "unable to open database file".
        if (readOnly)
            throw new InvalidOperationException(
                "A shared-cache memory database cannot be opened read-only. Open a memory URI " +
                "read-write, or use a file path when the file is the thing under test.");

        // No WAL on memory: journal_mode degrades to `memory` regardless, and asking for WAL would
        // claim a durability mode this substrate cannot provide. The remaining pragmas still apply.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var db = new SqliteConnection(cs);
        db.Open();
        Exec(db, "PRAGMA busy_timeout=5000;");
        Exec(db, "PRAGMA synchronous=NORMAL;");
        Exec(db, "PRAGMA temp_store=MEMORY;");
        return db;
    }

    public static void ApplyWalPragmas(SqliteConnection db)
    {
        Exec(db, "PRAGMA busy_timeout=5000;");
        Exec(db, "PRAGMA synchronous=NORMAL;");
        Exec(db, "PRAGMA temp_store=MEMORY;");
        Exec(db, "PRAGMA journal_mode=WAL;");
        Exec(db, "PRAGMA wal_autocheckpoint=1000;");
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
