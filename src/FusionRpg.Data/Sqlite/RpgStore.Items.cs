using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One player's stock count of a fungible (unrolled) container (item-ideal.md, `armoury`, I13 §4.3).</summary>
public sealed record RpgItemStockRow(string PlayerId, string ContainerId, int Qty, string UpdatedUtc);

/// <summary>An auto-salvage/hide rule over the same predicate encoding definitions.md §3 uses everywhere else.</summary>
public sealed record RpgItemRuleRow(
    string RuleId, string PlayerId, string Action, string PredicateJson, bool Enabled, string CreatedUtc);

/// <summary>One armoury event — what makes "where did my item go" answerable (I13 §4.7).</summary>
public sealed record RpgItemEventRow(
    string EventId, string InstanceId, string PlayerId, string Kind, string? Detail, string CreatedUtc);

/// <summary>One player-owned assignment. Commander equipment uses this scope instead of pretending
/// that the commander is a persistent unique specimen.</summary>
public sealed record RpgPlayerItemAssignmentRow(
    string PlayerId, FusionRpg.Core.Items.ItemRole Role, string RefKind, string RefId, string AssignedUtc);

/// <summary>A saved loadout — the library `armoury` claims beside its own store (I13 §3.5/§4.5).</summary>
public sealed record RpgItemLoadoutRow(
    string LoadoutId, string PlayerId, string Name, string? Frame, string CreatedUtc, long Revision);

/// <summary>One role's entry in a loadout. <c>RefKind</c> is <c>"item"</c> (an <c>rpg_item.instance_id</c>)
/// or <c>"stock"</c> (a <c>container_id</c>) — a stock-backed preset entry never pins one specific copy.</summary>
public sealed record RpgItemLoadoutEntryRow(string LoadoutId, string Role, string RefKind, string RefId);

/// <summary>
/// <c>rpg_item</c> — durable ownership over an <c>effect_instance</c> (item-ideal.md §2e C3/S2,
/// durable-ownership module 1).
///
/// <para><b>The second reachability root.</b> A binding says "equipped"; this says "owned". Before
/// this table existed the orphan sweep's only reachability root was a binding, so unequipping an item
/// made it unreachable and the sweep deleted it — a live data-loss defect
/// (<c>RpgStore.AtomInstances.cs</c>'s <c>CollectOrphanInstancesUnlocked</c>). An instance is now
/// collected only when it has <b>neither</b> a binding nor an owner.</para>
///
/// <para><b>Ownership is policy, not content.</b> <c>effect_instance</c>'s contract is content-derived
/// reproducibility (<see cref="FusionRpg.Core.Effects.Atoms.InstanceRow.ContentFingerprint"/>) — adding
/// <c>player_id</c> there would fold player state into a byte-identity comparison that must never see
/// it. This table carries everything an effect instance should not: who owns it, when, and what they
/// have done with it. <b>No rolled value is ever duplicated here</b> — rolls live in the instance.</para>
/// </summary>
public sealed record RpgItemRow
{
    /// <summary>PK, 1:1 with <c>effect_instance.instance_id</c>. No second identity.</summary>
    public string InstanceId { get; init; } = "";

    public string PlayerId { get; init; } = "";
    public string AcquiredUtc { get; init; } = "";

    /// <summary>Free text — "drop", "craft", "grant", "migration". Not a closed vocabulary here; the
    /// instance's own <c>Origin</c> already is one, and this is a support/provenance note beside it.</summary>
    public string OriginKind { get; init; } = "drop";

    public string? OriginRef { get; init; }

    /// <summary>Player-set: refuse salvage/transfer while true. Never enforced by this row alone —
    /// callers that spend an item check it.</summary>
    public bool Locked { get; init; }

    public bool Seen { get; init; }

    /// <summary>D9/D32: true once a content edit has changed an atom this item carries. Advisory —
    /// resolution itself judges compatibility per atom (<c>ResolveBindings</c>); this is a support
    /// flag so an owner can be told "this item may look different than when you found it".</summary>
    public bool Stale { get; init; }

    /// <summary>"owned" | "salvaged" | "transferred" | "destroyed" — deleting the underlying instance
    /// is always a disposition, never a side effect of another operation.</summary>
    public string Disposition { get; init; } = "owned";

    public string? Note { get; init; }

    public long Revision { get; init; }
}

public sealed partial class RpgStore
{
    void EnsureRpgItemSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_item (
              instance_id TEXT NOT NULL PRIMARY KEY,
              player_id TEXT NOT NULL,
              acquired_utc TEXT NOT NULL,
              origin_kind TEXT NOT NULL DEFAULT 'drop',
              origin_ref TEXT,
              locked INTEGER NOT NULL DEFAULT 0,
              seen INTEGER NOT NULL DEFAULT 0,
              stale INTEGER NOT NULL DEFAULT 0,
              disposition TEXT NOT NULL DEFAULT 'owned',
              note TEXT,
              revision INTEGER NOT NULL DEFAULT 0,
              FOREIGN KEY (instance_id) REFERENCES effect_instance(instance_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_item_player ON rpg_item(player_id);

            CREATE TABLE IF NOT EXISTS rpg_item_stock (
              player_id TEXT NOT NULL,
              container_id TEXT NOT NULL,
              qty INTEGER NOT NULL DEFAULT 0,
              updated_utc TEXT NOT NULL,
              PRIMARY KEY (player_id, container_id)
            );

            CREATE TABLE IF NOT EXISTS rpg_item_rule (
              rule_id TEXT NOT NULL PRIMARY KEY,
              player_id TEXT NOT NULL,
              action TEXT NOT NULL,
              predicate_json TEXT NOT NULL DEFAULT '{}',
              enabled INTEGER NOT NULL DEFAULT 1,
              created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_item_rule_player ON rpg_item_rule(player_id);

            CREATE TABLE IF NOT EXISTS rpg_item_event (
              event_id TEXT NOT NULL PRIMARY KEY,
              instance_id TEXT NOT NULL,
              player_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              detail TEXT,
              created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_item_event_instance ON rpg_item_event(instance_id);
            CREATE INDEX IF NOT EXISTS ix_rpg_item_event_player ON rpg_item_event(player_id);

            CREATE TABLE IF NOT EXISTS rpg_item_loadout (
              loadout_id TEXT NOT NULL PRIMARY KEY,
              player_id TEXT NOT NULL,
              name TEXT NOT NULL,
              frame TEXT,
              created_utc TEXT NOT NULL,
              revision INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_item_loadout_player ON rpg_item_loadout(player_id);

            CREATE TABLE IF NOT EXISTS rpg_item_loadout_entry (
              loadout_id TEXT NOT NULL,
              role TEXT NOT NULL,
              ref_kind TEXT NOT NULL,
              ref_id TEXT NOT NULL,
              PRIMARY KEY (loadout_id, role)
            );

            -- item-ideal.md, equip-assign (module 4). The durable half of equipping -- "this player
            -- put this item in this role on this specimen". The runtime binding (effect_binding, E6)
            -- is rebuilt from this as a full projection at deploy, never patched; deleting this row
            -- is the whole of "unequip", one row, no second writer.
            CREATE TABLE IF NOT EXISTS rpg_item_assignment (
              specimen_id TEXT NOT NULL,
              role TEXT NOT NULL,
              ref_kind TEXT NOT NULL,
              ref_id TEXT NOT NULL,
              assigned_utc TEXT NOT NULL,
              PRIMARY KEY (specimen_id, role)
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_item_assignment_ref ON rpg_item_assignment(ref_kind, ref_id);

            -- Commander-owned equipment is keyed by player, not by a fabricated specimen id. Keep it
            -- separate from the unique-actor assignment table so the two ownership scopes cannot bleed.
            CREATE TABLE IF NOT EXISTS rpg_player_item_assignment (
              player_id TEXT NOT NULL,
              role TEXT NOT NULL,
              ref_kind TEXT NOT NULL,
              ref_id TEXT NOT NULL,
              assigned_utc TEXT NOT NULL,
              PRIMARY KEY (player_id, role)
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_player_item_assignment_ref
              ON rpg_player_item_assignment(ref_kind, ref_id);

            -- item-ideal.md, slot-roles (module 3). Mirrors core.v1.json's roles.list -- a normalized,
            -- SQL-joinable copy of the same fifteen rows ItemRoleRegistry parses in Core, never a
            -- second source of truth: SeedRoles below reseeds from the same registry JSON on every
            -- call, so an edited (registryVersion-bumped) registry always wins.
            CREATE TABLE IF NOT EXISTS item_role (
              role_id TEXT NOT NULL PRIMARY KEY,
              humanoid_name TEXT NOT NULL,
              plant_name TEXT NOT NULL,
              hybrid_eligible INTEGER NOT NULL,
              budget_weight_milli INTEGER NOT NULL
            );

            -- Role x frame legality -- static and fully registry-derived (humanoid/plant host all
            -- fifteen; hybrid hosts only the twelve with hybrid_eligible=1). NOT the per-actor
            -- species -> frame lookup X1 supplies later -- that is a different table, keyed on
            -- species, that this module does not own.
            CREATE TABLE IF NOT EXISTS item_role_frame (
              role_id TEXT NOT NULL,
              frame TEXT NOT NULL,
              legal INTEGER NOT NULL,
              PRIMARY KEY (role_id, frame)
            );
            """);
    }

    /// <summary>
    /// (Re)seed <c>item_role</c>/<c>item_role_frame</c> from the registry's own JSON — never from a
    /// hand-transcribed C# literal. Idempotent: safe to call on every boot, and a
    /// <c>registryVersion</c> bump is picked up the next time this runs.
    /// </summary>
    public void SeedRoles(string coreRegistryJson)
    {
        var defs = FusionRpg.Core.Items.ItemRoleRegistry.Parse(coreRegistryJson);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            ExecIn(db, tx, "DELETE FROM item_role;");
            ExecIn(db, tx, "DELETE FROM item_role_frame;");

            foreach (var d in defs)
            {
                var roleId = FusionRpg.Core.Items.ItemRoles.Id(d.Role);
                ExecIn(db, tx, """
                    INSERT INTO item_role (role_id, humanoid_name, plant_name, hybrid_eligible, budget_weight_milli)
                    VALUES ($id, $h, $p, $he, $w);
                    """,
                    ("$id", roleId), ("$h", d.HumanoidName), ("$p", d.PlantName),
                    ("$he", d.HybridEligible ? 1 : 0), ("$w", d.BudgetWeightMilli));

                foreach (var frame in new[] { "humanoid", "plant" })
                    ExecIn(db, tx, "INSERT INTO item_role_frame (role_id, frame, legal) VALUES ($id, $f, 1);",
                        ("$id", roleId), ("$f", frame));

                ExecIn(db, tx, "INSERT INTO item_role_frame (role_id, frame, legal) VALUES ($id, 'hybrid', $legal);",
                    ("$id", roleId), ("$legal", d.HybridEligible ? 1 : 0));
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<(string RoleId, string HumanoidName, string PlantName, bool HybridEligible, int BudgetWeightMilli)> ListRoles()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT role_id, humanoid_name, plant_name, hybrid_eligible, budget_weight_milli FROM item_role ORDER BY role_id;";
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string, string, bool, int)>();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetInt32(4)));
            return list;
        }
    }

    public bool IsRoleLegalForFrame(string roleId, string frame)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT legal FROM item_role_frame WHERE role_id = $r AND frame = $f;";
            cmd.Parameters.AddWithValue("$r", roleId);
            cmd.Parameters.AddWithValue("$f", frame);
            var v = cmd.ExecuteScalar();
            return v is not null && Convert.ToInt64(v) != 0;
        }
    }

    /// <summary>
    /// The abuse guard on total armoury row count — a structural bug guard, never a progression
    /// ceiling (AGENTS.md, ssot-inventory.md §3.2). D26 forbids any capacity number that regulates
    /// drop volume or content pacing; this exists only to stop a runaway loop (a bug, not a player)
    /// from writing unbounded rows. <see cref="AcquireItem"/> is the only enforcement point — nothing
    /// else in this file or in <c>gk-core/src/FusionRpg.Core/Items/</c> may declare a second one
    /// (`NoCapacityCapExistsOutsideTheNamedAbuseGuard`, ArmouryTests.cs).
    /// </summary>
    public const int InventoryCeiling = 20_000;

    /// <summary>
    /// Acquire a rolled item into a player's armoury: owns it (module 1's <see cref="SaveItem"/>) and
    /// records the <c>acquired</c> event in one transaction — the two writes I13 §4.7 needs to answer
    /// "where did my item come from" for every row, not just the ones a caller remembered to log.
    /// </summary>
    public AtomRejection AcquireItem(RpgItemRow item, string? eventId = null, string? createdUtc = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var rejection = AcquireItemUnlocked(db, tx, item, eventId, createdUtc);
            if (!rejection.IsOk) return rejection;
            tx.Commit();
            return rejection;
        }
    }

    /// <summary>Same two writes on the caller's connection/transaction — `delve-quests` D4.12/D4.14's
    /// own named gap: a quest reward's item must be acquired in the SAME transaction `CloseDelve`
    /// commits. The armoury-row count reads through the SAME `(db, tx)`, never a second connection —
    /// a real, reproduced bug this task's own two-quest-reward test caught the hard way: a self-locked
    /// second connection's plain `SELECT` is fine for a LONE call (reading before the caller's own
    /// write starts), but this method is called MULTIPLE TIMES in the SAME transaction by a caller
    /// banking more than one item at once (`ApplyQuestRewardBankingUnlocked`'s own multi-quest loop),
    /// and the SECOND call's read lands AFTER the FIRST call's own write to `rpg_item` — a second
    /// connection then collides with the still-open write (`SQLite Error 6: database table is locked`).
    /// See <see cref="AppendMutationOpUnlocked"/> for the established "why" this whole `*Unlocked`
    /// family shares.</summary>
    internal AtomRejection AcquireItemUnlocked(
        SqliteConnection db, SqliteTransaction tx, RpgItemRow item, string? eventId = null, string? createdUtc = null)
    {
        var count = CountArmouryRowsUnlocked(db, tx, item.PlayerId);
        if (count >= InventoryCeiling)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"player '{item.PlayerId}' is at the {InventoryCeiling}-row abuse guard, not a content limit");

        SaveItemUnlocked(db, tx, item);
        SaveItemEventUnlocked(db, new RpgItemEventRow(
            eventId ?? Guid.NewGuid().ToString("N"), item.InstanceId, item.PlayerId, "acquired", null,
            createdUtc ?? ServerClock.UtcNowDateTime.ToString("O")));

        return AtomRejection.Ok;
    }

    int CountArmouryRows(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            return CountArmouryRowsUnlocked(db, tx, playerId);
        }
    }

    static int CountArmouryRowsUnlocked(SqliteConnection db, SqliteTransaction tx, string playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_item WHERE player_id = $player;";
        cmd.Parameters.AddWithValue("$player", playerId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ---- rpg_item_stock ---------------------------------------------------------------------------

    /// <summary>Add (or, if negative, remove) to a player's stock count of a fungible container.</summary>
    public void AdjustStock(string playerId, string containerId, int delta, string? updatedUtc = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            AdjustStockUnlocked(db, playerId, containerId, delta, updatedUtc ?? ServerClock.UtcNowDateTime.ToString("O"));
            tx.Commit();
        }
    }

    /// <summary>Same write on the caller's connection, so consuming an insert commits with the socket
    /// write that used it. See <see cref="AppendMutationOpUnlocked"/>.</summary>
    internal void AdjustStockUnlocked(
        SqliteConnection db, string playerId, string containerId, int delta, string updatedUtc)
    {
        ExecOn(db, """
            INSERT INTO rpg_item_stock (player_id, container_id, qty, updated_utc)
            VALUES ($p, $c, MAX(0, $d), $utc)
            ON CONFLICT(player_id, container_id) DO UPDATE SET
              qty = MAX(0, rpg_item_stock.qty + $d), updated_utc = excluded.updated_utc;
            """,
            ("$p", playerId), ("$c", containerId), ("$d", delta), ("$utc", updatedUtc));
    }

    public IReadOnlyList<RpgItemStockRow> ListStock(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT player_id, container_id, qty, updated_utc FROM rpg_item_stock WHERE player_id = $p;";
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<RpgItemStockRow>();
            while (r.Read()) list.Add(new RpgItemStockRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
            return list;
        }
    }

    // ---- rpg_item_event -----------------------------------------------------------------------------

    public void SaveItemEvent(RpgItemEventRow ev)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            SaveItemEventUnlocked(db, ev);
            tx.Commit();
        }
    }

    /// <summary>Same write on the caller's connection, so the provenance row lands in the same
    /// transaction as the disposition it records. See <see cref="AppendMutationOpUnlocked"/>.</summary>
    internal void SaveItemEventUnlocked(SqliteConnection db, RpgItemEventRow ev) =>
        ExecOn(db, """
            INSERT INTO rpg_item_event (event_id, instance_id, player_id, kind, detail, created_utc)
            VALUES ($id, $inst, $player, $kind, $detail, $utc);
            """,
            ("$id", ev.EventId), ("$inst", ev.InstanceId), ("$player", ev.PlayerId), ("$kind", ev.Kind),
            ("$detail", (object?)ev.Detail ?? DBNull.Value), ("$utc", ev.CreatedUtc));

    public IReadOnlyList<RpgItemEventRow> ListItemEvents(string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT event_id, instance_id, player_id, kind, detail, created_utc
                FROM rpg_item_event WHERE instance_id = $id ORDER BY created_utc;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            using var r = cmd.ExecuteReader();
            var list = new List<RpgItemEventRow>();
            while (r.Read())
                list.Add(new RpgItemEventRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
            return list;
        }
    }

    // ---- rpg_item_loadout / rpg_item_loadout_entry ---------------------------------------------------

    public void SaveLoadout(RpgItemLoadoutRow loadout, IReadOnlyList<RpgItemLoadoutEntryRow> entries)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            ExecIn(db, tx, """
                INSERT INTO rpg_item_loadout (loadout_id, player_id, name, frame, created_utc, revision)
                VALUES ($id, $player, $name, $frame, $utc, 1)
                ON CONFLICT(loadout_id) DO UPDATE SET
                  name = excluded.name, frame = excluded.frame, revision = rpg_item_loadout.revision + 1;
                """,
                ("$id", loadout.LoadoutId), ("$player", loadout.PlayerId), ("$name", loadout.Name),
                ("$frame", (object?)loadout.Frame ?? DBNull.Value), ("$utc", loadout.CreatedUtc));

            ExecIn(db, tx, "DELETE FROM rpg_item_loadout_entry WHERE loadout_id = $id;", ("$id", loadout.LoadoutId));
            foreach (var e in entries)
                ExecIn(db, tx, """
                    INSERT INTO rpg_item_loadout_entry (loadout_id, role, ref_kind, ref_id)
                    VALUES ($id, $role, $rk, $rid);
                    """,
                    ("$id", loadout.LoadoutId), ("$role", e.Role), ("$rk", e.RefKind), ("$rid", e.RefId));

            tx.Commit();
        }
    }

    public IReadOnlyList<RpgItemLoadoutEntryRow> GetLoadoutEntries(string loadoutId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT loadout_id, role, ref_kind, ref_id FROM rpg_item_loadout_entry WHERE loadout_id = $id;";
            cmd.Parameters.AddWithValue("$id", loadoutId);
            using var r = cmd.ExecuteReader();
            var list = new List<RpgItemLoadoutEntryRow>();
            while (r.Read()) list.Add(new RpgItemLoadoutEntryRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    /// <summary>
    /// The spec's validate-on-read: <i>"Entries validate on read, never silently drop. An entry whose
    /// item was salvaged returns with a <c>missing</c> marker, so the player sees the hole."</i>
    /// Every stored entry comes back — the marker is the output, never a shorter list.
    ///
    /// <para>An <c>"item"</c> entry resolves when this player still owns that instance; a
    /// <c>"stock"</c> entry resolves while the count is above zero, since a stock entry names a
    /// <c>container_id</c> and never pins one copy. An unrecognised <c>ref_kind</c> resolves to
    /// nothing and is reported <c>Missing</c> — an unreadable entry is a visible hole, not a silent
    /// pass.</para>
    ///
    /// <para>⚠ Ownership is the only liveness test available today: <c>rpg_item.disposition</c> ships
    /// with no writer (module 2's soft-delete/undo window is unbuilt), so gating on a disposition
    /// vocabulary here would invent one. When that lands, this predicate is where it plugs in.</para>
    /// </summary>
    public IReadOnlyList<FusionRpg.Core.Items.LoadoutEntryStatus> GetLoadoutEntriesValidated(
        string loadoutId, string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT e.role, e.ref_kind, e.ref_id,
                       CASE e.ref_kind
                         WHEN 'item'  THEN (SELECT COUNT(*) FROM rpg_item i
                                             WHERE i.instance_id = e.ref_id AND i.player_id = $p)
                         WHEN 'stock' THEN (SELECT COALESCE(MAX(s.qty), 0) FROM rpg_item_stock s
                                             WHERE s.player_id = $p AND s.container_id = e.ref_id)
                         ELSE 0
                       END AS resolves
                FROM rpg_item_loadout_entry e
                WHERE e.loadout_id = $id
                ORDER BY e.role;
                """;
            cmd.Parameters.AddWithValue("$id", loadoutId);
            cmd.Parameters.AddWithValue("$p", playerId);

            using var r = cmd.ExecuteReader();
            var list = new List<FusionRpg.Core.Items.LoadoutEntryStatus>();
            while (r.Read())
                list.Add(new FusionRpg.Core.Items.LoadoutEntryStatus(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.GetInt64(3) > 0
                        ? FusionRpg.Core.Items.LoadoutEntryState.Present
                        : FusionRpg.Core.Items.LoadoutEntryState.Missing));
            return list;
        }
    }

    /// <summary>Where each instance-pinned reference currently sits, so
    /// <see cref="FusionRpg.Core.Items.LoadoutReport.Plan"/> can name the cell rather than a count.
    /// Served by <c>ix_rpg_item_assignment_ref</c>.
    ///
    /// <para>The role comes back as the stored string rather than through
    /// <c>ItemRoles.TryParse</c> on purpose: <c>ListAssignments</c> skips a row whose role it cannot
    /// parse, which is right for projecting bindings and wrong here — a holder we failed to parse is
    /// still holding the item, and dropping it would report "free" for a copy that is worn.</para>
    ///
    /// <para>⛔ <b>The default was <c>LoadoutReport.InstanceRefKind</c> ("item") until 2026-09-06
    /// (defect R2), and that is the wrong table's vocabulary.</b> This query reads
    /// <c>rpg_item_assignment</c>, whose instance-pinned kind is
    /// <see cref="FusionRpg.Core.Items.EquipRefKinds.Rolled"/>; <c>"item"</c> belongs to
    /// <c>rpg_item_loadout_entry</c>, which is what the caller passes IN and what
    /// <see cref="FusionRpg.Core.Items.LoadoutReport.Plan"/> filters on. Same instance ids, two
    /// different columns — so the old default silently reported every worn copy as free.</para>
    /// </summary>
    public IReadOnlyDictionary<string, FusionRpg.Core.Items.LoadoutCell> FindAssignmentHolders(
        IReadOnlyCollection<string> refIds, string refKind = FusionRpg.Core.Items.EquipRefKinds.Rolled)
    {
        var held = new Dictionary<string, FusionRpg.Core.Items.LoadoutCell>(StringComparer.Ordinal);
        if (refIds.Count == 0) return held;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            foreach (var refId in refIds.Distinct(StringComparer.Ordinal))
            {
                // Both ownership scopes, in one answer: since 2026-09-16 the commander equips through the same gate as a
                // specimen, so a copy worn in the commander's pouch must still read as worn when a specimen reaches for it.
                // Querying only `rpg_item_assignment` would let one physical copy fill two cells across the two tables.
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT specimen_id, role FROM rpg_item_assignment
                    WHERE ref_kind = $rk AND ref_id = $rid
                    UNION ALL
                    SELECT $commander, role FROM rpg_player_item_assignment
                    WHERE ref_kind = $rk AND ref_id = $rid
                    ORDER BY 1, 2 LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$rk", refKind);
                cmd.Parameters.AddWithValue("$rid", refId);
                cmd.Parameters.AddWithValue("$commander",
                    HumanDefaultCommanderStableId());
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    held[refId] = new FusionRpg.Core.Items.LoadoutCell(r.GetString(0), r.GetString(1));
            }
        }
        return held;
    }

    public IReadOnlyList<RpgItemLoadoutRow> ListLoadouts(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT loadout_id, player_id, name, frame, created_utc, revision FROM rpg_item_loadout WHERE player_id = $p;";
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<RpgItemLoadoutRow>();
            while (r.Read())
                list.Add(new RpgItemLoadoutRow(r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt64(5)));
            return list;
        }
    }

    /// <summary>
    /// build-preset BP1.6 (spec-item-loadout-apply.md): the store had no delete -- the route row
    /// named DELETE but nothing backed it. Owner-checked, one transaction over both tables, so a
    /// concurrent reader never observes a loadout row with no entries or entries with no loadout.
    /// </summary>
    public bool DeleteLoadout(string playerId, string loadoutId)
    {
        if (string.IsNullOrWhiteSpace(loadoutId)) return false;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            string? owner;
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT player_id FROM rpg_item_loadout WHERE loadout_id = $id;";
                cmd.Parameters.AddWithValue("$id", loadoutId);
                owner = cmd.ExecuteScalar() as string;
            }
            if (owner is null || !string.Equals(owner, playerId, StringComparison.Ordinal)) return false;

            ExecIn(db, tx, "DELETE FROM rpg_item_loadout_entry WHERE loadout_id = $id;", ("$id", loadoutId));
            ExecIn(db, tx, "DELETE FROM rpg_item_loadout WHERE loadout_id = $id AND player_id = $p;",
                ("$id", loadoutId), ("$p", playerId));
            tx.Commit();
            return true;
        }
    }

    /// <summary>Create or update an ownership row. Never touches <c>effect_instance</c> or its atoms.</summary>
    public void SaveItem(RpgItemRow item)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            SaveItemUnlocked(db, tx, item);
            tx.Commit();
        }
    }

    /// <summary>Same write on the caller's connection/transaction — `delve-quests` D4.12/D4.14's own
    /// named gap: banking a quest reward's item must land in the SAME transaction `CloseDelve`
    /// commits. See <see cref="AppendMutationOpUnlocked"/> for the established "why".</summary>
    internal void SaveItemUnlocked(SqliteConnection db, SqliteTransaction tx, RpgItemRow item) =>
        ExecIn(db, tx, """
            INSERT INTO rpg_item
              (instance_id, player_id, acquired_utc, origin_kind, origin_ref, locked, seen, stale,
               disposition, note, revision)
            VALUES ($id, $player, $utc, $ok, $oref, $locked, $seen, $stale, $disp, $note, 1)
            ON CONFLICT(instance_id) DO UPDATE SET
              player_id = excluded.player_id, origin_kind = excluded.origin_kind,
              origin_ref = excluded.origin_ref, locked = excluded.locked, seen = excluded.seen,
              stale = excluded.stale, disposition = excluded.disposition, note = excluded.note,
              revision = rpg_item.revision + 1;
            """,
            ("$id", item.InstanceId), ("$player", item.PlayerId), ("$utc", item.AcquiredUtc),
            ("$ok", item.OriginKind), ("$oref", (object?)item.OriginRef ?? DBNull.Value),
            ("$locked", item.Locked ? 1 : 0), ("$seen", item.Seen ? 1 : 0),
            ("$stale", item.Stale ? 1 : 0), ("$disp", item.Disposition),
            ("$note", (object?)item.Note ?? DBNull.Value));

    public RpgItemRow? GetItem(string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT instance_id, player_id, acquired_utc, origin_kind, origin_ref, locked, seen,
                       stale, disposition, note, revision
                FROM rpg_item WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return ReadItem(r);
        }
    }

    /// <summary>Every item a player still owns, regardless of whether it is currently equipped —
    /// "still owns" excludes a deliberate disposition (`salvaged`/`transferred`/`destroyed`), which is
    /// the whole point of the field (see <see cref="RpgItemRow.Disposition"/>). Found 2026-09-06: a
    /// salvaged item kept listing in the armoury because this method never filtered on it.</summary>
    public IReadOnlyList<RpgItemRow> ListItemsByPlayer(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT instance_id, player_id, acquired_utc, origin_kind, origin_ref, locked, seen,
                       stale, disposition, note, revision
                FROM rpg_item WHERE player_id = $player AND disposition = 'owned' ORDER BY acquired_utc;
                """;
            cmd.Parameters.AddWithValue("$player", playerId);
            using var r = cmd.ExecuteReader();

            var list = new List<RpgItemRow>();
            while (r.Read()) list.Add(ReadItem(r));
            return list;
        }
    }

    // ---- rpg_item_assignment (module 4, equip-assign) ------------------------------------------

    /// <summary>
    /// <b>Deploy-time snapshot, by construction — the anti-fraud gate</b>
    /// (`spec-corpse-cache.md` §Locked anchors, "Deploy-time snapshot, not death-time state").
    ///
    /// <para>Refuses an assignment write against a unique actor that is not in
    /// <see cref="UniqueActorPhases.Roster"/>, mirroring `RpgStore.Expeditions.cs:60-61`, which already
    /// refuses dispatch on a non-<c>Roster</c> actor for the identical reason. Without it, nothing
    /// stopped a player re-equipping a specimen that was already <c>Deploying</c>/<c>ActiveBound</c>/
    /// <c>Recovering</c> — so "the gear it died with" and "the gear it deployed with" could differ, and
    /// the corpse cache would bank items that were never actually risked.</para>
    ///
    /// <para>Gating here rather than at the endpoint is deliberate: this is the single write gate for
    /// the table, so every caller is covered by construction instead of each one remembering.</para>
    ///
    /// <para><b>A specimen with no unique-actor row is not gated, and that is not a hole.</b>
    /// `rpg_item_assignment` is also keyed by plain creature specimens, which have no phase at all —
    /// there is no deploy/death distinction to defend for them. The gate applies exactly where the
    /// concept exists.</para>
    /// </summary>
    void RequireRosterPhaseForAssignmentUnlocked(SqliteConnection db, string specimenId, string operation)
    {
        var actor = ReadUniqueActorUnlocked(db, specimenId);
        if (actor is null) return; // not a unique actor -- no phase concept, nothing to defend
        if (string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal)) return;

        throw new InvalidOperationException(
            $"{operation} refused: specimen '{specimenId}' is in phase '{actor.Phase}', not "
            + $"'{UniqueActorPhases.Roster}'. Gear is committed before a specimen deploys and cannot be "
            + "changed while it is out, so that what it died with is what it deployed with "
            + "(spec-corpse-cache.md §Locked anchors). Return it to Roster first.");
    }

    /// <summary>Assign — durable, upserted by `(specimen_id, role)`. One role, one occupant.
    /// Refuses a non-<c>Roster</c> unique actor — see
    /// <see cref="RequireRosterPhaseForAssignmentUnlocked"/>.</summary>
    public void SaveAssignment(string specimenId, FusionRpg.Core.Items.ItemRole role, string refKind, string refId,
        string? assignedUtc = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            RequireRosterPhaseForAssignmentUnlocked(db, specimenId, nameof(SaveAssignment));
            using var tx = db.BeginTransaction();
            ExecIn(db, tx, """
                INSERT INTO rpg_item_assignment (specimen_id, role, ref_kind, ref_id, assigned_utc)
                VALUES ($sid, $role, $rk, $rid, $utc)
                ON CONFLICT(specimen_id, role) DO UPDATE SET
                  ref_kind = excluded.ref_kind, ref_id = excluded.ref_id, assigned_utc = excluded.assigned_utc;
                """,
                ("$sid", specimenId), ("$role", FusionRpg.Core.Items.ItemRoles.Id(role)),
                ("$rk", refKind), ("$rid", refId), ("$utc", assignedUtc ?? ServerClock.UtcNowDateTime.ToString("O")));
            tx.Commit();
        }
    }

    /// <summary>Unequip: one row deleted, no second writer (§6.4's atomicity claim).
    /// Refuses a non-<c>Roster</c> unique actor for the same reason
    /// <see cref="SaveAssignment"/> does — stripping gear mid-deployment is the same fraud as swapping
    /// it. The death path does NOT come through here: `RpgStore.CorpseCache.cs:188` deletes the rows
    /// itself, inside the cache transaction, precisely so the move stays atomic.</summary>
    public bool RemoveAssignment(string specimenId, FusionRpg.Core.Items.ItemRole role)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            RequireRosterPhaseForAssignmentUnlocked(db, specimenId, nameof(RemoveAssignment));
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM rpg_item_assignment WHERE specimen_id = $sid AND role = $role;";
            cmd.Parameters.AddWithValue("$sid", specimenId);
            cmd.Parameters.AddWithValue("$role", FusionRpg.Core.Items.ItemRoles.Id(role));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<FusionRpg.Core.Items.EquipAssignment> ListAssignments(string specimenId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT role, ref_kind, ref_id, assigned_utc FROM rpg_item_assignment WHERE specimen_id = $sid ORDER BY role;";
            cmd.Parameters.AddWithValue("$sid", specimenId);
            using var r = cmd.ExecuteReader();

            var list = new List<FusionRpg.Core.Items.EquipAssignment>();
            while (r.Read())
            {
                if (!FusionRpg.Core.Items.ItemRoles.TryParse(r.GetString(0), out var role)) continue;
                list.Add(new FusionRpg.Core.Items.EquipAssignment(specimenId, role, r.GetString(1), r.GetString(2), r.GetString(3)));
            }
            return list;
        }
    }

    /// <summary>Assign one item to the player's commander-owned standard slot.</summary>
    public void SavePlayerItemAssignment(string playerId, FusionRpg.Core.Items.ItemRole role,
        string refKind, string refId, string? assignedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("player id is required", nameof(playerId));
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            SavePlayerItemAssignmentUnlocked(db, tx, playerId, role, refKind, refId,
                assignedUtc ?? ServerClock.UtcNowDateTime.ToString("O"));
            tx.Commit();
        }
    }

    internal void SavePlayerItemAssignmentUnlocked(SqliteConnection db, SqliteTransaction tx,
        string playerId, FusionRpg.Core.Items.ItemRole role, string refKind, string refId, string assignedUtc)
    {
        ExecIn(db, tx, """
            INSERT INTO rpg_player_item_assignment (player_id, role, ref_kind, ref_id, assigned_utc)
            VALUES ($p, $role, $rk, $rid, $utc)
            ON CONFLICT(player_id, role) DO UPDATE SET
              ref_kind = excluded.ref_kind, ref_id = excluded.ref_id, assigned_utc = excluded.assigned_utc;
            """,
            ("$p", playerId), ("$role", FusionRpg.Core.Items.ItemRoles.Id(role)),
            ("$rk", refKind), ("$rid", refId), ("$utc", assignedUtc));
    }

    /// <summary>The commander half of unequip. One row deleted, same contract as
    /// <see cref="RemoveAssignment"/> — added 2026-09-16 with the owner's ruling that the commander equips through the
    /// same gate as a unique specimen; the write half already existed and had no caller.</summary>
    public bool RemovePlayerItemAssignment(string playerId, FusionRpg.Core.Items.ItemRole role)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM rpg_player_item_assignment WHERE player_id = $p AND role = $role;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$role", FusionRpg.Core.Items.ItemRoles.Id(role));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<RpgPlayerItemAssignmentRow> ListPlayerItemAssignments(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT player_id, role, ref_kind, ref_id, assigned_utc FROM rpg_player_item_assignment WHERE player_id = $p ORDER BY role;";
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<RpgPlayerItemAssignmentRow>();
            while (r.Read())
            {
                if (!FusionRpg.Core.Items.ItemRoles.TryParse(r.GetString(1), out var role)) continue;
                list.Add(new RpgPlayerItemAssignmentRow(r.GetString(0), role, r.GetString(2), r.GetString(3), r.GetString(4)));
            }
            return list;
        }
    }

    static RpgItemRow ReadItem(SqliteDataReader r) => new()
    {
        InstanceId = r.GetString(0),
        PlayerId = r.GetString(1),
        AcquiredUtc = r.GetString(2),
        OriginKind = r.GetString(3),
        OriginRef = r.IsDBNull(4) ? null : r.GetString(4),
        Locked = r.GetInt32(5) != 0,
        Seen = r.GetInt32(6) != 0,
        Stale = r.GetInt32(7) != 0,
        Disposition = r.GetString(8),
        Note = r.IsDBNull(9) ? null : r.GetString(9),
        Revision = r.GetInt64(10),
    };

    // ---- equip-runtime (module 5, the payoff) ----------------------------------------------------

    /// <summary>
    /// Apply module 4's projection at `unique-actor:` scope — the DB half of "rebuild bindings as a
    /// full projection, never a delta". The desired state (<paramref name="result"/>.Bindings) is
    /// always recomputed fresh from the live assignments before this runs
    /// (<see cref="FusionRpg.Core.Items.EquipProjector.Project"/>); this method only reconciles the
    /// stored `effect_binding` rows to match it — withdrawing an instance no longer projected,
    /// binding one newly projected, touching neither for one that is already correct. That is what
    /// "never a delta" protects against (a binding computed by patching the OLD binding state), not a
    /// requirement to literally drop and recreate every row on every call.
    /// </summary>
    /// <param name="owner">Which scope holds the bindings. Defaults to this specimen's own
    /// <see cref="OwnerKind.UniqueActor"/> scope; the commander passes its <see cref="OwnerKind.Player"/> scope, because
    /// the commander is not a specimen row (owner ruling 2026-09-16 — one equip feature, two durable homes).</param>
    public void ApplyEquipProjection(string specimenId, FusionRpg.Core.Items.ProjectionResult result, string? boundUtc = null,
        OwnerScope? owner = null)
    {
        var scope = owner ?? new OwnerScope(OwnerKind.UniqueActor, specimenId);

        var desired = result.Bindings
            .Where(b => string.Equals(b.RefKind, FusionRpg.Core.Items.EquipRefKinds.Rolled, StringComparison.Ordinal))
            .ToDictionary(b => b.RefId, b => b.Role, StringComparer.Ordinal);

        var existing = ListBindings(scope);

        foreach (var binding in existing)
            if (!desired.ContainsKey(binding.InstanceId))
                Withdraw(binding.BindingId);

        var alreadyBound = existing.Select(b => b.InstanceId).ToHashSet(StringComparer.Ordinal);
        foreach (var (instanceId, role) in desired)
        {
            if (alreadyBound.Contains(instanceId)) continue;
            Bind(new BindingRow
            {
                InstanceId = instanceId,
                OwnerKind = scope.Kind,
                OwnerKey = scope.Key,
                Slot = FusionRpg.Core.Items.ItemRoles.Id(role),
                Source = "equip-assign",
            }, boundUtc: boundUtc);
        }
    }

    /// <summary>
    /// The "deploy" moment module 4's own architecture named but nothing ever triggered
    /// (`decision-d1-durable-ownership.md`: "Deploy: full projection ... hand the compiled grants to
    /// the runtime"; `ssot-inventory.md:132` names the two real triggers as "at deploy (or at squad
    /// build for a battle / expedition)" — only the second exists in this server, so this is where it
    /// lives). Before this, <see cref="ApplyEquipProjection"/> and <c>ApplyEquippedGrants</c> had zero
    /// production callers (item-todo.md Checkpoint 1 / Phase 1's own named gap): an equipped rolled
    /// item persisted in <c>rpg_item_assignment</c> and changed nothing else. This closes both halves
    /// for a squad member — bindings and granted actions — from the SAME assignment read, in one call.
    ///
    /// <para>A full rebuild every call, never a delta, matching <see cref="FusionRpg.Core.Items.EquipProjector"/>'s
    /// own contract — safe and idempotent to call once per specimen on every squad build, exactly the
    /// shipped shape for "deploy", not a simplification of it.</para>
    ///
    /// <para><paramref name="level"/> is supplied by the caller rather than re-read here because
    /// `WebMatchService.BuildSquad` already resolved it for the same specimen one line above every call
    /// site — re-reading it here would be a second, and possibly diverging, source for a number the
    /// gate actually uses (<see cref="FusionRpg.Core.Items.EquipGate.Projectable"/> consults level via
    /// module 3's <c>SlotUnlock</c>).</para>
    /// </summary>
    /// <summary>Stored durability pair for the at-zero filter — read-only, never deriving.
    /// NULL (never derived, e.g. a pre-slice instance) admits; only an explicit stored 0 excludes.
    /// Any future second reader of `rpg_item_assignment` for combat contribution must apply the
    /// identical predicate (module 7's own interface note).</summary>
    public InstanceHeadPair DurabilityOf(string instanceId) => GetDurability(instanceId);

    public IReadOnlyList<FusionRpg.Core.Actions.ActionRejection> MaterializeRolledEquipRuntime(string specimenId, int level)
    {
        var assignments = ListAssignments(specimenId)
            .Where(a => string.Equals(a.RefKind, FusionRpg.Core.Items.EquipRefKinds.Rolled, StringComparison.Ordinal))
            // species-gear-chain T11 (module 7 §6, pulled forward): a broken item stops granting
            // stats by being EXCLUDED here — `DurabilityOf` reads the stored pair without deriving
            // (NULL = never derived = admitted, `?? 1`), and the already-shipped withdraw-on-absence
            // machinery below removes its grants exactly as for a genuinely unequipped item. A filter
            // on the existing query, never a new Hub gate (AGENTS.md Hard boundaries).
            .Where(a => (DurabilityOf(a.RefId).Current ?? 1) > 0)
            .ToList();

        string? ContainerIdOf(FusionRpg.Core.Items.EquipAssignment a) => GetInstance(a.RefId)?.ContainerId;

        // ⚠ EquippedGrantProjection.ForSpecimen only withdraws sources still PRESENT in `assignments` —
        // it has no memory of what used to be equipped, so it correctly re-authors a still-equipped
        // item's grants after a content edit but cannot detect "this item was unequipped since the last
        // materialize". A deploy-time reconciliation needs the same "diff against stored state"
        // ApplyEquipProjection already does for bindings: read what is actually stored, withdraw any
        // source no longer backed by a current assignment, only then let ApplyEquippedGrants (re)write
        // the rest. Found while wiring this method's first real caller — ApplyEquippedGrants alone had
        // zero production callers before, so this gap was structurally unreachable until now.
        var entityScope = new OwnerScope(OwnerKind.Entity, specimenId);
        var currentSources = assignments.Select(ContainerIdOf)
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var staleSource in ListGrants(entityScope).Select(g => g.Source).Distinct(StringComparer.Ordinal))
            if (!currentSources.Contains(staleSource))
                WithdrawGrantsBySource(entityScope, staleSource);

        var actor = new FusionRpg.Core.Items.SpecimenActor(specimenId, Frame: null, level, Faction: null);
        var projector = new FusionRpg.Core.Items.EquipProjector(new FusionRpg.Core.Items.EquipGate(),
            actorOf: _ => actor,
            itemFactsOf: a =>
            {
                var instance = GetInstance(a.RefId);
                var container = instance is null ? null : GetContainer(instance.ContainerId);
                var generation = GetItemGeneration(a.RefId);
                return new FusionRpg.Core.Items.EquipItemFacts(generation?.Frame, container?.LevelReq, FactionReq: null);
            },
            // species-gear-chain T21: filled sockets project their insert instances at the host's
            // role, through this same projection (never a parallel Bind). Only `Rolled` rows can
            // own sockets — a stock RefId is a container id, never an instance with socket rows,
            // so the kind check below is belt-and-braces against id-namespace collisions, not load-bearing.
            socketsOf: a => string.Equals(a.RefKind, FusionRpg.Core.Items.EquipRefKinds.Rolled, StringComparison.Ordinal)
                ? GetSockets(a.RefId)
                : Array.Empty<FusionRpg.Core.Items.Sockets.SocketSlot>(),
            // SSH4.6 (spec-combo-bind §2): every firing combination binds through THIS projection too.
            // The evaluator call is supplied here (the store owns the catalog, the sockets and the
            // combination inputs the boot set), so `EquipProjector` stays pure.
            combosOf: (a, slots) => ComboTargetsFor(a.RefId, slots));

        ApplyEquipProjection(specimenId, projector.Project(specimenId, assignments));

        return ApplyEquippedGrants(specimenId, assignments, containerIdOf: ContainerIdOf);
    }

    /// <summary>
    /// The commander's half of <see cref="MaterializeRolledEquipRuntime"/> (owner ruling 2026-09-16: the commander equips
    /// through the same gate as a unique specimen). Its assignments live in <c>rpg_player_item_assignment</c> and its
    /// bindings hang off the player's own <see cref="OwnerKind.Player"/> scope — the scope
    /// <c>AtomPushService.OwnersForSave</c> already pushes to the injector, so a commander item reaches a live lawn by
    /// the same route a specimen's does.
    ///
    /// <para><b>Granted actions are deliberately not part of this yet.</b> <see cref="ApplyEquippedGrants"/> binds an
    /// item's granted actions to an <see cref="OwnerKind.Entity"/> scope keyed by a specimen; the commander has no entity
    /// row, and inventing one would be the fabrication this repo's debug rule forbids. Stat atoms (the equip projection
    /// above) are what a commander item contributes today; item-granted actions on the commander are named, not faked.</para>
    /// </summary>
    public void MaterializeRolledCommanderEquipRuntime(string playerId, int level)
    {
        var commanderId = HumanDefaultCommanderStableId();
        var assignments = ListPlayerItemAssignments(playerId)
            .Where(a => string.Equals(a.RefKind, FusionRpg.Core.Items.EquipRefKinds.Rolled, StringComparison.Ordinal))
            .Select(a => new FusionRpg.Core.Items.EquipAssignment(commanderId, a.Role, a.RefKind, a.RefId, a.AssignedUtc))
            .ToList();

        var actor = new FusionRpg.Core.Items.SpecimenActor(commanderId, Frame: null, level, Faction: null);
        var projector = new FusionRpg.Core.Items.EquipProjector(new FusionRpg.Core.Items.EquipGate(),
            actorOf: _ => actor,
            itemFactsOf: a =>
            {
                var instance = GetInstance(a.RefId);
                var container = instance is null ? null : GetContainer(instance.ContainerId);
                var generation = GetItemGeneration(a.RefId);
                return new FusionRpg.Core.Items.EquipItemFacts(generation?.Frame, container?.LevelReq, FactionReq: null);
            });

        ApplyEquipProjection(commanderId, projector.Project(commanderId, assignments),
            owner: new OwnerScope(OwnerKind.Player, playerId));
    }

    // ---- rarity_budget (module 7, rarity-bands) ----------------------------------------------------

    void EnsureRarityBudgetSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- item-ideal.md, rarity-bands: a KV registry over the rarity ladder, keys validated
            -- against a closed code-side registry naming each key's consumer (SC7) -- an unknown key,
            -- or a key whose consumer has no decided shape, is refused rather than sitting inert.
            CREATE TABLE IF NOT EXISTS rarity_budget (
              rarity_id TEXT NOT NULL,
              budget_key TEXT NOT NULL,
              value_int INTEGER NOT NULL,
              PRIMARY KEY (rarity_id, budget_key)
            );
            """);
    }

    /// <summary>
    /// One `rarity_budget` row. SC7 enforced here, not just at the C# call site — a caller writing
    /// raw rows some other way still goes through this method or fails the same way `UpsertAtom`
    /// fails an unnamed kind.
    /// </summary>
    public void SetRarityBudget(string rarityId, string key, int value)
    {
        FusionRpg.Core.Items.RarityBudgetKeys.Validate(key);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            ExecIn(db, tx, """
                INSERT INTO rarity_budget (rarity_id, budget_key, value_int)
                VALUES ($id, $key, $val)
                ON CONFLICT(rarity_id, budget_key) DO UPDATE SET value_int = excluded.value_int;
                """,
                ("$id", rarityId), ("$key", key), ("$val", value));
            tx.Commit();
        }
    }

    public int? GetRarityBudget(string rarityId, string key)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value_int FROM rarity_budget WHERE rarity_id = $id AND budget_key = $key;";
            cmd.Parameters.AddWithValue("$id", rarityId);
            cmd.Parameters.AddWithValue("$key", key);
            var v = cmd.ExecuteScalar();
            return v is null ? null : Convert.ToInt32(v);
        }
    }

    /// <summary>
    /// Seed the five ready `rarity_budget` keys (`RarityBudgetKeys.All`, `HasDecidedShape`) for every
    /// rung of <see cref="FusionRpg.Core.Items.RarityLadder"/>. The `rarity` table rows themselves are
    /// NOT written here — they arrive through the standard content-import path
    /// (`gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` → `AtomSeedFile.ReadRarity` → `RpgStore.Import`'s
    /// `content.Rarities` loop), the same as every other seeded table. Duplicating that here would be
    /// a second, driftable source for the same rows. Idempotent: safe on every boot.
    /// </summary>
    public void SeedRarityLadder(IReadOnlyDictionary<string, FusionRpg.Core.Items.ItemRarityRungTuning> tuning)
    {
        foreach (var rarityId in FusionRpg.Core.Items.RarityLadder.RungIds)
        {
            if (!tuning.TryGetValue(rarityId, out var t))
                throw new InvalidOperationException($"seeding rarity budget: tuning has no rung '{rarityId}'");

            SetRarityBudget(rarityId, "promote_from", FusionRpg.Core.Items.RarityLadder.PromoteFrom(rarityId));
            SetRarityBudget(rarityId, "pity_guarded", FusionRpg.Core.Items.RarityLadder.IsPityGuarded(rarityId) ? 1 : 0);
            SetRarityBudget(rarityId, "drop_weight_default", t.DropWeightPer100k);
            SetRarityBudget(rarityId, "enhance_cap", t.EnhanceCapMilli);
            SetRarityBudget(rarityId, "power_ceiling", t.PowerCeilingShareMilli);
        }
    }
}
