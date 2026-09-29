using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `RpgStore.EnsureColumn` is the additive-migration step every boot runs — 41 calls per store on
/// any database that already has the columns. BU1: it swallowed every `ALTER TABLE` failure with
/// `catch { /* already exists */ }`, so a malformed definition, an unknown table or a locked
/// database booted silently and surfaced much later as a missing column.
///
/// <para>These tests pin BOTH halves of the narrowed contract: the idempotent re-run is still
/// tolerated (and the added column is genuinely there and readable), while every other failure
/// reaches the caller. The two propagation cases are the planted violation — they fail against the
/// blanket `catch { }` and pass against the narrowed one.</para>
///
/// <para>Subject is schema evolution, not the disk, so the store is the memory plan
/// (docs/contributing/testing-standard.md R1).</para>
/// </summary>
public class EnsureColumnTests
{
    /// <summary>A table shaped like a pre-migration one: no `theta` column, one committed row.</summary>
    const string LegacyActorDdl =
        "CREATE TABLE legacy_actor(id INTEGER PRIMARY KEY, name TEXT);" +
        "INSERT INTO legacy_actor(id, name) VALUES(1, 'x');";

    static (DataTestStore Store, SqliteConnection Db) OpenMemoryHot()
    {
        var store = DataTestStore.Create();
        return (store, SqliteConnectionFactory.Open(store.Store.HotPath));
    }

    static void ExecRaw(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static long ReadScalarLong(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void An_existing_column_is_tolerated_and_the_added_column_reads_back_with_its_default()
    {
        var (store, db) = OpenMemoryHot();
        using (store)
        using (db)
        {
            ExecRaw(db, LegacyActorDdl);

            // The old-schema row has no `theta`; the migration adds it with a default.
            RpgStore.EnsureColumn(db, "legacy_actor", "theta", "INTEGER NOT NULL DEFAULT 0");

            // Read it back through the normal query path: the pre-existing row carries the default.
            Assert.Equal(0L, ReadScalarLong(db, "SELECT theta FROM legacy_actor WHERE id=1;"));

            // The idempotent re-run every boot takes must be tolerated — and the column must not be
            // duplicated, so this proves the duplicate case specifically, not a blanket swallow.
            RpgStore.EnsureColumn(db, "legacy_actor", "theta", "INTEGER NOT NULL DEFAULT 0");
            Assert.Equal(1L, ReadScalarLong(db,
                "SELECT COUNT(*) FROM pragma_table_info('legacy_actor') WHERE name='theta';"));
        }
    }

    [Fact]
    public void A_second_column_on_an_existing_table_is_added_and_read_back()
    {
        var (store, db) = OpenMemoryHot();
        using (store)
        using (db)
        {
            ExecRaw(db, LegacyActorDdl);

            RpgStore.EnsureColumn(db, "legacy_actor", "empire_id", "TEXT");

            Assert.Equal(1L, ReadScalarLong(db,
                "SELECT COUNT(*) FROM legacy_actor WHERE id=1 AND empire_id IS NULL;"));
        }
    }

    [Fact]
    public void The_tolerated_shape_is_exactly_the_duplicate_column_error()
    {
        var (store, db) = OpenMemoryHot();
        using (store)
        using (db)
        {
            ExecRaw(db, LegacyActorDdl);
            ExecRaw(db, "ALTER TABLE legacy_actor ADD COLUMN theta INTEGER NOT NULL DEFAULT 0;");

            // Real driver exception for the one tolerated shape: re-adding `theta`.
            using var duplicateCmd = db.CreateCommand();
            duplicateCmd.CommandText = "ALTER TABLE legacy_actor ADD COLUMN theta INTEGER NOT NULL DEFAULT 0;";
            var duplicate = Assert.Throws<SqliteException>(() => duplicateCmd.ExecuteNonQuery());
            Assert.True(RpgStore.IsDuplicateColumnError(duplicate, "theta"), duplicate.Message);
            Assert.False(RpgStore.IsDuplicateColumnError(duplicate, "empire_id"), duplicate.Message);

            // A different failure carrying the same SQLITE_ERROR code must not be mistaken for it.
            using var unknownTableCmd = db.CreateCommand();
            unknownTableCmd.CommandText = "ALTER TABLE legacy_actor_typo ADD COLUMN theta INTEGER;";
            var unknownTable = Assert.Throws<SqliteException>(() => unknownTableCmd.ExecuteNonQuery());
            Assert.False(RpgStore.IsDuplicateColumnError(unknownTable, "theta"), unknownTable.Message);
        }
    }

    [Fact]
    public void An_unknown_table_propagates_instead_of_being_swallowed()
    {
        var (store, db) = OpenMemoryHot();
        using (store)
        using (db)
        {
            var ex = Assert.Throws<SqliteException>(
                () => RpgStore.EnsureColumn(db, "legacy_actor_typo", "theta", "INTEGER NOT NULL DEFAULT 0"));

            Assert.True(ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase), ex.Message);
        }
    }

    [Fact]
    public void A_malformed_definition_propagates_instead_of_being_swallowed()
    {
        var (store, db) = OpenMemoryHot();
        using (store)
        using (db)
        {
            ExecRaw(db, LegacyActorDdl);

            var ex = Assert.Throws<SqliteException>(
                () => RpgStore.EnsureColumn(db, "legacy_actor", "bad", "INTEGER NOT NULL DEFAULT"));

            Assert.False(ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase), ex.Message);
        }
    }
}
