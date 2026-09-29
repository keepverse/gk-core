using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Module `memory-storage-plan` (T2): <see cref="SqliteConnectionFactory"/> must be able to open a
/// named shared-memory SQLite database, so a store test can run without creating a file. The file
/// path must behave exactly as before.
/// </summary>
public class MemoryStoragePlanTests
{
    [Fact]
    public void Memory_uri_opens_and_round_trips_sql_without_creating_a_file()
    {
        var uri = SqliteConnectionFactory.MemoryUri("msp-roundtrip-" + Guid.NewGuid().ToString("N"));

        using var keeper = SqliteConnectionFactory.Open(uri);
        using (var cmd = keeper.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES(42);";
            cmd.ExecuteNonQuery();
        }

        // A second connection to the same shared-memory DB sees the first's schema AND rows.
        using var second = SqliteConnectionFactory.Open(uri);
        using (var cmd = second.CreateCommand())
        {
            cmd.CommandText = "SELECT x FROM t;";
            Assert.Equal(42L, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        // It is not a file: the "path" is a memory URI, so nothing was created on disk.
        Assert.StartsWith("file:", uri, StringComparison.Ordinal);
        Assert.Contains("mode=memory", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_uri_is_shared_so_two_connections_share_one_database()
    {
        var uri = SqliteConnectionFactory.MemoryUri("msp-shared-" + Guid.NewGuid().ToString("N"));
        using var a = SqliteConnectionFactory.Open(uri);
        using var b = SqliteConnectionFactory.Open(uri);

        using (var cmd = a.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t(x);";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = b.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO t VALUES(1);";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = a.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM t;";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
    }

    [Fact]
    public void File_path_still_behaves_exactly_as_before()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-msp-file-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "rpg-hot.sqlite");
        try
        {
            using var db = SqliteConnectionFactory.Open(file);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE t(x); INSERT INTO t VALUES(7);";
                cmd.ExecuteNonQuery();
            }
            Assert.True(File.Exists(file), "opening a file path must create the file (unchanged behavior)");

            // The directory was created for a nested path, exactly as before.
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            // Pooling holds the file handle, so clear pools before deleting. A failed delete is a
            // test failure, not a swallowed one (testing-standard.md R3).
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_only_plus_memory_is_rejected()
    {
        var uri = SqliteConnectionFactory.MemoryUri("msp-ro-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteConnectionFactory.Open(uri, readOnly: true));
        Assert.Contains("memory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void File_path_is_not_treated_as_a_memory_uri()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-msp-plain-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "rpg-hot.sqlite");
        try
        {
            using var db = SqliteConnectionFactory.Open(file);
            Assert.True(File.Exists(file));
            Assert.DoesNotContain("mode=memory", Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
