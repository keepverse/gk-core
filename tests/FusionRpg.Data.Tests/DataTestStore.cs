using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data.Tests;

/// <summary>
/// One way to build and dispose a store in a test (module <c>test-store-helper</c>). The default is
/// in-memory — no file, no temp dir, nothing to clean up — and the store is already <c>Init</c>ed.
/// <see cref="CreateFileBacked"/> is the opt-in for the classes that test file semantics (legacy
/// migration, WAL journal mode, <c>File.Exists</c> on hot/media, archive slices on disk, storage
/// purge), and its dispose is leak-proof: it clears SQLite's connection pool before deleting, and a
/// failed delete **throws** rather than being swallowed (docs/contributing/testing-standard.md R3).
///
/// <para>The caller never calls <c>Init()</c> and never calls <c>ClearAllPools()</c>; this helper owns
/// both.</para>
/// </summary>
public sealed class DataTestStore : IDisposable, IAsyncDisposable
{
    /// <summary>The initialized store.</summary>
    public RpgStore Store { get; }

    /// <summary>The file-backed data directory, or <c>null</c> for the memory plan.</summary>
    public string? DataDir { get; }

    readonly string? _hotName;
    readonly string? _mediaName;

    DataTestStore(RpgStore store, string? dataDir, string? hotName = null, string? mediaName = null)
    {
        Store = store;
        DataDir = dataDir;
        _hotName = hotName;
        _mediaName = mediaName;
    }

    /// <summary>An in-memory store with unique database names, initialized and ready to use.</summary>
    public static DataTestStore Create()
    {
        // Names are generated here (not inside InMemory()) so Reopen() can address the same
        // databases; a shared-memory DB is shared by name.
        var id = Guid.NewGuid().ToString("N");
        var hotName = "rpg-hot-" + id;
        var mediaName = "rpg-media-" + id;
        var store = new RpgStore(new RpgStoreOptions { InMemory = true, HotName = hotName, MediaName = mediaName });
        store.Init();
        return new DataTestStore(store, dataDir: null, hotName, mediaName);
    }

    /// <summary>
    /// A **second** store over the same storage, initialized and ready to use — the memory equivalent
    /// of "a fresh process restart sees what the last one committed". For the memory plan it addresses
    /// the same named databases (kept alive by this helper's keepers); for the file plan, the same
    /// directory. The caller owns the returned store and must dispose it.
    /// </summary>
    public RpgStore Reopen()
    {
        var store = DataDir is not null
            ? new RpgStore(DataDir)
            : new RpgStore(new RpgStoreOptions { InMemory = true, HotName = _hotName!, MediaName = _mediaName! });
        store.Init();
        return store;
    }

    /// <summary>
    /// A memory store whose hot database was hand-seeded **before** <c>Init()</c> ran — the memory
    /// equivalent of "a save written by an older build". <paramref name="seedHot"/> receives an open
    /// connection to the hot database (the store's own keeper keeps it alive) and can create a legacy
    /// table shape; the returned store then <c>Init()</c>s over it, so additive-migration paths
    /// (<c>EnsureColumn</c>) are exercised exactly as they are against a real file. Nothing touches
    /// the disk.
    /// </summary>
    public static DataTestStore CreateWithPreInitHot(Action<SqliteConnection> seedHot)
    {
        ArgumentNullException.ThrowIfNull(seedHot);

        var id = Guid.NewGuid().ToString("N");
        var hotName = "rpg-hot-" + id;
        var mediaName = "rpg-media-" + id;

        // Construct first: the store's keepers must be open before we seed, or the shared-memory
        // database would vanish the moment the seeding connection closed.
        var store = new RpgStore(new RpgStoreOptions { InMemory = true, HotName = hotName, MediaName = mediaName });
        using (var seed = SqliteConnectionFactory.Open(store.HotPath))
        {
            seedHot(seed);
        }
        store.Init();
        return new DataTestStore(store, dataDir: null, hotName, mediaName);
    }

    /// <summary>
    /// A real-file store in a unique directory under the test output root, initialized and ready to
    /// use. Dispose deletes the directory; a failed delete throws.
    /// </summary>
    public static DataTestStore CreateFileBacked()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "teststore-" + Guid.NewGuid().ToString("N"));
        var store = new RpgStore(dir);
        store.Init();
        return new DataTestStore(store, dir);
    }

    public void Dispose()
    {
        Store.Dispose(); // releases the memory keepers / closes the file store

        if (DataDir is null)
            return;

        // Pooling holds the file handle past the connection close, so clear pools before deleting.
        // No catch: a cleanup failure must fail the test, never be swallowed.
        SqliteConnection.ClearAllPools();
        Directory.Delete(DataDir, recursive: true);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
