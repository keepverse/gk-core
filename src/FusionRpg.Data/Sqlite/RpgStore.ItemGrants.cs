using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Grants;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// <c>item_granted_action</c> — ssot-granted-actions.md §5.2's six columns, the whole item side of
/// the grant seam (module 19).
///
/// <para>⛔ <b>The Never list (§5.3) is enforced by this DDL's shape, not by a comment.</b> There is no
/// cooldown, cost, target, condition, charge, display name or override column here, and a schema test
/// greps the shipped <c>PRAGMA table_info</c> for every forbidden name. The test is the mechanism: "if
/// a column would let two items naming the same <c>action_id</c> behave differently, it belongs to the
/// action layer or it does not exist."</para>
///
/// <para>⚠ <b><c>container_id</c> carries no FK, and that is a wiring gap, not a design one</b> — the
/// identical one module 17 recorded for <c>item_unique.derived_from</c>. §5.2 wants
/// <c>FK → item_base_type(container_id)</c>. ⚠ <b>Corrected 2026-09-23:</b> this paragraph used to say
/// the FK "has nothing to point at" because there was no table — no longer true. <c>item_base_type</c>
/// exists (<see cref="EnsureBaseTypeSchemaUnlocked"/>, populated at boot by <c>Program.cs</c>'s
/// <c>ImportBaseTypes</c>) and its <c>id</c> is a primary key, so the FK is possible; it was never
/// added, and adding one is a schema change. The reference is checked by
/// <see cref="ItemGrantValidator"/> against caller-supplied base-type facts instead.</para>
///
/// <para>⭐ <b><see cref="ApplyEquippedGrants"/> is <c>UpsertGrant</c>'s first production caller.</b>
/// The write half of <c>rpg_action_grant</c> has been shipped and callable since T1 with zero
/// production callers while the read half ran live in <c>WebMatchService.EquippedActionIdsFor</c>. The
/// pipe was connected at the far end and nothing fed it.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureItemGrantSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- ssot-granted-actions.md §5.2. SIX columns and never a seventh: the item supplies an
            -- identifier and a role, and everything answering *when*, *how much*, *at whom* or *how
            -- often* stays in rpg_action. A child table rather than a nullable column on the base type,
            -- so a unique granting two abilities needs no schema change and "at most one default-attack"
            -- is a constraint a validator can state.
            CREATE TABLE IF NOT EXISTS item_granted_action (
              container_id TEXT NOT NULL,                 -- the BASE TYPE's container id (§4.4), never an instance
              seq          INTEGER NOT NULL,              -- stable authoring and display order
              action_id    TEXT NOT NULL,                 -- FK -> rpg_action(action_id). This is the entire seam
              grant_role   TEXT NOT NULL DEFAULT 'granted', -- default-attack | granted
              enabled      INTEGER NOT NULL DEFAULT 1,    -- content is disabled, never deleted
              revision     INTEGER NOT NULL DEFAULT 0,    -- joins the E8 content hash
              PRIMARY KEY (container_id, seq)
            );

            -- So the action layer can answer "what grants this" (§5.2's one extra index).
            CREATE INDEX IF NOT EXISTS ix_item_granted_action_action ON item_granted_action(action_id);
            """);
    }

    // ---- item_granted_action ------------------------------------------------------------------

    /// <summary>Upsert one grant row on <c>(container_id, seq)</c>.</summary>
    public void UpsertItemGrantedAction(ItemGrantedActionRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            ExecParams(db, """
                INSERT INTO item_granted_action (container_id, seq, action_id, grant_role, enabled, revision)
                VALUES ($cid, $seq, $aid, $role, $enabled, $rev)
                ON CONFLICT(container_id, seq) DO UPDATE SET
                  action_id = excluded.action_id, grant_role = excluded.grant_role,
                  enabled = excluded.enabled, revision = excluded.revision;
                """,
                ("$cid", row.ContainerId), ("$seq", row.Seq), ("$aid", row.ActionId),
                ("$role", row.RoleWire), ("$enabled", row.Enabled ? 1 : 0), ("$rev", row.Revision));
        }
    }

    /// <summary>Every grant row on one base type, in <c>seq</c> order — ordinal, never a generated id.</summary>
    public IReadOnlyList<ItemGrantedActionRow> ListItemGrantedActions(string containerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT container_id, seq, action_id, grant_role, enabled, revision
                FROM item_granted_action WHERE container_id = $cid ORDER BY seq ASC;
                """;
            cmd.Parameters.AddWithValue("$cid", containerId);
            return ReadGrantRows(cmd);
        }
    }

    /// <summary>The reverse index §5.2 asks for: "what grants this action".</summary>
    public IReadOnlyList<ItemGrantedActionRow> ListContainersGranting(string actionId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT container_id, seq, action_id, grant_role, enabled, revision
                FROM item_granted_action WHERE action_id = $aid ORDER BY container_id ASC, seq ASC;
                """;
            cmd.Parameters.AddWithValue("$aid", actionId);
            return ReadGrantRows(cmd);
        }
    }

    public bool RemoveItemGrantedAction(string containerId, int seq)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM item_granted_action WHERE container_id = $cid AND seq = $seq;";
            cmd.Parameters.AddWithValue("$cid", containerId);
            cmd.Parameters.AddWithValue("$seq", seq);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    static List<ItemGrantedActionRow> ReadGrantRows(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<ItemGrantedActionRow>();
        while (r.Read())
        {
            var roleWire = r.GetString(3);
            if (!ItemGrantRoles.TryParse(roleWire, out var role))
                throw new InvalidOperationException(
                    $"item_granted_action.grant_role '{roleWire}' is outside the closed set " +
                    $"({ItemGrantRoles.Granted} | {FusionRpg.Core.Actions.Grants.ActionGrantRoles.DefaultAttack})");

            list.Add(new ItemGrantedActionRow(
                r.GetString(0), r.GetInt32(1), r.GetString(2), role,
                Enabled: r.GetInt32(4) != 0, Revision: r.GetInt32(5)));
        }
        return list;
    }

    // ---- the wiring: equip/unequip -> rpg_action_grant -----------------------------------------

    /// <summary>
    /// ⭐ <b>Wiring gap (b), closed.</b> Rebuilds every <c>rpg_action_grant</c> row this specimen's
    /// equipped items produce: withdraw by <c>source</c> first, then upsert, at
    /// <see cref="OwnerKind.Entity"/> + the specimen's own instance id — the exact scope
    /// <c>WebMatchService.EquippedActionIdsFor</c> already reads, so the rows written here are the rows
    /// that reader sees.
    ///
    /// <para>A full rebuild, never a delta: it is what <see cref="EquipProjector"/> already does with
    /// bindings, and it is what makes an item whose grant rows CHANGED converge instead of
    /// accumulate. The <c>grant_id</c> is derived, not a fresh GUID, for the same reason.</para>
    ///
    /// <para>⚠ Refusals are RETURNED, not swallowed. <c>UpsertGrant</c> runs the shipped
    /// <c>ActionValidator.ValidateGrant</c>, so with X3 unresolved (no <c>rpg_action</c> rows) every
    /// write refuses with <c>UnknownContainer</c> — the honest outcome, and the caller sees it.</para>
    /// </summary>
    /// <returns>One rejection per grant that failed to write; empty when every row landed.</returns>
    public IReadOnlyList<ActionRejection> ApplyEquippedGrants(
        string specimenId,
        IReadOnlyList<EquipAssignment> assignments,
        Func<EquipAssignment, string?> containerIdOf)
    {
        if (containerIdOf is null) throw new ArgumentNullException(nameof(containerIdOf));

        var (grants, sources) = EquippedGrantProjection.ForSpecimen(
            specimenId, assignments ?? Array.Empty<EquipAssignment>(), containerIdOf, ListItemGrantedActions);

        var owner = new OwnerScope(OwnerKind.Entity, specimenId);
        foreach (var source in sources) WithdrawGrantsBySource(owner, source);

        var failures = new List<ActionRejection>();
        foreach (var grant in grants)
        {
            var grantId = EquippedGrantProjection.GrantIdFor(specimenId, grant.Source, grant.ActionId);
            var result = UpsertGrant(grant, grantId);
            if (!result.IsOk) failures.Add(result);
        }
        return failures;
    }

    /// <summary>
    /// Unequip: delete this item's grants by <c>source</c> and leave every other item's alone. One
    /// call against the <c>ix_rpg_action_grant_source</c> index that already exists — "provenance is
    /// rows; the set is a group-by", so removing one of two sources leaves the action.
    /// </summary>
    public int WithdrawEquippedGrants(string specimenId, string containerId) =>
        WithdrawGrantsBySource(new OwnerScope(OwnerKind.Entity, specimenId), containerId);
}
