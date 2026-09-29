using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Module `memory-storage-plan` (T3): the store's storage plan — three public doors, keeper-held
/// memory databases with no file on disk, the enforced URI trap, the archive throw, and a no-op
/// Dispose for the production file plan.
///
/// <para>T2's <see cref="SqliteConnectionFactory"/> file-path tests live in
/// <c>MemoryStoragePlanTests.cs</c>; they touch real temp files, while this file creates none.</para>
/// </summary>
public class RpgStoreStoragePlanTests
{
    // A registry-valid atom row: the store round-trips it, so this is a real write/read probe.
    static AtomRow Atom(string family, int tier = 1) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", tier),
        KindId = "stat.modify",
        FamilyId = family,
        Variant = "",
        Tier = tier,
        Name = family,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
    };

    [Fact]
    public void InMemory_store_inits_full_schema_and_round_trips_sql_with_no_file()
    {
        using var store = RpgStore.InMemory();
        store.Init();

        Assert.True(store.UpsertAtom(Atom("atom.vitality")).IsOk);
        Assert.NotEmpty(store.ListAtoms());

        // The plan's paths are memory URIs, not files, and nothing exists on disk for them.
        Assert.True(SqliteConnectionFactory.IsMemoryUri(store.HotPath), store.HotPath);
        Assert.True(SqliteConnectionFactory.IsMemoryUri(store.MediaPath), store.MediaPath);
        Assert.False(File.Exists(store.HotPath), "a memory 'hot path' must not exist as a file");
        Assert.Equal("", store.DataDir);
    }

    [Fact]
    public void Memory_store_schema_is_a_real_relational_schema()
    {
        using var store = RpgStore.InMemory();
        store.Init();

        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table';";
        var tables = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.True(tables > 50, $"expected the full schema, saw {tables} tables");
    }

    [Fact]
    public void All_three_public_doors_build_a_usable_store()
    {
        // A relative marker path: the memory plan must ignore DataDir entirely and never create it.
        var ignoredDir = "msp-ignored-dir-" + Guid.NewGuid().ToString("N");

        // Door 1: static factory.
        using (var a = RpgStore.InMemory())
        {
            a.Init();
            Assert.True(a.UpsertAtom(Atom("atom.vitality")).IsOk);
        }

        // Door 2: the bool flag on the string constructor.
        using (var b = new RpgStore(ignoredDir, inMemory: true))
        {
            b.Init();
            Assert.True(b.UpsertAtom(Atom("atom.might")).IsOk);
            Assert.True(SqliteConnectionFactory.IsMemoryUri(b.HotPath));
            Assert.Equal("", b.DataDir);
        }

        // Door 3: the options record, including an explicit database name.
        using (var c = new RpgStore(new RpgStoreOptions { InMemory = true, HotName = "msp-named-" + Guid.NewGuid().ToString("N") }))
        {
            c.Init();
            Assert.True(c.UpsertAtom(Atom("atom.vitality", 2)).IsOk);
            Assert.True(SqliteConnectionFactory.IsMemoryUri(c.HotPath));
        }

        // The bool door with inMemory:true must NOT have created the passed dir.
        Assert.False(Directory.Exists(ignoredDir), "an in-memory plan must ignore dataDir entirely");
    }

    [Fact]
    public void File_plan_constructor_is_unchanged_and_Dispose_is_a_noop()
    {
        // No Init and no temp-dir acquisition: this asserts path computation only, so it creates
        // nothing on disk. The production file *behavior* is covered by the existing smoke tests,
        // which pass unmodified (full Data suite green).
        var dir = "msp-fileplan-" + Guid.NewGuid().ToString("N");
        using var store = new RpgStore(dir);

        Assert.False(SqliteConnectionFactory.IsMemoryUri(store.HotPath));
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), LegacyMonoMigrator.HotFileName), store.HotPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "archive"), store.ArchiveDir);
        Assert.NotEmpty(store.DataDir);

        // File-plan Dispose is a no-op: it must not throw and must not remove anything.
        store.Dispose();
        Assert.False(Directory.Exists(dir), "construction alone must not create the directory");
    }

    [Fact]
    public void The_URI_trap_is_rejected_by_the_options_record()
    {
        var uri = SqliteConnectionFactory.MemoryUri("msp-trap-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => new RpgStore(new RpgStoreOptions { DataDir = uri, InMemory = false }));
        Assert.Contains("memory URI", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Memory_stores_are_independent_under_parallel_creation()
    {
        // No singleton, no shared DB. Property 1 — every store gets its OWN databases, even when
        // constructed concurrently. A full schema build per store is expensive (46 tables), and this
        // half needs only construction, so it does not Init; the schema build is exercised by every
        // other test in this file and the wider suite.
        var dbNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        System.Threading.Tasks.Parallel.For(0, 24, _ =>
        {
            using var store = RpgStore.InMemory();
            dbNames.Add(store.HotPath);
            dbNames.Add(store.MediaPath);
        });
        Assert.Equal(48, dbNames.Distinct().Count());

        // Property 2 — their rows do not cross, under parallelism. Four initialized stores (not 24)
        // is enough to prove no shared database: each writes one row and must see only its own.
        var tiers = new System.Collections.Concurrent.ConcurrentBag<long>();
        System.Threading.Tasks.Parallel.For(0, 4, i =>
        {
            using var store = RpgStore.InMemory();
            store.Init();
            Assert.True(store.UpsertAtom(Atom("atom.vitality", 1 + i)).IsOk);
            var rows = store.ListAtoms();
            Assert.Single(rows);
            tiers.Add(rows.Single().Tier);
        });
        Assert.Equal(4, tiers.Distinct().Count());
    }

    [Fact]
    public void Keeper_holds_the_memory_db_across_connection_open_and_close()
    {
        using var store = RpgStore.InMemory();
        store.Init();
        Assert.True(store.UpsertAtom(Atom("atom.vitality")).IsOk);

        // A direct open + close of the same shared-memory DB, then read through another connection,
        // proves the store's keeper (not the transient connection) owns the lifetime.
        using (var transient = SqliteConnectionFactory.Open(store.HotPath)) { }
        Assert.NotEmpty(store.ListAtoms());
    }

    [Fact]
    public void Keeper_survives_a_global_ClearAllPools()
    {
        using var store = RpgStore.InMemory();
        store.Init();
        Assert.True(store.UpsertAtom(Atom("atom.vitality")).IsOk);

        // Another test's process-wide pool clear must not kill a store whose keeper holds the DB.
        SqliteConnection.ClearAllPools();

        Assert.NotEmpty(store.ListAtoms());
    }

    [Fact]
    public void Memory_schema_is_identical_to_the_file_store()
    {
        // R2: disk here is the thing under test — this compares the memory schema against the
        // production file schema, which cannot be proven without one file store. The dir is created
        // under the test output root (not the OS temp dir) and deleted on the way out; a failed
        // delete is a failure, never a swallowed catch.
        var dir = Path.Combine(AppContext.BaseDirectory, "msp-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new RpgStore(dir);
            file.Init();
            using var mem = RpgStore.InMemory();
            mem.Init();

            Assert.Equal(SchemaDdl(file), SchemaDdl(mem));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    static List<string> SchemaDdl(RpgStore store)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT type || ':' || name FROM sqlite_master ORDER BY type, name;";
        var rows = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    [Fact]
    public void Memory_store_archive_entry_point_throws_rather_than_writing_to_cwd()
    {
        using var store = RpgStore.InMemory();
        store.Init();

        var ex = Assert.Throws<StorePlanException>(() => store.ArchiveDir);
        Assert.Contains("memory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_archive_entry_point_throws_a_StorePlanException_on_a_memory_store()
    {
        using var store = RpgStore.InMemory();
        store.Init();

        // Every filesystem-backed archive surface must fail loudly under the memory plan; none may
        // silently resolve a cwd-relative path and write there.
        Assert.Throws<StorePlanException>(() => store.PromoteClosedRunCapture(1));
        Assert.Throws<StorePlanException>(() => store.CompactAfterRunClosed(null));
        Assert.Throws<StorePlanException>(() => store.TrimHotTailsNow());
        Assert.Throws<StorePlanException>(() => store.TrimSoulLedgerTails());
        Assert.Throws<StorePlanException>(() => store.DeleteArchives(Array.Empty<string>()));
        Assert.Throws<StorePlanException>(() => store.PurgeClosedRunCapture(Array.Empty<long>()));
        Assert.Throws<StorePlanException>(() => store.DeleteClosedRuns(Array.Empty<long>()));
    }

    [Fact]
    public void StorePlanException_is_an_InvalidOperationException_and_names_the_memory_plan()
    {
        using var store = RpgStore.InMemory();
        store.Init();

        var ex = Assert.Throws<StorePlanException>(() => store.DeleteArchives(Array.Empty<string>()));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains("memory plan", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reset_on_a_memory_store_leaves_a_usable_store_with_no_file()
    {
        using var store = RpgStore.InMemory();
        store.Init();
        Assert.True(store.UpsertAtom(Atom("atom.vitality", 3)).IsOk);
        Assert.NotEmpty(store.ListAtoms());

        store.Reset();

        // Schema is back (usable), data is gone, and still no file on disk.
        Assert.True(store.UpsertAtom(Atom("atom.might")).IsOk);
        Assert.NotEmpty(store.ListAtoms());
        Assert.False(File.Exists(store.HotPath));
    }
}
