using FusionRpg.Core.Achievements;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Materials;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

// Title lifecycle (spec-title-lifecycle.md): relative windows, honors, hidden curses,
// priced ritual removal. Providers own bindings; this module owns the lifecycle ledger
// (rpg_title_lifecycle) and policy intents. Grant/equip/expire/lift carry distinct
// dedupe keys. Withdraw always commits before the expire row (telemetry retry key);
// telemetry never rolls a withdraw back.
public sealed partial class RpgStore
{
    void EnsureTitleLifecycleSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_title_lifecycle (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              player_id INTEGER NOT NULL,
              scope_kind TEXT NOT NULL,
              scope_key TEXT NOT NULL,
              title_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              dedupe_key TEXT NOT NULL,
              turn INTEGER NOT NULL DEFAULT 0,
              evidence_json TEXT NOT NULL DEFAULT '{}',
              t TEXT NOT NULL,
              UNIQUE(player_id, scope_kind, scope_key, title_id, kind, dedupe_key)
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_title_lifecycle_title
              ON rpg_title_lifecycle(player_id, scope_kind, scope_key, title_id, id);
            """);
    }

    /// <param name="tx">⚠️ Merged 2026-09-16: `TrySpendRecipe`'s `perform` step now runs on an OPEN
    /// transaction (species-gear-chain widened it to hand back the connection AND the transaction —
    /// "a mint that saves must join this transaction, not open its own"). A command created on a
    /// connection with a pending local transaction must carry that transaction or the provider
    /// refuses it, so this takes an optional one. Null for every caller outside a spend, which is all
    /// of them but the curse-lift below — those keep their existing behaviour exactly.</param>
    long AppendLifecycleUnlocked(
        SqliteConnection db, long playerId, string scopeKind, string scopeKey,
        string titleId, string kind, string dedupeKey, long turn, string evidenceJson, string t,
        SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO rpg_title_lifecycle(
              player_id, scope_kind, scope_key, title_id, kind, dedupe_key, turn, evidence_json, t)
            VALUES($p, $sk, $skey, $t2, $k, $dk, $turn, $ev, $t);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$sk", scopeKind);
        cmd.Parameters.AddWithValue("$skey", scopeKey);
        cmd.Parameters.AddWithValue("$t2", titleId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$dk", dedupeKey);
        cmd.Parameters.AddWithValue("$turn", turn);
        cmd.Parameters.AddWithValue("$ev", evidenceJson);
        cmd.Parameters.AddWithValue("$t", t);
        cmd.ExecuteNonQuery();
        using var read = db.CreateCommand();
        read.Transaction = tx;
        read.CommandText = """
            SELECT id FROM rpg_title_lifecycle
            WHERE player_id=$p AND scope_kind=$sk AND scope_key=$skey
              AND title_id=$t2 AND kind=$k AND dedupe_key=$dk;
            """;
        read.Parameters.AddWithValue("$p", playerId);
        read.Parameters.AddWithValue("$sk", scopeKind);
        read.Parameters.AddWithValue("$skey", scopeKey);
        read.Parameters.AddWithValue("$t2", titleId);
        read.Parameters.AddWithValue("$k", kind);
        read.Parameters.AddWithValue("$dk", dedupeKey);
        return (long)(read.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Record a title window anchored at equip/grant. Refuses unknown scopes/clocks,
    /// wall-clock on world scopes, unnamed battle-tick counters, and non-positive spans.
    /// Returns the row id (re-anchors are new rows on new anchor turns, never dedupe hits).
    /// </summary>
    public (bool Ok, string Reason, long RowId) RecordTitleWindow(
        long playerId, string scopeKind, string scopeKey, string titleId,
        string clock, string? counterName, long anchorTurn, long validTurns, string t)
    {
        var refusal = TitleLifecyclePolicy.ClockRefusal(scopeKind, clock, counterName);
        if (refusal is not null) return (false, refusal, 0);
        if (validTurns <= 0)
            return (false, $"title lifecycle: non-positive span for '{titleId}'", 0);
        lock (_gate)
        {
            using var db = Open();
            var id = AppendLifecycleUnlocked(db, playerId, scopeKind, scopeKey, titleId,
                "equip", $"anchor={anchorTurn}:span={validTurns}:clock={clock}",
                anchorTurn, "{\"validTurns\":" + validTurns + "}", t);
            return (true, "", id);
        }
    }

    /// <summary>
    /// Expiry intent from the LATEST window row. Honors (no window) and absent rows never
    /// expire. Pure read — providers withdraw first, then <see cref="RecordTitleExpire"/>.
    /// </summary>
    public (bool Expired, long ExpiryTurn) EvaluateTitleExpiry(
        long playerId, string scopeKind, string scopeKey, string titleId, long currentTurn)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT turn, evidence_json FROM rpg_title_lifecycle
                WHERE player_id=$p AND scope_kind=$sk AND scope_key=$skey
                  AND title_id=$t2 AND kind='equip'
                ORDER BY turn DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$sk", scopeKind);
            cmd.Parameters.AddWithValue("$skey", scopeKey);
            cmd.Parameters.AddWithValue("$t2", titleId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (false, 0);
            var anchor = r.GetInt64(0);
            var span = ReadValidTurns(r.GetString(1));
            var expiry = anchor + span;
            return (currentTurn >= expiry, expiry);
        }
    }

    static long ReadValidTurns(string evidenceJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(evidenceJson);
            return doc.RootElement.TryGetProperty("validTurns", out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.Number &&
                v.TryGetInt64(out var n) ? n : 0;
        }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    /// <summary>
    /// Append the expire row (telemetry + retry key). Idempotent on the expire key —
    /// a retried expiry after a crash returns the same row, never a second event.
    /// Call AFTER the provider withdraws the binding.
    /// </summary>
    public long RecordTitleExpire(
        long playerId, string scopeKind, string scopeKey, string titleId,
        long equipAnchorTurn, long expiryTurn, string t)
    {
        lock (_gate)
        {
            using var db = Open();
            return AppendLifecycleUnlocked(db, playerId, scopeKind, scopeKey, titleId,
                "expire", $"anchor={equipAnchorTurn}:expired={expiryTurn}",
                expiryTurn, "{}", t);
        }
    }

    /// <summary>Honor bind: permanent, slot-free, keyed to a durable grading fact.</summary>
    public long RecordHonor(
        long playerId, string scopeKind, string scopeKey, string titleId,
        string gradingFactId, string t)
    {
        lock (_gate)
        {
            using var db = Open();
            return AppendLifecycleUnlocked(db, playerId, scopeKind, scopeKey, titleId,
                "honor", $"fact={gradingFactId}", 0,
                "{\"gradingFact\":\"" + gradingFactId + "\"}", t);
        }
    }

    /// <summary>
    /// Record a curse: hidden, earned for a Cold-recorded transgression fact from the
    /// closed v1 taxonomy, and bound passive (slot "curse") so it composes until lifted.
    /// Unknown transgressions refuse — never a silent skip.
    /// </summary>
    public (bool Ok, string Reason, long RowId) RecordCurse(
        long playerId, string scopeKind, string scopeKey, string curseId,
        string transgression, string transgressionFactId, string t)
    {
        if (!CurseTransgressions.IsKnown(transgression))
            return (false, $"unknown transgression '{transgression}'", 0);
        lock (_gate)
        {
            using var db = Open();
            var id = AppendLifecycleUnlocked(db, playerId, scopeKind, scopeKey, curseId,
                "curse", $"fact={transgressionFactId}", 0,
                "{\"transgression\":\"" + transgression +
                "\",\"visibility\":\"hidden\",\"fact\":\"" + transgressionFactId + "\"}", t);
            return (true, "", id);
        }
    }

    /// <summary>
    /// Atomic curse lift: souls + matched-essence ritual spend and curse removal in ONE
    /// material transaction (perform hook). Insufficient funds write nothing (no escrow,
    /// no partial); tombstoned curses (no live binding) refuse without spending; repeats
    /// under one correlation replay the recorded outcome.
    /// </summary>
    public MaterialSpendResult LiftCurse(
        long playerId, string actorInstanceId, string curseId,
        string elementId, long soulsPrice, long essencePrice, string correlationId)
    {
        var lines = new List<MaterialCostLine>
        {
            new(MaterialClass.Souls, "souls", soulsPrice),
            new(MaterialClass.Essence, MaterialCatalog.EssenceId(elementId), essencePrice),
        };
        // Replay short-circuit FIRST: a recorded lift already withdrew the live binding,
        // so the tombstone check below would misread a replay as a tombstone. Digest
        // equality decides replay vs caller-bug mismatch (same discipline as TrySpendRecipe).
        var prior = FindMaterialSpend(playerId, correlationId);
        if (prior is not null && prior.RecipeId == "title.ritual.lift")
            return CostJson(lines) == prior.CostJson
                ? new MaterialSpendResult(true, "replay", prior.OutcomeRef)
                : new MaterialSpendResult(false, "correlation.mismatch", "");
        var owner = new OwnerScope(OwnerKind.UniqueActor, actorInstanceId);
        var live = false;
        foreach (var b in ListBindings(owner))
            if (b.Slot == "curse") { live = true; break; }
        if (!live)
            return new MaterialSpendResult(false, "curse.tombstoned", "");
        return TrySpendRecipe(playerId, "title.ritual.lift", lines, correlationId, (db, tx) =>
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM effect_binding WHERE owner_kind='unique-actor' AND owner_key=$k AND slot='curse';
                """;
            cmd.Parameters.AddWithValue("$k", actorInstanceId);
            cmd.ExecuteNonQuery();
            var liftId = AppendLifecycleUnlocked(db, playerId, "unique-actor", actorInstanceId,
                curseId, "lift", $"corr={correlationId.Trim()}", 0, "{}", ServerClock.UtcNowDateTime.ToString("o"), tx);
            return $"lift:{liftId}";
        });
    }

    /// <summary>Normal-path read-back of lifecycle rows (probes assert through this).</summary>
    public IReadOnlyList<TitleLifecycleRow> ListTitleLifecycle(
        long playerId, string scopeKind, string scopeKey, string titleId)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT id, kind, dedupe_key, turn, evidence_json, t FROM rpg_title_lifecycle
                WHERE player_id=$p AND scope_kind=$sk AND scope_key=$skey AND title_id=$t2
                ORDER BY id;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$sk", scopeKind);
            cmd.Parameters.AddWithValue("$skey", scopeKey);
            cmd.Parameters.AddWithValue("$t2", titleId);
            var rows = new List<TitleLifecycleRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new TitleLifecycleRow(
                    r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.GetInt64(3), r.GetString(4), r.GetString(5)));
            return rows;
        }
    }

    /// <summary>
    /// Hall-full overflow victim: the equipped Hall slot with the earliest binding
    /// (bound_utc ISO ordering). Null when a slot is free — callers equip directly.
    /// </summary>
    public string? HallOverflowVictim(long playerId)
    {
        var owner = new OwnerScope(OwnerKind.Player, playerId.ToString());
        string? oldest = null;
        var oldestUtc = "\uffff";
        var filled = 0;
        foreach (var b in ListBindings(owner))
        {
            if (!HallLoadout.IsHallSlot(b.Slot, out _)) continue;
            filled++;
            var utc = BoundUtcOf(b.BindingId);
            if (string.Compare(utc, oldestUtc, StringComparison.Ordinal) < 0)
            {
                oldestUtc = utc;
                oldest = b.Slot;
            }
        }
        return filled >= HallLoadout.HallSlots ? oldest : null;
    }

    string BoundUtcOf(string bindingId)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT bound_utc FROM effect_binding WHERE binding_id=$id;";
            cmd.Parameters.AddWithValue("$id", bindingId);
            return (string?)cmd.ExecuteScalar() ?? "";
        }
    }
}

/// <summary>One lifecycle ledger row (grant/equip/expire/lift/honor/curse).</summary>
public sealed record TitleLifecycleRow(
    long Id, string Kind, string DedupeKey, long Turn, string EvidenceJson, string T);
