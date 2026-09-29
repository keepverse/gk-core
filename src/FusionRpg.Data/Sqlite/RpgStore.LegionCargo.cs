using FusionRpg.Core.World.LegionCargo;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>
/// One cargo row aboard a legion — the <c>rpg_world_entity_cargo</c> overlay
/// (spec-legion-cargo.md §Design 2). Reachability + capacity only: never a second ownership root,
/// so there is no <c>player_id</c> column of its own.
/// </summary>
public sealed record LegionCargoRow(
    string WorldId,
    string EntityId,
    int Seq,
    string Kind,
    string? InstanceId,
    string? ContainerId,
    long? Qty,
    long WeightEach);

/// <summary>
/// Legion cargo — the slot+weight carry overlay per <c>WorldEntity</c>
/// (spec-legion-cargo.md). SQL lives here, only here (guard-dal).
///
/// <para>Capacity is <b>every</b> member regardless of role
/// (<c>memberCount × tuning</c>), a deliberate divergence from <c>LegionSupply</c>'s Bearer-only
/// loam-carry precedent (spec Locked anchors, owner-confirmed) — <see cref="MemberCountUnlocked"/>
/// counts with no role filter, and the regression suite proves it.</para>
///
/// <para>Weight is a snapshot, never a live join (spec §Design 4): <c>weight_each</c> is captured
/// once at load time from the caller-supplied derived weight and never re-resolved on read. No
/// per-item weight column exists anywhere in code today (verified 2026-09-15: the only weight-like
/// fields are delve pack-footprint mass steps, an unrelated mechanic), so the load verbs take
/// <c>weightEach</c> as an explicit parameter — the snapshot discipline holds regardless of where
/// the number came from, and a future per-species stat only changes the caller, never this table.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>
    /// The marker <see cref="RpgItemRow.Disposition"/> carries while its instance is aboard a
    /// legion. This is the "whatever 'aboard entity E' marker kept the <c>rpg_item</c> row out of
    /// armoury listings" spec-legion-cargo.md §Design 3's own checklist names as implementation's
    /// first task: <c>ListItemsByPlayer</c> already filters <c>disposition = 'owned'</c>, so a loaded
    /// item drops out of the armoury with zero changes to existing readers, and unload restores
    /// <c>'owned'</c>. <c>player_id</c> itself is never touched — ownership never moves, only
    /// reachability. The orphan sweep keys off <c>rpg_item</c> existence regardless of disposition,
    /// so a cargo-marked row is still owned, never swept; the salvage gate keys off
    /// <c>disposition = 'owned'</c>, so an aboard item correctly refuses salvage while loaded.
    /// </summary>
    public const string CargoAboardDisposition = "cargo";

    /// <summary>
    /// Test probe (approach.test_probe_over_impossible): when non-null, invoked inside the
    /// load/unload/transfer transactions after the first destructive write but before the second
    /// write. Lets the atomicity suite force a mid-transaction failure through the real code path —
    /// a crash between the delete and the insert must leave the pre-transfer state fully intact.
    /// Production never sets this; it defaults to null (no-op).
    /// </summary>
    internal static Action? TestProbeMidWrite;

    void EnsureLegionCargoSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_world_entity_cargo (
              world_id     TEXT NOT NULL,
              entity_id    TEXT NOT NULL,
              seq          INTEGER NOT NULL,
              kind         TEXT NOT NULL,
              instance_id  TEXT,
              container_id TEXT,
              qty          INTEGER,
              weight_each  INTEGER NOT NULL,
              PRIMARY KEY (world_id, entity_id, seq),
              FOREIGN KEY (world_id, entity_id) REFERENCES rpg_world_entities(world_id, entity_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_world_entity_cargo_instance
              ON rpg_world_entity_cargo(instance_id);
            """);
    }

    // ---- capacity: computed fresh, never cached (spec §Design 1) -------------------------------

    /// <summary>
    /// Live member count — <b>every</b> member regardless of role (no <c>role</c> filter), the
    /// deliberate divergence from <c>LegionSupply.BearerCount</c> the spec's Locked anchors record.
    /// Read live off <c>rpg_world_entity_members</c> so a member joining or dying changes capacity
    /// with no second write.
    /// </summary>
    internal int MemberCountUnlocked(SqliteConnection db, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM rpg_world_entity_members WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Weight capacity for a legion, computed fresh (spec §Design 1).</summary>
    internal long WeightCapacityUnlocked(SqliteConnection db, string worldId, string entityId) =>
        ScopedInventoryPolicy.WeightCapacityFor(MemberCountUnlocked(db, worldId, entityId));

    /// <summary>Slot capacity for a legion, computed fresh (spec §Design 1).</summary>
    internal int SlotCapacityUnlocked(SqliteConnection db, string worldId, string entityId) =>
        ScopedInventoryPolicy.SlotCapacityFor(MemberCountUnlocked(db, worldId, entityId));

    public long LegionWeightCapacity(string worldId, string entityId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return WeightCapacityUnlocked(db, worldId, entityId);
        }
    }

    public int LegionSlotCapacity(string worldId, string entityId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return SlotCapacityUnlocked(db, worldId, entityId);
        }
    }

    // ---- reads ---------------------------------------------------------------------------------

    internal LegionCargoRow? ReadCargoRowUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId, int seq)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each
            FROM rpg_world_entity_cargo WHERE world_id = $w AND entity_id = $e AND seq = $s;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        cmd.Parameters.AddWithValue("$s", seq);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCargoRow(r) : null;
    }

    static LegionCargoRow ReadCargoRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt64(6),
        r.GetInt64(7));

    public IReadOnlyList<LegionCargoRow> ListCargo(string worldId, string entityId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            return ListCargoUnlocked(db, tx, worldId, entityId);
        }
    }

    internal List<LegionCargoRow> ListCargoUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each
            FROM rpg_world_entity_cargo WHERE world_id = $w AND entity_id = $e ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        using var r = cmd.ExecuteReader();
        var list = new List<LegionCargoRow>();
        while (r.Read()) list.Add(ReadCargoRow(r));
        return list;
    }

    /// <summary>
    /// Current weight aboard — sums the stored <c>weight_each</c> snapshot column, never
    /// re-resolving any base-type weight (spec §Design 4). Summed in C# under <c>checked</c>:
    /// overflow throws, never wraps.
    /// </summary>
    internal long WeightUsedUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        var total = 0L;
        foreach (var row in ListCargoUnlocked(db, tx, worldId, entityId))
            total = checked(total + RowWeight(row));
        return total;
    }

    /// <summary>One row occupies one slot, instance or stack alike.</summary>
    internal int SlotsUsedUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM rpg_world_entity_cargo WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    static long RowWeight(LegionCargoRow row) =>
        row.Kind == "stack" ? checked((row.Qty ?? 0) * row.WeightEach) : row.WeightEach;

    internal int NextSeqUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COALESCE(MAX(seq), -1) + 1 FROM rpg_world_entity_cargo
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    internal string? ReadEntityOwnerUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT owner_faction_id FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? null : (string)v;
    }

    internal long? ReadWorldPlayerUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT player_id FROM rpg_worlds WHERE world_id = $w;";
        cmd.Parameters.AddWithValue("$w", worldId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? null : Convert.ToInt64(v);
    }

    // ---- load: the empire-stash boundary (spec §Design 3) --------------------------------------

    /// <summary>
    /// Loads an armoury-resident item (<c>kind='instance'</c>, <paramref name="instanceId"/>) or a
    /// fungible stock quantity (<c>kind='stack'</c>, <paramref name="containerId"/> ×
    /// <paramref name="qty"/>) aboard a legion. Every refusal fires before any write — never a
    /// partial load. <paramref name="weightEach"/> is the item's derived weight, captured once into
    /// the row's snapshot column (spec §Design 4).
    /// </summary>
    public (bool Ok, string Reason) LoadCargo(
        string worldId, string entityId, long playerId,
        string kind, string? instanceId, string? containerId, long qty, long weightEach)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = LoadCargoUnlocked(db, tx, worldId, entityId, playerId,
                kind, instanceId, containerId, qty, weightEach);
            if (result.Ok) tx.Commit();
            return result;
        }
    }

    internal (bool Ok, string Reason) LoadCargoUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, string entityId, long playerId,
        string kind, string? instanceId, string? containerId, long qty, long weightEach)
    {
        if (kind != "instance" && kind != "stack")
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "cargo kind must be 'instance' or 'stack'");
        if (weightEach < 0)
            throw new ArgumentOutOfRangeException(nameof(weightEach), "cargo weight_each must be non-negative");
        if (kind == "instance" && string.IsNullOrEmpty(instanceId))
            throw new ArgumentException("instance loads require an instance_id", nameof(instanceId));
        if (kind == "stack" && (string.IsNullOrEmpty(containerId) || qty <= 0))
            throw new ArgumentException("stack loads require a container_id and a positive qty", nameof(containerId));

        if (ReadEntityOwnerUnlocked(db, tx, worldId, entityId) is null)
            return (false, "cargo.not-owned");
        if (ReadWorldPlayerUnlocked(db, tx, worldId) != playerId)
            return (false, "cargo.not-owned");

        var addedWeight = kind == "stack" ? checked(qty * weightEach) : weightEach;

        if (kind == "instance")
        {
            if (!IsArmouryResidentUnlocked(db, tx, instanceId!, playerId.ToString()))
                return (false, "cargo.not-owned");
        }
        else
        {
            if (ReadStockQtyUnlocked(db, tx, playerId.ToString(), containerId!) < qty)
                return (false, "cargo.not-owned");
        }

        var weightUsed = WeightUsedUnlocked(db, tx, worldId, entityId);
        if (checked(weightUsed + addedWeight) > WeightCapacityUnlocked(db, worldId, entityId))
            return (false, "cargo.over-weight");
        if (SlotsUsedUnlocked(db, tx, worldId, entityId) + 1 > SlotCapacityUnlocked(db, worldId, entityId))
            return (false, "cargo.no-slots");

        if (kind == "instance")
        {
            var moved = ExecInCounted(db, tx, """
                UPDATE rpg_item SET disposition = $disp, revision = revision + 1
                WHERE instance_id = $id AND disposition = 'owned';
                """,
                ("$disp", CargoAboardDisposition), ("$id", instanceId!));
            if (moved != 1)
                return (false, "cargo.not-owned");

            TestProbeMidWrite?.Invoke();

            ExecInCounted(db, tx, """
                INSERT INTO rpg_world_entity_cargo
                  (world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each)
                VALUES ($w, $e, $s, 'instance', $id, NULL, NULL, $wt);
                """,
                ("$w", worldId), ("$e", entityId),
                ("$s", NextSeqUnlocked(db, tx, worldId, entityId)),
                ("$id", instanceId!), ("$wt", weightEach));
        }
        else
        {
            var moved = ExecInCounted(db, tx, """
                UPDATE rpg_item_stock SET qty = qty - $q, updated_utc = $utc
                WHERE player_id = $p AND container_id = $c AND qty >= $q;
                """,
                ("$q", qty), ("$utc", ServerClock.UtcNowDateTime.ToString("O")),
                ("$p", playerId.ToString()), ("$c", containerId!));
            if (moved != 1)
                return (false, "cargo.not-owned");

            TestProbeMidWrite?.Invoke();

            ExecInCounted(db, tx, """
                INSERT INTO rpg_world_entity_cargo
                  (world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each)
                VALUES ($w, $e, $s, 'stack', NULL, $c, $q, $wt);
                """,
                ("$w", worldId), ("$e", entityId),
                ("$s", NextSeqUnlocked(db, tx, worldId, entityId)),
                ("$c", containerId!), ("$q", qty), ("$wt", weightEach));
        }

        return (true, "ok");
    }

    /// <summary>
    /// Armoury-resident: owned by this player, still <c>'owned'</c> (not aboard another legion, not
    /// salvaged/transferred/destroyed), equipped on no specimen and no commander. Any failure is
    /// <c>cargo.not-owned</c> — the item is not loadable from this player's armoury.
    /// </summary>
    internal bool IsArmouryResidentUnlocked(
        SqliteConnection db, SqliteTransaction tx, string instanceId, string playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT 1 FROM rpg_item WHERE instance_id = $id AND player_id = $p AND disposition = 'owned'
              AND NOT EXISTS (SELECT 1 FROM rpg_item_assignment WHERE ref_id = $id)
              AND NOT EXISTS (SELECT 1 FROM rpg_player_item_assignment WHERE ref_id = $id)
              AND NOT EXISTS (SELECT 1 FROM rpg_world_entity_cargo WHERE instance_id = $id);
            """;
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.Parameters.AddWithValue("$p", playerId);
        return cmd.ExecuteScalar() is not null;
    }

    internal long ReadStockQtyUnlocked(
        SqliteConnection db, SqliteTransaction tx, string playerId, string containerId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT qty FROM rpg_item_stock WHERE player_id = $p AND container_id = $c;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$c", containerId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? 0 : Convert.ToInt64(v);
    }

    // ---- unload: the exact reverse (spec §Design 3) --------------------------------------------

    /// <summary>
    /// Unloads one cargo row back to the armoury: the cargo row is deleted and the stash side is
    /// restored (instance disposition back to <c>'owned'</c>; stock quantity credited back) in the
    /// same transaction.
    /// </summary>
    public (bool Ok, string Reason) UnloadCargo(string worldId, string entityId, int seq, long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = UnloadCargoUnlocked(db, tx, worldId, entityId, seq, playerId);
            if (result.Ok) tx.Commit();
            return result;
        }
    }

    internal (bool Ok, string Reason) UnloadCargoUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId, int seq, long playerId)
    {
        var row = ReadCargoRowUnlocked(db, tx, worldId, entityId, seq);
        if (row is null)
            return (false, "cargo.not-found");
        if (ReadWorldPlayerUnlocked(db, tx, worldId) != playerId)
            return (false, "cargo.not-owned");

        ExecInCounted(db, tx, """
            DELETE FROM rpg_world_entity_cargo WHERE world_id = $w AND entity_id = $e AND seq = $s;
            """,
            ("$w", worldId), ("$e", entityId), ("$s", seq));

        TestProbeMidWrite?.Invoke();

        if (row.Kind == "instance")
        {
            // Best-effort restore: if the underlying instance row is gone (its effect_instance was
            // hard-deleted, cascading into rpg_item), the cargo row's deletion above is still the
            // whole of the unload — refuse nothing and strand nothing.
            ExecInCounted(db, tx, """
                UPDATE rpg_item SET disposition = 'owned', revision = revision + 1
                WHERE instance_id = $id AND disposition = $disp;
                """,
                ("$id", row.InstanceId!), ("$disp", CargoAboardDisposition));
        }
        else
        {
            ExecInCounted(db, tx, """
                INSERT INTO rpg_item_stock (player_id, container_id, qty, updated_utc)
                VALUES ($p, $c, $q, $utc)
                ON CONFLICT(player_id, container_id) DO UPDATE SET
                  qty = rpg_item_stock.qty + $q, updated_utc = excluded.updated_utc;
                """,
                ("$p", playerId.ToString()), ("$c", row.ContainerId!),
                ("$q", row.Qty ?? 0), ("$utc", ServerClock.UtcNowDateTime.ToString("O")));
        }

        return (true, "ok");
    }

    // ---- legion-to-legion transfer (spec §Design 5) --------------------------------------------

    /// <summary>
    /// Moves one cargo row from legion to legion, same empire only: delete from source, insert an
    /// equivalent row (new <c>seq</c>) under the destination — one transaction, move never copy.
    /// The destination's own capacity gate is checked before any write, same as
    /// <see cref="LoadCargoUnlocked"/>.
    /// </summary>
    public (bool Ok, string Reason, int NewSeq) TransferCargo(
        string worldId, string fromEntityId, string toEntityId, int seq, long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = TransferCargoUnlocked(db, tx, worldId, fromEntityId, toEntityId, seq, playerId);
            if (result.Ok) tx.Commit();
            return result;
        }
    }

    internal (bool Ok, string Reason, int NewSeq) TransferCargoUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, string fromEntityId, string toEntityId, int seq, long playerId)
    {
        if (string.Equals(fromEntityId, toEntityId, StringComparison.Ordinal))
        {
            // Same-legion "transfer" is a no-op: the row is already there. Succeed without writing
            // rather than churning its seq.
            var existing = ReadCargoRowUnlocked(db, tx, worldId, fromEntityId, seq);
            return existing is null ? (false, "cargo.not-found", -1) : (true, "ok", seq);
        }

        var row = ReadCargoRowUnlocked(db, tx, worldId, fromEntityId, seq);
        if (row is null)
            return (false, "cargo.not-found", -1);

        var fromOwner = ReadEntityOwnerUnlocked(db, tx, worldId, fromEntityId);
        var toOwner = ReadEntityOwnerUnlocked(db, tx, worldId, toEntityId);
        if (fromOwner is null || toOwner is null)
            return (false, "cargo.not-found", -1);
        if (!string.Equals(fromOwner, toOwner, StringComparison.Ordinal))
            return (false, "cargo.cross-empire", -1);
        if (ReadWorldPlayerUnlocked(db, tx, worldId) != playerId)
            return (false, "cargo.cross-empire", -1);

        var rowWeight = RowWeight(row);
        var destWeightUsed = WeightUsedUnlocked(db, tx, worldId, toEntityId);
        if (checked(destWeightUsed + rowWeight) > WeightCapacityUnlocked(db, worldId, toEntityId))
            return (false, "cargo.over-weight", -1);
        if (SlotsUsedUnlocked(db, tx, worldId, toEntityId) + 1 > SlotCapacityUnlocked(db, worldId, toEntityId))
            return (false, "cargo.no-slots", -1);

        ExecInCounted(db, tx, """
            DELETE FROM rpg_world_entity_cargo WHERE world_id = $w AND entity_id = $e AND seq = $s;
            """,
            ("$w", worldId), ("$e", fromEntityId), ("$s", seq));

        TestProbeMidWrite?.Invoke();

        var newSeq = NextSeqUnlocked(db, tx, worldId, toEntityId);
        ExecInCounted(db, tx, """
            INSERT INTO rpg_world_entity_cargo
              (world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each)
            VALUES ($w, $e, $s, $k, $iid, $cid, $q, $wt);
            """,
            ("$w", worldId), ("$e", toEntityId), ("$s", newSeq), ("$k", row.Kind),
            ("$iid", (object?)row.InstanceId ?? DBNull.Value),
            ("$cid", (object?)row.ContainerId ?? DBNull.Value),
            ("$q", (object?)row.Qty ?? DBNull.Value), ("$wt", row.WeightEach));

        return (true, "ok", newSeq);
    }

    // ---- small write helpers (same shape as RpgStore.World.cs's Insert) -------------------------

    static int ExecInCounted(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }
}
