using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

// scoped-inventory-hierarchy module 2 (`sector-storage`),
// `docs/architecture/scoped-inventory-hierarchy/spec-sector-storage.md` §Design 4.
//
// A sector's item storage: a reachability+capacity overlay over the `rpg_item`/
// `rpg_item_stock` ownership root, keyed `(world_id, sector_id, seq)` with the same
// `kind ∈ {instance, stack}` split `corpse-cache` and `legion-cargo` already use.
// Deliberately NO `weight_each` column (§Design 2: single slot-count scalar, no weight
// gate for this scope) and deliberately NO owner/faction column of any kind (§Design 5:
// the absence is the mechanism — reachability is derived live from the sector's own
// *current* `OwnerFactionId`, so a capture flips reachability with zero rows migrated).

/// <summary>One item row sitting in a sector's storage.</summary>
public sealed record SectorStorageRow(
    string WorldId,
    string SectorId,
    int Seq,
    string Kind,
    string? InstanceId,
    string? ContainerId,
    long? Qty);

/// <summary>
/// Sector storage — the slot-bound item overlay per <c>WorldSector</c>
/// (spec-sector-storage.md). SQL lives here, only here (guard-dal).
///
/// <para>An item is "in a sector's storage" purely by existing in
/// <c>rpg_world_sector_storage</c> with no corresponding armoury-listing row, identical
/// to <c>legion-cargo</c>'s own "reachable by owner, not by a second column" shape.
/// <c>rpg_item.player_id</c>/<c>disposition</c> are never touched by this module:
/// deposit/withdraw verbs are module 3 (<c>cargo-transfer</c>)'s own deliverable, not
/// built here — this file ships the schema, the row-count read its capacity check needs
/// (§Design 3: one row, one slot, regardless of stack size), the live reachability read,
/// and the capture hook site (§Design 5).</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>
    /// Test probe: when non-null, invoked inside <see cref="OnSectorCapturedUnlocked"/> —
    /// i.e. after <c>DiffSectors</c>'s sector-owner write but still inside the turn-commit
    /// transaction, before <c>tx.Commit()</c>. Lets the atomicity suite force a
    /// mid-commit failure through the real code path: a crash there must leave the
    /// sector's <c>OwnerFactionId</c> and its storage rows' observable reachability in
    /// the same pre-capture state. Production never sets this; it defaults to null.
    /// </summary>
    internal static Action? TestProbeSectorCapture;

    void EnsureSectorStorageSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_world_sector_storage (
              world_id     TEXT NOT NULL,
              sector_id    TEXT NOT NULL,
              seq          INTEGER NOT NULL,
              kind         TEXT NOT NULL,
              instance_id  TEXT,
              container_id TEXT,
              qty          INTEGER,
              PRIMARY KEY (world_id, sector_id, seq),
              FOREIGN KEY (world_id, sector_id) REFERENCES rpg_world_sectors(world_id, sector_id) ON DELETE CASCADE
            );
            """);
    }

    // ---- reads (module 3's capacity + reachability inputs, §Design 3 / §Interface) ----------

    static SectorStorageRow ReadSectorStorageRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt64(6));

    internal List<SectorStorageRow> ListSectorStorageUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string worldId, string sectorId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT world_id, sector_id, seq, kind, instance_id, container_id, qty
            FROM rpg_world_sector_storage WHERE world_id = $w AND sector_id = $s ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        using var r = cmd.ExecuteReader();
        var list = new List<SectorStorageRow>();
        while (r.Read()) list.Add(ReadSectorStorageRow(r));
        return list;
    }

    /// <summary>
    /// Every row stored at a sector, in stable <c>seq</c> order. The capture test asserts on
    /// this read directly: after a capture the rows must be byte-identical (zero migrated).
    /// </summary>
    public IReadOnlyList<SectorStorageRow> ListSectorStorage(string worldId, string sectorId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ListSectorStorageUnlocked(db, tx: null, worldId, sectorId);
        }
    }

    internal int SectorStorageSlotCountUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string worldId, string sectorId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM rpg_world_sector_storage WHERE world_id = $w AND sector_id = $s;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Row count, never summed <c>qty</c> (spec §Design 3): one <c>kind='stack'</c> row
    /// holding 500 of a fungible material occupies exactly one storage slot, the same as
    /// one <c>kind='instance'</c> row. Module 3 compares this against
    /// <c>SectorItemCapacity.EffectiveCapacity</c> before every deposit.
    /// </summary>
    public int SectorStorageSlotCount(string worldId, string sectorId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return SectorStorageSlotCountUnlocked(db, tx: null, worldId, sectorId);
        }
    }

    internal string? ReadSectorOwnerUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string worldId, string sectorId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT owner_faction_id FROM rpg_world_sectors WHERE world_id = $w AND sector_id = $s;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? null : (string)v;
    }

    internal bool SectorStorageReachableByUnlocked(
        SqliteConnection db, SqliteTransaction? tx,
        string worldId, string sectorId, string? factionId) =>
        string.Equals(ReadSectorOwnerUnlocked(db, tx, worldId, sectorId), factionId,
            StringComparison.Ordinal);

    /// <summary>
    /// The reachability rule itself (spec §Interface, for module 3): no owner column on
    /// the storage row — whoever calls in must compare the acting legion's
    /// <c>OwnerFactionId</c> against the sector's own <b>current</b>
    /// <c>OwnerFactionId</c>, read live, never cached. This module exposes the read; the
    /// refusal itself is module 3's job.
    /// </summary>
    public bool SectorStorageReachableBy(string worldId, string sectorId, string? factionId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return SectorStorageReachableByUnlocked(db, tx: null, worldId, sectorId, factionId);
        }
    }

    // ---- capture hook (§Design 5: exact site, no-op body by construction) -------------------

    /// <summary>
    /// The capture-transfer hook (spec-sector-storage.md §Design 5). Called from
    /// <c>DiffSectors</c>'s existing per-sector loop, inside the same turn-commit
    /// transaction as every other turn-commit write — never a second, later transaction.
    ///
    /// <para>The body is a no-op today, and that is not a dodge: because
    /// <c>rpg_world_sector_storage</c> carries no owner/faction column at all, the moment
    /// <c>ClaimResolver</c> reassigns <c>sector.OwnerFactionId</c> and that write lands in
    /// the same <c>tx</c>, every row already in that sector's storage becomes reachable to
    /// the new owner and unreachable to the old one, automatically, with nothing left to
    /// migrate. A literal <c>rpg_item.player_id</c>-rooted transfer only becomes a
    /// meaningful operation once a genuine second empire/player row exists (spec §Design
    /// 5's named real gap — there is only one real <c>player_id</c> in any world today),
    /// and its write belongs here, guarded on the new owner resolving to a *different*
    /// real <c>player_id</c> than the old one.</para>
    /// </summary>
    internal static void OnSectorCapturedUnlocked(
        string worldId, string sectorId, string? oldOwnerFactionId, string? newOwnerFactionId)
    {
        Debug.Assert(!string.Equals(oldOwnerFactionId, newOwnerFactionId, StringComparison.Ordinal),
            $"sector-storage capture hook fired without an owner change for sector '{sectorId}' " +
            $"in world '{worldId}' — the caller must guard on was.OwnerFactionId != s.OwnerFactionId.");
        TestProbeSectorCapture?.Invoke();
    }
}
