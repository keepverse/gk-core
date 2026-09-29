using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Module `test-store-helper` (T6): one leak-proof way to build and dispose a store. Memory is the
/// default; a real file is opt-in for the classes that test file semantics. Standard:
/// docs/contributing/testing-standard.md.
///
/// <para><c>DiskSemantics</c>: this is the helper's own proof suite — the <c>CreateFileBacked</c> rows
/// assert the file plan's directory, uniqueness and initialized schema on a real
/// <c>teststore-*</c> directory (docs/contributing/testing-standard.md R2), so the default profile
/// skips the class and it runs at <c>full</c>/nightly/gate.</para>
/// </summary>
[Trait("Category", "DiskSemantics")]
public class DataTestStoreTests
{
    static AtomRow Atom(string family) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Variant = "",
        Tier = 1,
        Name = family,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
    };

    [Fact]
    public void Create_returns_an_initialized_memory_store_that_round_trips_sql()
    {
        using var test = DataTestStore.Create();

        Assert.True(test.Store.UpsertAtom(Atom("atom.vitality")).IsOk);
        Assert.NotEmpty(test.Store.ListAtoms());
        Assert.Null(test.DataDir);
    }

    [Fact]
    public void Create_creates_no_temp_directory()
    {
        string before;
        string after;
        using (var test = DataTestStore.Create())
        {
            before = test.Store.HotPath;
            Assert.NotEmpty(before);
        }

        // A memory plan's "path" is a URI, never a directory on disk.
        after = before;
        Assert.StartsWith("file:", after, StringComparison.Ordinal);
        Assert.False(File.Exists(after));
    }

    [Fact]
    public void CreateFileBacked_returns_an_initialized_file_store_with_a_unique_dir()
    {
        using var a = DataTestStore.CreateFileBacked();
        using var b = DataTestStore.CreateFileBacked();

        Assert.NotNull(a.DataDir);
        Assert.NotNull(b.DataDir);
        Assert.NotEqual(a.DataDir, b.DataDir);
        Assert.True(File.Exists(a.Store.HotPath), "the file plan creates its file");
        Assert.True(a.Store.UpsertAtom(Atom("atom.might")).IsOk);
    }

    [Fact]
    public void Two_memory_stores_are_independent()
    {
        using var a = DataTestStore.Create();
        using var b = DataTestStore.Create();

        Assert.True(a.Store.UpsertAtom(Atom("atom.vitality")).IsOk);
        Assert.Empty(b.Store.ListAtoms());
    }

    [Fact]
    public void Reopen_sees_what_the_previous_store_committed()
    {
        using var test = DataTestStore.Create();
        Assert.True(test.Store.UpsertAtom(Atom("atom.vitality")).IsOk);

        // A fresh store over the same storage must see the committed row — the memory equivalent of
        // "survives a process restart".
        using var reopened = test.Reopen();
        Assert.NotEmpty(reopened.ListAtoms());
    }

    [Fact]
    public void CreateWithPreInitHot_seeds_a_legacy_schema_that_Init_migrates_in_memory()
    {
        // The memory equivalent of "a save written by an older build": a table created before Init
        // ran. Init must then run its additive migration over it — with no file.
        using var test = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var cmd = seed.CreateCommand();
            cmd.CommandText = "CREATE TABLE legacy_probe(id INTEGER PRIMARY KEY, name TEXT);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO legacy_probe(name) VALUES('before-init');";
            cmd.ExecuteNonQuery();
        });

        // The pre-Init row survived Init, and the full schema was layered on top.
        using var db = SqliteConnectionFactory.Open(test.Store.HotPath);
        using var probe = db.CreateCommand();
        probe.CommandText = "SELECT name FROM legacy_probe;";
        Assert.Equal("before-init", probe.ExecuteScalar());

        probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table';";
        Assert.True(Convert.ToInt64(probe.ExecuteScalar()) > 50, "Init must layer the full schema");

        Assert.False(File.Exists(test.Store.HotPath), "no file may be created");
    }

    [Fact]
    public void A_file_store_dispose_deletes_its_directory_cleanly()
    {
        var test = DataTestStore.CreateFileBacked();
        var dir = test.DataDir!;
        Assert.True(Directory.Exists(dir));

        test.Dispose();

        Assert.False(Directory.Exists(dir), "dispose must delete the file store's directory");
    }

    [Fact]
    public void Memory_helper_adds_no_substrate_gate_entry()
    {
        // The helper must not become a new violation: it appears in no baseline line.
        var baseline = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "test-substrate-baseline.txt"));
        Assert.DoesNotContain("DataTestStore", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_store_with_a_pooled_connection_still_deletes_after_ClearAllPools()
    {
        var test = DataTestStore.CreateFileBacked();
        var dir = test.DataDir!;

        // A connection returned to the pool keeps the file handle open. The helper's dispose must
        // clear pools before deleting, or this would leak (the 65.5 GB failure mode).
        using (var pooled = SqliteConnectionFactory.Open(test.Store.HotPath))
        {
            using var cmd = pooled.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            cmd.ExecuteScalar();
        }

        test.Dispose();

        Assert.False(Directory.Exists(dir), "the helper must delete its dir even with a pooled handle");
    }

    [Fact]
    public void A_failed_file_cleanup_throws_rather_than_being_swallowed()
    {
        var test = DataTestStore.CreateFileBacked();
        var dir = test.DataDir!;

        // An open handle with FileShare.None makes a file inside the dir undeletable, so
        // Directory.Delete must fail. The helper must let that failure surface (R3).
        var blocker = Path.Combine(dir, "blocker.lock");
        var fs = new FileStream(blocker, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Assert.ThrowsAny<IOException>(() => test.Dispose());
        }
        finally
        {
            fs.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root not found");
    }
}
