using FusionRpg.Core.Achievements;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

// Achievement + title registry tables (spec-achievement-registry.md).
// One unlock/lifecycle ledger with key-namespaced reasons; exactly-once on
// (player_id, scope_kind, scope_key, def_id, revision, dedupe_key).
public sealed partial class RpgStore
{
    void EnsureAchievementSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_achievement_def (
              def_id TEXT NOT NULL PRIMARY KEY,
              revision INTEGER NOT NULL DEFAULT 1,
              scope TEXT NOT NULL,
              trigger TEXT NOT NULL,
              trigger_payload TEXT NOT NULL DEFAULT '{}',
              persistence TEXT NOT NULL,
              reearn_scope TEXT NOT NULL DEFAULT 'never',
              visibility TEXT NOT NULL DEFAULT 'revealed',
              tier INTEGER NOT NULL DEFAULT 0,
              bundle_ref TEXT NOT NULL DEFAULT '',
              sink_stock TEXT NOT NULL DEFAULT '',
              sink_reason TEXT NOT NULL DEFAULT '',
              definition_json TEXT NOT NULL DEFAULT '{}',
              updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rpg_achievement_unlock (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              player_id INTEGER NOT NULL,
              scope_kind TEXT NOT NULL,
              scope_key TEXT NOT NULL,
              def_id TEXT NOT NULL,
              revision INTEGER NOT NULL,
              dedupe_key TEXT NOT NULL,
              kind TEXT NOT NULL,
              evidence_json TEXT NOT NULL DEFAULT '{}',
              t TEXT NOT NULL,
              UNIQUE(player_id, scope_kind, scope_key, def_id, revision, dedupe_key)
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_achievement_unlock_player
              ON rpg_achievement_unlock(player_id, id);
            CREATE INDEX IF NOT EXISTS ix_rpg_achievement_unlock_def
              ON rpg_achievement_unlock(def_id, revision);
            """);
    }

    /// <summary>
    /// Register one definition row. Returns false with a cause naming the row instead of
    /// throwing, so one bad row never fails the whole load (per-row isolation).
    /// </summary>
    public bool TryRegisterAchievementDefinition(
        AchievementDefinition def, string definitionJson, string utc, out string? cause)
    {
        try
        {
            AchievementRegistryValidator.Validate(def);
        }
        catch (RegistryLoadException ex)
        {
            cause = ex.Message;
            return false;
        }
        lock (_gate)
        {
            using var db = Open();
            using (var scopeCmd = db.CreateCommand())
            {
                // Single namespace: the same id in two scopes is a load rejection, never an overwrite.
                scopeCmd.CommandText = "SELECT scope FROM rpg_achievement_def WHERE def_id=$id;";
                scopeCmd.Parameters.AddWithValue("$id", def.DefId);
                var existing = (string?)scopeCmd.ExecuteScalar();
                if (existing is not null && !string.Equals(existing, def.Scope, StringComparison.Ordinal))
                {
                    cause = $"achievement id {def.DefId}: DuplicateIdAcrossScopes";
                    return false;
                }
            }
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rpg_achievement_def(
                  def_id, revision, scope, trigger, trigger_payload, persistence, reearn_scope,
                  visibility, tier, bundle_ref, sink_stock, sink_reason,
                  definition_json, updated_utc)
                VALUES($id, $rev, $scope, $trg, $payload, $per, $re, $vis, $tier,
                  $bundle, $stock, $reason, $json, $t)
                ON CONFLICT(def_id) DO UPDATE SET
                  revision = excluded.revision,
                  scope = excluded.scope,
                  trigger = excluded.trigger,
                  trigger_payload = excluded.trigger_payload,
                  persistence = excluded.persistence,
                  reearn_scope = excluded.reearn_scope,
                  visibility = excluded.visibility,
                  tier = excluded.tier,
                  bundle_ref = excluded.bundle_ref,
                  sink_stock = excluded.sink_stock,
                  sink_reason = excluded.sink_reason,
                  definition_json = excluded.definition_json,
                  updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$id", def.DefId);
            cmd.Parameters.AddWithValue("$rev", def.Revision);
            cmd.Parameters.AddWithValue("$scope", def.Scope);
            cmd.Parameters.AddWithValue("$trg", def.Trigger);
            cmd.Parameters.AddWithValue("$payload", def.TriggerPayloadJson ?? "{}");
            cmd.Parameters.AddWithValue("$per", def.Persistence);
            cmd.Parameters.AddWithValue("$re", def.ReearnScope);
            cmd.Parameters.AddWithValue("$vis", def.Visibility);
            cmd.Parameters.AddWithValue("$tier", def.Tier);
            cmd.Parameters.AddWithValue("$bundle", def.BundleRef);
            cmd.Parameters.AddWithValue("$stock", def.SinkStock);
            cmd.Parameters.AddWithValue("$reason", def.SinkReason);
            cmd.Parameters.AddWithValue("$json", definitionJson);
            cmd.Parameters.AddWithValue("$t", utc);
            cmd.ExecuteNonQuery();
        }
        cause = null;
        return true;
    }

    /// <summary>
    /// Append one unlock row. Idempotent on the exactly-once key: repeats return the
    /// canonical receipt with <c>replayed</c> set, so downstream fan-out can reference
    /// the original row on re-ingest (soul-ledger receipt discipline).
    /// </summary>
    public (long UnlockId, bool Replayed) TryAppendAchievementUnlock(
        long playerId, string scopeKind, string scopeKey, string defId, int revision,
        string dedupeKey, string kind, string t, string evidenceJson = "{}")
    {
        if (kind is not ("grant" or "re-grant"))
            throw new RegistryLoadException($"achievement unlock {defId}: UnknownKind {kind}");
        lock (_gate)
        {
            using var db = Open();
            return TryAppendAchievementUnlockUnlocked(
                db, playerId, scopeKind, scopeKey, defId, revision, dedupeKey, kind, t, evidenceJson);
        }
    }

    internal (long UnlockId, bool Replayed) TryAppendAchievementUnlockUnlocked(
        SqliteConnection db, long playerId, string scopeKind, string scopeKey,
        string defId, int revision, string dedupeKey, string kind, string t,
        string evidenceJson = "{}")
    {
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO rpg_achievement_unlock(
                  player_id, scope_kind, scope_key, def_id, revision, dedupe_key, kind, evidence_json, t)
                VALUES($p, $sk, $skey, $id, $rev, $dk, $k, $ev, $t);
                SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() ELSE 0 END;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$sk", scopeKind);
            cmd.Parameters.AddWithValue("$skey", scopeKey);
            cmd.Parameters.AddWithValue("$id", defId);
            cmd.Parameters.AddWithValue("$rev", revision);
            cmd.Parameters.AddWithValue("$dk", dedupeKey);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$ev", evidenceJson);
            cmd.Parameters.AddWithValue("$t", t);
            var fresh = (long)(cmd.ExecuteScalar() ?? 0L);
            if (fresh != 0) return (fresh, false);
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id FROM rpg_achievement_unlock
                WHERE player_id=$p AND scope_kind=$sk AND scope_key=$skey
                  AND def_id=$id AND revision=$rev AND dedupe_key=$dk;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$sk", scopeKind);
            cmd.Parameters.AddWithValue("$skey", scopeKey);
            cmd.Parameters.AddWithValue("$id", defId);
            cmd.Parameters.AddWithValue("$rev", revision);
            cmd.Parameters.AddWithValue("$dk", dedupeKey);
            return ((long)(cmd.ExecuteScalar() ?? 0L), true);
        }
    }

    /// <summary>Canonical read-back of an unlock row by its exactly-once key. Null when absent.</summary>
    public long? GetAchievementUnlockByKey(
        long playerId, string scopeKind, string scopeKey,
        string defId, int revision, string dedupeKey)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT id FROM rpg_achievement_unlock
                WHERE player_id=$p AND scope_kind=$sk AND scope_key=$skey
                  AND def_id=$id AND revision=$rev AND dedupe_key=$dk;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$sk", scopeKind);
            cmd.Parameters.AddWithValue("$skey", scopeKey);
            cmd.Parameters.AddWithValue("$id", defId);
            cmd.Parameters.AddWithValue("$rev", revision);
            cmd.Parameters.AddWithValue("$dk", dedupeKey);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? null : (long)v;
        }
    }

    /// <summary>Grant vs re-grant marker for an unlock row (audit read-back).</summary>
    public string? GetAchievementUnlockKind(long unlockId)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT kind FROM rpg_achievement_unlock WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", unlockId);
            return (string?)cmd.ExecuteScalar();
        }
    }

    /// <summary>
    /// Cold achievement evaluation over durable fact evidence (spec-achievement-evaluator.md).
    /// Lookup-first (existing row returns its receipt), criterion-checked, race-safe via the
    /// UNIQUE key: concurrent racers share one dedupe key, exactly one inserts. Composite
    /// dedupe keys exclude varying evidence (counts, fact ids, turns) — those ride in
    /// evidence_json for audit, never in the key. Returns null when the criterion is unmet.
    /// Verbs beyond counter-reach + event-seen refuse with cause (never a silent skip).
    /// </summary>
    public (long UnlockId, bool Replayed)? EvaluateAchievement(
        AchievementDefinition def, AchievementEvidence ev)
    {
        var scopeKey = def.Scope == AchievementScopes.Empire
            ? ev.PlayerId.ToString()
            : string.IsNullOrWhiteSpace(ev.ScopeKey)
                ? throw new RegistryLoadException($"achievement id {def.DefId}: MissingScopeKey")
                : ev.ScopeKey;
        var worldFold = def.ReearnScope switch
        {
            AchievementReearn.World => string.IsNullOrWhiteSpace(ev.WorldId)
                ? throw new RegistryLoadException($"achievement id {def.DefId}: MissingWorldId")
                : ev.WorldId,
            AchievementReearn.Season => string.IsNullOrWhiteSpace(ev.SeasonId)
                ? throw new RegistryLoadException($"achievement id {def.DefId}: MissingSeasonId")
                : ev.SeasonId,
            _ => string.Empty,
        };
        string? dedupe = def.Trigger switch
        {
            AchievementTriggers.CounterReach => EvaluateCounterDedupe(def, ev, worldFold),
            // Absent fact is an unmet criterion (null), not a caller bug — same as count < need.
            AchievementTriggers.EventSeen => ev.FactId?.ToString(),
            _ => throw new RegistryLoadException($"achievement id {def.DefId}: UnknownTriggerAtEval {def.Trigger}"),
        };
        if (dedupe is null) return null;
        lock (_gate)
        {
            using var db = Open();
            var prior = GetAchievementUnlockByKeyUnlocked(
                db, ev.PlayerId, def.Scope, scopeKey, def.DefId, def.Revision, dedupe);
            if (prior is not null) return (prior.Value, true);
            var kind = def.ReearnScope != AchievementReearn.Never && HasUnlockForDefUnlocked(
                db, ev.PlayerId, def.Scope, scopeKey, def.DefId)
                ? "re-grant" : "grant";
            var evidence = $"{{\"count\":{ev.Count},\"maxFactId\":{ev.MaxFactId},\"turn\":{ev.Turn}}}";
            return TryAppendAchievementUnlockUnlocked(
                db, ev.PlayerId, def.Scope, scopeKey, def.DefId, def.Revision,
                dedupe, kind, ev.T, evidence);
        }
    }

    static string? EvaluateCounterDedupe(
        AchievementDefinition def, AchievementEvidence ev, string worldFold)
    {
        var need = AchievementRegistryValidator.TriggerNeed(def);
        if (ev.Count < need) return null;
        // Race-stable: threshold + world fold only. Live counts, fact ids and turns
        // vary between racers and would fork duplicate rows; they ride in evidence_json.
        return $"need={need}:w={worldFold}";
    }

    long? GetAchievementUnlockByKeyUnlocked(
        SqliteConnection db, long playerId, string scopeKind, string scopeKey,
        string defId, int revision, string dedupeKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM rpg_achievement_unlock
            WHERE player_id=$p AND scope_kind=$sk AND scope_key=$skey
              AND def_id=$id AND revision=$rev AND dedupe_key=$dk;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$sk", scopeKind);
        cmd.Parameters.AddWithValue("$skey", scopeKey);
        cmd.Parameters.AddWithValue("$id", defId);
        cmd.Parameters.AddWithValue("$rev", revision);
        cmd.Parameters.AddWithValue("$dk", dedupeKey);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (long)v;
    }

    static bool HasUnlockForDefUnlocked(
        SqliteConnection db, long playerId, string scopeKind, string scopeKey, string defId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM rpg_achievement_unlock
            WHERE player_id=$p AND scope_kind=$sk AND scope_key=$skey AND def_id=$id;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$sk", scopeKind);
        cmd.Parameters.AddWithValue("$skey", scopeKey);
        cmd.Parameters.AddWithValue("$id", defId);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }
}
