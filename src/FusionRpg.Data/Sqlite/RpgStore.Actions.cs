using System.Text.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// <c>rpg_action</c> and its four satellite tables (spec-action-model.md, A1): cost, effect scope,
/// grant, and the species-basics source of an actor's three intrinsics plus its innate.
///
/// <para><b>Actions are mechanism, not content</b> — same law as <c>effect_container</c> (T1
/// acceptance): this ships the tables and their validation; authored rows land as later modules'
/// specs do.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureActionSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_action (
              action_id TEXT NOT NULL PRIMARY KEY,
              name TEXT NOT NULL DEFAULT '',
              kind TEXT NOT NULL,
              rung INTEGER NOT NULL DEFAULT 0,
              tags_json TEXT NOT NULL DEFAULT '[]',
              enabled INTEGER NOT NULL DEFAULT 1,
              revision INTEGER NOT NULL DEFAULT 0,

              grantable INTEGER NOT NULL DEFAULT 0,
              default_attack_eligible INTEGER NOT NULL DEFAULT 0,

              container_id TEXT NOT NULL,

              time_cost_ticks INTEGER NOT NULL DEFAULT 0,
              speed_channel TEXT NOT NULL DEFAULT '',
              cooldown_channel TEXT,
              windup_ticks INTEGER NOT NULL DEFAULT 0,
              resolve_offsets_json TEXT NOT NULL DEFAULT '[0]',
              recovery_ticks INTEGER NOT NULL DEFAULT 0,
              commitment TEXT NOT NULL DEFAULT 'lateBound',
              interruptible TEXT NOT NULL DEFAULT 'onCC',
              interrupt_refund_milli INTEGER NOT NULL DEFAULT 0,
              slot_consuming INTEGER NOT NULL DEFAULT 1,
              priority_band INTEGER NOT NULL DEFAULT 0,

              cooldown_class TEXT NOT NULL DEFAULT 'none',
              cooldown_key TEXT,
              cooldown_ticks INTEGER NOT NULL DEFAULT 0,
              starts_at TEXT NOT NULL DEFAULT 'resolve',
              interrupt_cooldown_milli INTEGER NOT NULL DEFAULT 1000,

              target_spec_json TEXT,
              min_range INTEGER NOT NULL DEFAULT 0,
              max_range INTEGER NOT NULL DEFAULT 0,
              range_channel TEXT,
              requires_line_of_sight INTEGER NOT NULL DEFAULT 0,

              conditions_json TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_action_kind ON rpg_action(kind);
            CREATE INDEX IF NOT EXISTS ix_rpg_action_container ON rpg_action(container_id);

            CREATE TABLE IF NOT EXISTS rpg_action_cost (
              action_id TEXT NOT NULL,
              resource_id TEXT NOT NULL,
              amount_spec_json TEXT NOT NULL,
              when_paid TEXT NOT NULL DEFAULT 'onCommit',
              PRIMARY KEY (action_id, resource_id, when_paid)
            );

            CREATE TABLE IF NOT EXISTS rpg_action_effect_scope (
              action_id TEXT NOT NULL,
              atom_id TEXT NOT NULL,
              scope TEXT NOT NULL DEFAULT 'eachTarget',
              PRIMARY KEY (action_id, atom_id)
            );

            -- No `instance_id` column: a granted action has no instance and no rolls
            -- (spec-action-model.md §5 — the correction from item/ssot-granted-actions.md §5.5 item 5).
            CREATE TABLE IF NOT EXISTS rpg_action_grant (
              grant_id TEXT NOT NULL PRIMARY KEY,
              owner_kind TEXT NOT NULL,
              owner_key TEXT NOT NULL DEFAULT '',
              action_id TEXT NOT NULL,
              source TEXT NOT NULL DEFAULT '',
              grant_role TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_action_grant_owner ON rpg_action_grant(owner_kind, owner_key);
            CREATE INDEX IF NOT EXISTS ix_rpg_action_grant_source ON rpg_action_grant(source);

            CREATE TABLE IF NOT EXISTS rpg_action_species_basics (
              species_key TEXT NOT NULL PRIMARY KEY,
              attack_action_id TEXT NOT NULL,
              guard_action_id TEXT NOT NULL,
              move_action_id TEXT NOT NULL,
              innate_action_id TEXT
            );
            """);

        // A-E1 (spec-eligibility-axis.md §3.0/§6a gate 2): a database created before this module has
        // rpg_action without these six columns — CREATE TABLE IF NOT EXISTS is a no-op against it, so
        // the addition has to be explicit, same shape as T3.4's effect_instance migration above.
        // Defaults (scope='general', pairing_role='none') only ever apply to pre-migration rows read
        // back after this point; UpsertAction always supplies real values from here on.
        EnsureColumn(db, "rpg_action", "scope", "TEXT NOT NULL DEFAULT 'general'");
        EnsureColumn(db, "rpg_action", "scope_key", "TEXT");
        EnsureColumn(db, "rpg_action", "category", "TEXT");
        EnsureColumn(db, "rpg_action", "pairing_role", "TEXT NOT NULL DEFAULT 'none'");
        EnsureColumn(db, "rpg_action", "structure_axes_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(db, "rpg_action", "atom_families_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(db, "rpg_action", "rung_band_json", "TEXT");
        // base-defense siege-cover, decision 35, the fifth of the five plumbing sites
        // RequiresLineOfSight already occupies. Default 7 = ProjectilePenalties.All (Range|Obstruction|
        // MeleeLock) -- an ordinary shot pays everything, matching the enum's own default and every
        // existing row's correct behavior (no exemption authored, none applied).
        EnsureColumn(db, "rpg_action", "projectile_penalties", "INTEGER NOT NULL DEFAULT 7");
        // item-content `granted-action-text` (T14, spec-granted-action-text.md criterion 1): the
        // display KEY for the action's description, per `ssot-presentation.md` §3.6 L3 — the same
        // `_key` shape `rarity.display_key` and `item_unique.flavour_key` already use, never a
        // literal sentence. Card block 9 (§9.14) reads it. Default '' means "no description
        // authored", which `DisplayRules.MissingDisplayKey` reports rather than papering over.
        //
        // ⚠ Deliberately NOT joined to `ContentHashRegistry`'s `rpg_action` column list. That list
        // is explicit, and every column added to this table since V6 (`scope`, `scope_key`,
        // `category`, `pairing_role`, `structure_axes_json`, `atom_families_json`, `rung_band_json`,
        // `projectile_penalties`) stayed out of it too. Adding one would force a
        // `CurrentSchemaVersion` bump that moves every content stamp in the tree for a string that
        // changes no battle outcome.
        EnsureColumn(db, "rpg_action", "description_key", "TEXT NOT NULL DEFAULT ''");
        Exec(db, "CREATE INDEX IF NOT EXISTS ix_rpg_action_scope ON rpg_action(scope, scope_key);");
    }

    // ---- rpg_action -------------------------------------------------------------------------------

    /// <summary>All atom ids a container holds — fixed core and pool alike, since a pool draw can
    /// still need a scope row. Returns null when the container itself is unknown.
    ///
    /// <para>T3.1 (affix-schema): a pool row names an affix, not an atom directly — every CONCRETE
    /// ref inside that affix's bundle counts (a slot-bearing ref has no single atom until
    /// `resolution-order`, module 2, resolves it, so it contributes nothing here — the same
    /// direction <c>ContentValidation.OrphanAtoms</c> already takes for the same reason).</para>
    /// </summary>
    HashSet<string>? ContainerAtomIdsUnlocked(string containerId)
    {
        var container = GetContainer(containerId);
        if (container is null) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in container.Atoms) ids.Add(a.AtomId);
        foreach (var p in container.Pool)
            if (GetAffix(p.AffixId) is { } affix)
                foreach (var r in affix.Refs)
                    if (r.AtomId is not null) ids.Add(r.AtomId);
        return ids;
    }

    /// <summary>Validate then write. <paramref name="boardAvailable"/> flows to `ValidateAction` — see
    /// spec-action-model.md §8: an `Area` action is rejected while no board exists.</summary>
    public ActionRejection UpsertAction(ActionRow row, bool boardAvailable = false)
    {
        var atomIds = ContainerAtomIdsUnlocked(row.ContainerId);
        var check = ActionValidator.ValidateAction(row, atomIds, boardAvailable);
        if (!check.IsOk) return check;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            var e = row.Envelope;
            ExecParams(db, """
                INSERT INTO rpg_action
                  (action_id, name, kind, rung, tags_json, enabled, revision,
                   grantable, default_attack_eligible, container_id,
                   time_cost_ticks, speed_channel, cooldown_channel, windup_ticks, resolve_offsets_json,
                   recovery_ticks, commitment, interruptible, interrupt_refund_milli, slot_consuming,
                   priority_band, cooldown_class, cooldown_key, cooldown_ticks, starts_at,
                   interrupt_cooldown_milli, target_spec_json, min_range, max_range,
                   range_channel, requires_line_of_sight, conditions_json,
                   scope, scope_key, category, pairing_role, structure_axes_json, atom_families_json,
                   rung_band_json, projectile_penalties, description_key)
                VALUES
                  ($id, $name, $kind, $rung, $tags, $enabled, coalesce((SELECT revision FROM rpg_action WHERE action_id = $id), 0) + 1,
                   $grantable, $dae, $container,
                   $tct, $speedCh, $cdCh, $windup, $offsets,
                   $recovery, $commitment, $interruptible, $refund, $slotConsuming,
                   $priority, $cdClass, $cdKey, $cdTicks, $startsAt,
                   $interruptCd, $tspec, $minRange, $maxRange,
                   $rangeCh, $los, $conditions,
                   $scope, $scopeKey, $category, $pairingRole, $structureAxes, $atomFamilies,
                   $rungBand, $projectilePenalties, $descriptionKey)
                -- The update is SKIPPED when nothing differs, so `revision` counts how many times
                -- this row CHANGED rather than how many times it was written -- the same fix
                -- `effect_atom`'s own UpsertAtom already carries (E14a: import twice, hash
                -- unchanged), needed here because `revision` is a T30-hashed column too and an
                -- unconditional bump would make a no-op re-save look like a content edit.
                -- `IS NOT` is SQLite's null-safe inequality; `<>` would be NULL for NULLs.
                ON CONFLICT(action_id) DO UPDATE SET
                  name = excluded.name, kind = excluded.kind, rung = excluded.rung,
                  tags_json = excluded.tags_json, enabled = excluded.enabled,
                  revision = rpg_action.revision + 1,
                  grantable = excluded.grantable, default_attack_eligible = excluded.default_attack_eligible,
                  container_id = excluded.container_id,
                  time_cost_ticks = excluded.time_cost_ticks, speed_channel = excluded.speed_channel,
                  cooldown_channel = excluded.cooldown_channel, windup_ticks = excluded.windup_ticks,
                  resolve_offsets_json = excluded.resolve_offsets_json, recovery_ticks = excluded.recovery_ticks,
                  commitment = excluded.commitment, interruptible = excluded.interruptible,
                  interrupt_refund_milli = excluded.interrupt_refund_milli, slot_consuming = excluded.slot_consuming,
                  priority_band = excluded.priority_band, cooldown_class = excluded.cooldown_class,
                  cooldown_key = excluded.cooldown_key, cooldown_ticks = excluded.cooldown_ticks,
                  starts_at = excluded.starts_at, interrupt_cooldown_milli = excluded.interrupt_cooldown_milli,
                  target_spec_json = excluded.target_spec_json,
                  min_range = excluded.min_range, max_range = excluded.max_range,
                  range_channel = excluded.range_channel,
                  requires_line_of_sight = excluded.requires_line_of_sight, conditions_json = excluded.conditions_json,
                  scope = excluded.scope, scope_key = excluded.scope_key, category = excluded.category,
                  pairing_role = excluded.pairing_role, structure_axes_json = excluded.structure_axes_json,
                  atom_families_json = excluded.atom_families_json, rung_band_json = excluded.rung_band_json,
                  projectile_penalties = excluded.projectile_penalties,
                  description_key = excluded.description_key
                WHERE rpg_action.name IS NOT excluded.name
                  OR rpg_action.kind IS NOT excluded.kind
                  OR rpg_action.rung IS NOT excluded.rung
                  OR rpg_action.tags_json IS NOT excluded.tags_json
                  OR rpg_action.enabled IS NOT excluded.enabled
                  OR rpg_action.grantable IS NOT excluded.grantable
                  OR rpg_action.default_attack_eligible IS NOT excluded.default_attack_eligible
                  OR rpg_action.container_id IS NOT excluded.container_id
                  OR rpg_action.time_cost_ticks IS NOT excluded.time_cost_ticks
                  OR rpg_action.speed_channel IS NOT excluded.speed_channel
                  OR rpg_action.cooldown_channel IS NOT excluded.cooldown_channel
                  OR rpg_action.windup_ticks IS NOT excluded.windup_ticks
                  OR rpg_action.resolve_offsets_json IS NOT excluded.resolve_offsets_json
                  OR rpg_action.recovery_ticks IS NOT excluded.recovery_ticks
                  OR rpg_action.commitment IS NOT excluded.commitment
                  OR rpg_action.interruptible IS NOT excluded.interruptible
                  OR rpg_action.interrupt_refund_milli IS NOT excluded.interrupt_refund_milli
                  OR rpg_action.slot_consuming IS NOT excluded.slot_consuming
                  OR rpg_action.priority_band IS NOT excluded.priority_band
                  OR rpg_action.cooldown_class IS NOT excluded.cooldown_class
                  OR rpg_action.cooldown_key IS NOT excluded.cooldown_key
                  OR rpg_action.cooldown_ticks IS NOT excluded.cooldown_ticks
                  OR rpg_action.starts_at IS NOT excluded.starts_at
                  OR rpg_action.interrupt_cooldown_milli IS NOT excluded.interrupt_cooldown_milli
                  OR rpg_action.target_spec_json IS NOT excluded.target_spec_json
                  OR rpg_action.min_range IS NOT excluded.min_range
                  OR rpg_action.max_range IS NOT excluded.max_range
                  OR rpg_action.range_channel IS NOT excluded.range_channel
                  OR rpg_action.requires_line_of_sight IS NOT excluded.requires_line_of_sight
                  OR rpg_action.conditions_json IS NOT excluded.conditions_json
                  OR rpg_action.scope IS NOT excluded.scope
                  OR rpg_action.scope_key IS NOT excluded.scope_key
                  OR rpg_action.category IS NOT excluded.category
                  OR rpg_action.pairing_role IS NOT excluded.pairing_role
                  OR rpg_action.structure_axes_json IS NOT excluded.structure_axes_json
                  OR rpg_action.atom_families_json IS NOT excluded.atom_families_json
                  OR rpg_action.rung_band_json IS NOT excluded.rung_band_json
                  OR rpg_action.projectile_penalties IS NOT excluded.projectile_penalties
                  OR rpg_action.description_key IS NOT excluded.description_key;
                """,
                ("$id", row.ActionId), ("$name", row.Name), ("$kind", ActionKinds.Name(row.Kind)),
                ("$rung", row.Rung),
                ("$tags", JsonSerializer.Serialize(row.Tags.Select(ActionTags.Name))),
                ("$enabled", row.Enabled ? 1 : 0),
                ("$grantable", row.Grantable ? 1 : 0), ("$dae", row.DefaultAttackEligible ? 1 : 0),
                ("$container", row.ContainerId),
                ("$tct", e.TimeCostTicks), ("$speedCh", e.SpeedChannel),
                ("$cdCh", (object?)e.CooldownChannel ?? DBNull.Value), ("$windup", e.WindupTicks),
                ("$offsets", JsonSerializer.Serialize(e.ResolveOffsets)),
                // battle-tempo commitment-binding: null means "no override, inherit the profile
                // default" -- the column stays TEXT NOT NULL (no migration), so null is the empty
                // string, which never collides with a real Commitment enum name.
                ("$recovery", e.RecoveryTicks), ("$commitment", e.Commitment?.ToString() ?? ""),
                ("$interruptible", e.Interruptible.ToString()), ("$refund", e.InterruptRefundMilli),
                ("$slotConsuming", e.SlotConsuming ? 1 : 0), ("$priority", e.PriorityBand),
                ("$cdClass", e.Class.ToString()), ("$cdKey", (object?)e.CooldownKey ?? DBNull.Value),
                ("$cdTicks", e.CooldownTicks), ("$startsAt", e.StartsAt.ToString()),
                ("$interruptCd", 1000L),
                ("$tspec", ActionTargetSpecJson.Write(row.Targeting)),
                ("$minRange", row.MinRange), ("$maxRange", row.MaxRange),
                ("$rangeCh", (object?)row.RangeChannel ?? DBNull.Value),
                ("$los", row.RequiresLineOfSight ? 1 : 0),
                ("$conditions", (object?)row.ConditionsJson ?? DBNull.Value),
                ("$scope", EligibilityScopes.Name(row.Scope)),
                ("$scopeKey", (object?)row.ScopeKey ?? DBNull.Value),
                ("$category", row.Category is { } cat ? ActionCategories.Name(cat) : (object)DBNull.Value),
                ("$pairingRole", PairingRoles.Name(row.PairingRole)),
                ("$structureAxes", JsonSerializer.Serialize(row.StructureAxes)),
                ("$atomFamilies", JsonSerializer.Serialize(row.AtomFamilies)),
                ("$rungBand", row.RungBand is { } band
                    ? JsonSerializer.Serialize(new[] { band.Floor, band.Ceiling })
                    : (object)DBNull.Value),
                ("$projectilePenalties", (int)row.ProjectilePenalties),
                ("$descriptionKey", row.DescriptionKey ?? ""));

            return ActionRejection.Ok;
        }
    }

    public ActionRow? GetAction(string actionId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return GetActionUnlocked(db, actionId);
        }
    }

    ActionRow? GetActionUnlocked(SqliteConnection db, string actionId, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
                SELECT action_id, name, kind, rung, tags_json, enabled, revision,
                       grantable, default_attack_eligible, container_id,
                       time_cost_ticks, speed_channel, cooldown_channel, windup_ticks, resolve_offsets_json,
                       recovery_ticks, commitment, interruptible, interrupt_refund_milli, slot_consuming,
                       priority_band, cooldown_class, cooldown_key, cooldown_ticks, starts_at,
                       target_spec_json, min_range, max_range,
                       range_channel, requires_line_of_sight, conditions_json,
                       scope, scope_key, category, pairing_role, structure_axes_json, atom_families_json,
                       rung_band_json, projectile_penalties, description_key
                FROM rpg_action WHERE action_id = $id;
                """;
        cmd.Parameters.AddWithValue("$id", actionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadAction(r);
    }

    /// <summary>Action ids in stable order — future content-hash and assembly-order consumers need this.</summary>
    public IReadOnlyList<string> ListActionIds()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ListActionIdsUnlocked(db);
        }
    }

    IReadOnlyList<string> ListActionIdsUnlocked(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT action_id FROM rpg_action ORDER BY action_id;";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    static ActionRow ReadAction(SqliteDataReader r)
    {
        ActionKinds.TryParse(r.GetString(2), out var kind);
        var tagNames = JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? Array.Empty<string>();
        var tags = new List<ActionTag>();
        foreach (var t in tagNames) if (ActionTags.TryParse(t, out var tag)) tags.Add(tag);

        var offsets = JsonSerializer.Deserialize<long[]>(r.GetString(14)) ?? new long[] { 0 };

        var envelope = new ActionEnvelope
        {
            ActionId = r.GetString(0),
            TimeCostTicks = r.GetInt64(10),
            SpeedChannel = r.GetString(11),
            CooldownChannel = r.IsDBNull(12) ? null : r.GetString(12),
            WindupTicks = r.GetInt64(13),
            ResolveOffsets = offsets,
            RecoveryTicks = r.GetInt64(15),
            Commitment = r.GetString(16) is { Length: > 0 } commitmentStr ? Enum.Parse<Commitment>(commitmentStr) : null,
            Interruptible = Enum.Parse<Interruptible>(r.GetString(17)),
            InterruptRefundMilli = r.GetInt32(18),
            SlotConsuming = r.GetInt32(19) != 0,
            PriorityBand = r.GetInt32(20),
            Class = Enum.Parse<CooldownClass>(r.GetString(21)),
            CooldownKey = r.IsDBNull(22) ? null : r.GetString(22),
            CooldownTicks = r.GetInt64(23),
            StartsAt = Enum.Parse<CooldownStart>(r.GetString(24)),
        };

        ActionTargetSpecJson.TryRead(r.IsDBNull(25) ? null : r.GetString(25), out var targeting);

        EligibilityScopes.TryParse(r.GetString(31), out var scope);
        ActionCategory? category = null;
        if (!r.IsDBNull(33) && ActionCategories.TryParse(r.GetString(33), out var cat)) category = cat;
        PairingRoles.TryParse(r.GetString(34), out var pairingRole);
        var structureAxes = JsonSerializer.Deserialize<string[]>(r.GetString(35)) ?? Array.Empty<string>();
        var atomFamilies = JsonSerializer.Deserialize<string[]>(r.GetString(36)) ?? Array.Empty<string>();
        RungBand? rungBand = null;
        if (!r.IsDBNull(37))
        {
            var pair = JsonSerializer.Deserialize<int[]>(r.GetString(37));
            if (pair is { Length: 2 }) rungBand = new RungBand(pair[0], pair[1]);
        }

        return new ActionRow
        {
            ActionId = r.GetString(0),
            Name = r.GetString(1),
            Kind = kind,
            Rung = r.GetInt32(3),
            Tags = tags,
            Enabled = r.GetInt32(5) != 0,
            Revision = r.GetInt64(6),
            Grantable = r.GetInt32(7) != 0,
            DefaultAttackEligible = r.GetInt32(8) != 0,
            ContainerId = r.GetString(9),
            Envelope = envelope,
            Targeting = targeting,
            MinRange = r.GetInt32(26),
            MaxRange = r.GetInt32(27),
            RangeChannel = r.IsDBNull(28) ? null : r.GetString(28),
            RequiresLineOfSight = r.GetInt32(29) != 0,
            ConditionsJson = r.IsDBNull(30) ? null : r.GetString(30),
            Scope = scope,
            ScopeKey = r.IsDBNull(32) ? null : r.GetString(32),
            Category = category,
            PairingRole = pairingRole,
            StructureAxes = structureAxes,
            AtomFamilies = atomFamilies,
            RungBand = rungBand,
            ProjectilePenalties = (ProjectilePenalties)r.GetInt32(38),
            // item-content `granted-action-text` (T14). `IsDBNull` guarded rather than assumed
            // non-null: a database created before this column existed gets it by `EnsureColumn`
            // with a '' default, but a hand-edited row can still hold NULL.
            DescriptionKey = r.IsDBNull(39) ? "" : r.GetString(39),
        };
    }

    // ---- rpg_action_cost --------------------------------------------------------------------------

    public ActionRejection UpsertCost(ActionCostRow cost)
    {
        var check = ActionValidator.ValidateCost(cost);
        if (!check.IsOk) return check;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            ExecParams(db, """
                INSERT INTO rpg_action_cost (action_id, resource_id, amount_spec_json, when_paid)
                VALUES ($action, $resource, $amount, $when)
                ON CONFLICT(action_id, resource_id, when_paid) DO UPDATE SET
                  amount_spec_json = excluded.amount_spec_json;
                """,
                ("$action", cost.ActionId), ("$resource", cost.ResourceId),
                ("$amount", ActionValueSpecJson.Write(cost.AmountSpec)),
                ("$when", ActionCostTimings.Name(cost.When)));
        }

        return ActionRejection.Ok;
    }

    public IReadOnlyList<ActionCostRow> ListCosts(string actionId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT action_id, resource_id, amount_spec_json, when_paid
                FROM rpg_action_cost WHERE action_id = $id ORDER BY resource_id, when_paid;
                """;
            cmd.Parameters.AddWithValue("$id", actionId);
            using var r = cmd.ExecuteReader();

            var list = new List<ActionCostRow>();
            while (r.Read())
            {
                ActionValueSpecJson.TryRead(r.GetString(2), out var spec);
                ActionCostTimings.TryParse(r.GetString(3), out var when);
                list.Add(new ActionCostRow(r.GetString(0), r.GetString(1), spec, when));
            }
            return list;
        }
    }

    // ---- rpg_action_effect_scope -------------------------------------------------------------------

    public ActionRejection UpsertScope(ActionScopeRow scope)
    {
        var atomIds = ContainerAtomIdsForActionUnlocked(scope.ActionId);
        if (atomIds is null)
            return ActionRejection.Fail(ActionRejectionReason.UnknownContainer,
                $"{scope.ActionId}: action does not exist");

        var check = ActionValidator.ValidateScope(scope, atomIds);
        if (!check.IsOk) return check;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            ExecParams(db, """
                INSERT INTO rpg_action_effect_scope (action_id, atom_id, scope)
                VALUES ($action, $atom, $scope)
                ON CONFLICT(action_id, atom_id) DO UPDATE SET scope = excluded.scope;
                """,
                ("$action", scope.ActionId), ("$atom", scope.AtomId),
                ("$scope", ActionEffectScopes.Name(scope.Scope)));
        }

        return ActionRejection.Ok;
    }

    HashSet<string>? ContainerAtomIdsForActionUnlocked(string actionId)
    {
        var action = GetAction(actionId);
        return action is null ? null : ContainerAtomIdsUnlocked(action.ContainerId);
    }

    /// <summary>The scope for one atom in one action, defaulting to `eachTarget` when no row exists
    /// (spec-action-model.md §4).</summary>
    public ActionEffectScope GetScope(string actionId, string atomId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT scope FROM rpg_action_effect_scope WHERE action_id = $a AND atom_id = $t;";
            cmd.Parameters.AddWithValue("$a", actionId);
            cmd.Parameters.AddWithValue("$t", atomId);
            var value = cmd.ExecuteScalar() as string;
            return value is not null && ActionEffectScopes.TryParse(value, out var scope)
                ? scope
                : ActionEffectScope.EachTarget;
        }
    }

    /// <summary>Every scope row authored for one action — T30 (A6): the catalog's structure-budget
    /// check (spec-rung-table.md §4) and its scope compile step both need the FULL set, not one atom
    /// at a time the way <see cref="GetScope"/> reads.</summary>
    public IReadOnlyList<ActionScopeRow> ListScopes(string actionId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT action_id, atom_id, scope
                FROM rpg_action_effect_scope WHERE action_id = $id ORDER BY atom_id;
                """;
            cmd.Parameters.AddWithValue("$id", actionId);
            using var r = cmd.ExecuteReader();

            var list = new List<ActionScopeRow>();
            while (r.Read())
            {
                ActionEffectScopes.TryParse(r.GetString(2), out var scope);
                list.Add(new ActionScopeRow(r.GetString(0), r.GetString(1), scope));
            }
            return list;
        }
    }

    // ---- rpg_action_grant ---------------------------------------------------------------------------

    public ActionRejection UpsertGrant(ActionGrantRow grant, string? grantId = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return UpsertGrantUnlocked(db, grant, grantId);
        }
    }

    ActionRejection UpsertGrantUnlocked(SqliteConnection db, ActionGrantRow grant, string? grantId = null,
        SqliteTransaction? tx = null)
    {
        var check = ActionValidator.ValidateGrant(grant, id => GetActionUnlocked(db, id, tx));
        if (!check.IsOk) return check;

        var id = string.IsNullOrWhiteSpace(grantId) ? Guid.NewGuid().ToString("N") : grantId!;
        if (tx is null)
            ExecParams(db, """
                INSERT INTO rpg_action_grant (grant_id, owner_kind, owner_key, action_id, source, grant_role)
                VALUES ($id, $kind, $key, $action, $source, $role)
                ON CONFLICT(grant_id) DO UPDATE SET
                  owner_kind = excluded.owner_kind, owner_key = excluded.owner_key,
                  action_id = excluded.action_id, source = excluded.source, grant_role = excluded.grant_role;
                """,
                ("$id", id), ("$kind", OwnerScope.Name(grant.OwnerKind)), ("$key", grant.OwnerKey ?? ""),
                ("$action", grant.ActionId), ("$source", grant.Source ?? ""), ("$role", grant.GrantRole ?? ""));
        else
            ExecParams(db, tx, """
                INSERT INTO rpg_action_grant (grant_id, owner_kind, owner_key, action_id, source, grant_role)
                VALUES ($id, $kind, $key, $action, $source, $role)
                ON CONFLICT(grant_id) DO UPDATE SET
                  owner_kind = excluded.owner_kind, owner_key = excluded.owner_key,
                  action_id = excluded.action_id, source = excluded.source, grant_role = excluded.grant_role;
                """,
                ("$id", id), ("$kind", OwnerScope.Name(grant.OwnerKind)), ("$key", grant.OwnerKey ?? ""),
                ("$action", grant.ActionId), ("$source", grant.Source ?? ""), ("$role", grant.GrantRole ?? ""));
        return ActionRejection.Ok;
    }

    /// <summary>Grants on one owner, ordered by `action_id` ordinal — never a generated id
    /// (spec-action-model.md §5: "never sorted on a generated id").</summary>
    public IReadOnlyList<ActionGrantRow> ListGrants(OwnerScope owner)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT owner_kind, owner_key, action_id, source, grant_role
                FROM rpg_action_grant WHERE owner_kind = $kind AND owner_key = $key
                ORDER BY action_id ASC;
                """;
            cmd.Parameters.AddWithValue("$kind", OwnerScope.Name(owner.Kind));
            cmd.Parameters.AddWithValue("$key", owner.Key ?? "");
            using var r = cmd.ExecuteReader();

            var list = new List<ActionGrantRow>();
            while (r.Read())
            {
                var ownerKind = ParseActionOwnerKind(r.GetString(0));
                list.Add(new ActionGrantRow(ownerKind, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            }
            return list;
        }
    }

    /// <summary>Withdraw every grant from one `source` on one owner — leaves every other source's grants.</summary>
    public int WithdrawGrantsBySource(OwnerScope owner, string source)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM rpg_action_grant
                WHERE owner_kind = $kind AND owner_key = $key AND source = $source;
                """;
            cmd.Parameters.AddWithValue("$kind", OwnerScope.Name(owner.Kind));
            cmd.Parameters.AddWithValue("$key", owner.Key ?? "");
            cmd.Parameters.AddWithValue("$source", source);
            return cmd.ExecuteNonQuery();
        }
    }

    static OwnerKind ParseActionOwnerKind(string name)
    {
        foreach (OwnerKind k in Enum.GetValues(typeof(OwnerKind)))
            if (string.Equals(OwnerScope.Name(k), name, StringComparison.Ordinal))
                return k;
        return OwnerKind.Match;
    }

    // ---- rpg_action_species_basics ------------------------------------------------------------------

    public ActionRejection UpsertSpeciesBasics(SpeciesBasicsRow row)
    {
        var check = ActionValidator.ValidateSpeciesBasics(row, GetAction);
        if (!check.IsOk) return check;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            ExecParams(db, """
                INSERT INTO rpg_action_species_basics
                  (species_key, attack_action_id, guard_action_id, move_action_id, innate_action_id)
                VALUES ($key, $attack, $guard, $move, $innate)
                ON CONFLICT(species_key) DO UPDATE SET
                  attack_action_id = excluded.attack_action_id, guard_action_id = excluded.guard_action_id,
                  move_action_id = excluded.move_action_id, innate_action_id = excluded.innate_action_id;
                """,
                ("$key", row.SpeciesKey), ("$attack", row.AttackActionId), ("$guard", row.GuardActionId),
                ("$move", row.MoveActionId), ("$innate", (object?)row.InnateActionId ?? DBNull.Value));
        }

        return ActionRejection.Ok;
    }

    public SpeciesBasicsRow? GetSpeciesBasics(string speciesKey)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT species_key, attack_action_id, guard_action_id, move_action_id, innate_action_id
                FROM rpg_action_species_basics WHERE species_key = $key;
                """;
            cmd.Parameters.AddWithValue("$key", speciesKey);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new SpeciesBasicsRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4));
        }
    }

    static void ExecParams(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    static void ExecParams(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
