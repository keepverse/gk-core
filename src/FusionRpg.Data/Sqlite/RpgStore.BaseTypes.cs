using FusionRpg.Core.Items.Drops;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>
    /// `item_base_type` — the table both `FusionRpg.Server.ItemBaseTypeCorpus` (item-card display) and
    /// `FusionRpg.Server.BaseTypeSocketMaxCorpus` (module 16 socket cap) name as the real target their
    /// boot-time JSON-on-disk read stands in for. ⚠ <b>Corrected 2026-09-23:</b> both used to claim
    /// "Module 6 shipped the base-type corpus but no `item_base_type` table" / "Deleted the day that
    /// table exists" — the table HAS existed since `loot-content-view-unwired` (2026-09-07,
    /// `RpgStore.Loot.cs`'s `BaseTypesFor` reads it back), so those claims were false for a fortnight
    /// and are corrected in place. Neither Server reader is reused here — both are display/socket-shaped
    /// and Data cannot depend on Server regardless — this is the raw
    /// `(id, frame, role)` triple <see cref="BuildLiveLootContentView"/>'s own `BaseTypesFor` needs, fed
    /// by <see cref="BaseTypeSeedFile"/> (Core).
    /// </summary>
    void EnsureBaseTypeSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS item_base_type (
              id TEXT PRIMARY KEY,
              frame TEXT NOT NULL,
              role TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_item_base_type_frame_role ON item_base_type(frame, role);
            """);
    }

    /// <summary>Whole-corpus replace, one transaction — the same "delete then insert every row" idiom
    /// <see cref="ImportLootCorpus"/> already uses for its own whole-corpus reload. No validator: unlike
    /// a drop table, a base type has no cross-reference for a validator to check (id/frame/role are
    /// each free text here, resolved against real content only at the two call sites that read this
    /// table back).</summary>
    public void ImportBaseTypes(IReadOnlyList<BaseTypeSeedRow> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            ExecIn(db, tx, "DELETE FROM item_base_type;");
            foreach (var row in rows)
                ExecIn(db, tx,
                    "INSERT INTO item_base_type (id, frame, role) VALUES ($id, $frame, $role);",
                    ("$id", row.Id), ("$frame", row.Frame), ("$role", row.Role));

            tx.Commit();
        }
    }

    /// <summary>The forward query <see cref="LootContentView.BaseTypesFor"/> needs: every base-type id
    /// authored for this exact `(frame, role)` pair, ordinal by id (deterministic draw order upstream —
    /// <c>Instantiator</c>'s own roll picks among these by seed, never by insertion order).</summary>
    public IReadOnlyList<string> BaseTypeIdsFor(string frame, string role)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id FROM item_base_type WHERE frame = $frame AND role = $role ORDER BY id;";
            cmd.Parameters.AddWithValue("$frame", frame);
            cmd.Parameters.AddWithValue("$role", role);
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }
    }

    /// <summary>The reverse query: one base type's own `(frame, role)`, or <c>null</c> if this id was
    /// never imported — an `item_generation` stamp's own need when Equipment's `LootGrant` doesn't
    /// already carry both (D3.15's own boss-relic path, which mints a `Unique`-kind container directly
    /// rather than drawing an Equipment entry).</summary>
    public (string Frame, string Role)? GetBaseType(string baseTypeId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT frame, role FROM item_base_type WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", baseTypeId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
        }
    }
}
