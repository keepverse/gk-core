using FusionRpg.Core.Items.Materials;
using System.Text.Json;
using FusionRpg.Core.Battle;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>One cache whose decay clock started on a given world turn (cache-notify-source spec
/// §1's `CacheClockStartRow`) — read-only projection of `rpg_corpse_cache`, plus a live item
/// count so the source can word "a cache with N items" without a second read.</summary>
public sealed record CacheClockStartedRow(
    string CacheId, string PlaceKind, string PlaceRef, string SourceKind, int DecayStartedTurn, int ItemCount);

/// <summary>One (cache, tick) decay-log row (cache-notify-source spec §1's `CacheDecayTickRow`),
/// its outcomes already reduced to counts — the caller never parses `outcomes_json` itself. Field
/// names (`Destroyed`/`Remaining`, not `*Count`) match the spec's own code style verbatim, since
/// `CacheNotificationSource` (NS6.2) reads them exactly as shown there.</summary>
public sealed record CacheDecayTickRow(
    string CacheId, int Tick, string PlaceKind, string PlaceRef, int Destroyed, int Remaining);

// deployment-hierarchy module 4 (`cache-decay-void`), spec
// `docs/architecture/deployment-hierarchy/spec-cache-decay-void.md` §Design 1-3, including the V5
// amendment (2026-09-13): `world_sector`/`world_lane` caches start their decay clock immediately
// but NEVER flip `in_void` — a sector/lane is a durable, revisitable world-map place, unlike a
// finished lawn match or a closed delve room.
//
// No wall clock is ever read in this file: the tick arithmetic runs on world-turn numbers, and
// both audit stamps (`decay_started_utc`, `rolled_utc`) arrive as caller-supplied strings —
// the same discipline `spec-delve-attrition.md:387-389` enforces, pinned by the
// `CacheDecay_module_never_reads_wall_clock` test. Every roll is a
// `SeededRng.DeriveStream(worldSeed, …)` per-mille comparison, never the shared framework RNG.

public sealed partial class RpgStore
{
    // The decay curve lives in config: `cacheDecay` in gk-core/data/tuning/deployment-hierarchy.v5.json, read
    // through DeploymentHierarchyTuningHub (solid-enforcement SE3.14, 2026-09-18). These were constants
    // here, parked "until that domain file lands" -- it had landed, and the magic-number audit's own
    // exemption said that was the event that takes them off its list. Values unchanged: base 994
    // ((994/1000)^112 ~ 0.50, a median full-decay near 112 turns), rarity step 2, cap 999.

    // Closed outcome vocabulary (spec §Tunables: structural, not tunable).
    const string DecayOutcomeSurvived = "survived";
    const string DecayOutcomeDestroyed = "destroyed";

    /// <summary>Additive schema for this module: two columns on `corpse-cache`'s header (spec
    /// §Design 1 — `decay_started_utc`/`in_void` stay module 3's declared columns, only ever
    /// written here) plus the per-(cache, tick) idempotency guard and audit trail. Called from
    /// `EnsureCorpseCacheSchemaUnlocked` so a fresh database has the whole family.</summary>
    void EnsureCacheDecaySchemaUnlocked(SqliteConnection db)
    {
        // decay_started_turn: the WORLD-STAGE TURN NUMBER the clock started at (NULL = not yet
        // running). Never a *_utc/DateTime field — a turn-counted clock cannot be reconstructed
        // from wall time, the same reason `rpg_unique_actor_recovery` carries none.
        EnsureColumn(db, "rpg_corpse_cache", "decay_started_turn", "INTEGER");
        // owner_player_id: which player's MAP-kind `rpg_worlds` row owns this cache's decay
        // clock (spec §Real gap — module 3's schema never named an owner; cheap pre-code
        // correction, supplied at insert time, never backfilled).
        EnsureColumn(db, "rpg_corpse_cache", "owner_player_id", "INTEGER");
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_corpse_cache_decay_log (
              cache_id      TEXT NOT NULL,
              tick          INTEGER NOT NULL,
              rolled_utc    TEXT NOT NULL,
              outcomes_json TEXT NOT NULL,
              PRIMARY KEY (cache_id, tick)
            );
            """);
    }

    /// <summary>Which player's map world owns a specimen's decay clock — read from the
    /// specimen's own `rpg_unique_actors` row, the information module 3 already has locally
    /// (spec §Real gap). Null when the specimen row is absent (never a guess).</summary>
    static long? ReadDecayOwnerUnlocked(SqliteConnection db, SqliteTransaction? tx, string specimenId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", specimenId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? null : Convert.ToInt64(v);
    }

    /// <summary>Starts the decay clock at cache-creation time (spec §Design 2 — the one hook,
    /// called only on a genuine INSERT, never on the reuse branch). `lawn`/`siege`/`delve_room`
    /// enter the void immediately (no durable place-row exists to wait on); `world_sector`/
    /// `world_lane` start the same clock but stay field-reachable (`in_void = 0`, V5 amendment).
    /// A second death joining an already-decaying cache never re-stamps (`AND
    /// decay_started_turn IS NULL`). Returns false without writing when the owner has no MAP-kind
    /// world row — with no map world there is no turn clock to read, so the cache waits clockless
    /// (module 3's pre-decay shape) rather than stamping a guess.</summary>
    internal bool TryStartDecayClockUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string cacheId, string placeKind,
        long ownerPlayerId, string startedUtc)
    {
        RequireCorpseCachePlaceKind(placeKind);

        int turn;
        using (var q = db.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT current_turn FROM rpg_worlds
                WHERE player_id = $p AND kind = 'map' ORDER BY world_id LIMIT 1;
                """;
            q.Parameters.AddWithValue("$p", ownerPlayerId);
            var v = q.ExecuteScalar();
            if (v is null || v == DBNull.Value) return false;
            turn = Convert.ToInt32(v);
        }

        // The one `place_kind` branch the V5 amendment adds (spec §Design 2): scoped to the
        // `in_void` write alone, for exactly the two newly-live kinds. `decay_started_turn`'s
        // own timing is identical for every kind.
        var staysReachable =
            string.Equals(placeKind, "world_sector", StringComparison.Ordinal) ||
            string.Equals(placeKind, "world_lane", StringComparison.Ordinal);

        using var u = db.CreateCommand();
        u.Transaction = tx;
        u.CommandText = """
            UPDATE rpg_corpse_cache
            SET decay_started_turn = $t, decay_started_utc = $now, in_void = $v, owner_player_id = $p
            WHERE cache_id = $id AND decay_started_turn IS NULL;
            """;
        u.Parameters.AddWithValue("$t", turn);
        u.Parameters.AddWithValue("$now", startedUtc);
        u.Parameters.AddWithValue("$v", staysReachable ? 0 : 1);
        u.Parameters.AddWithValue("$p", ownerPlayerId);
        u.Parameters.AddWithValue("$id", cacheId);
        return u.ExecuteNonQuery() > 0;
    }

    /// <summary>The per-turn tick (spec §Design 3): every remaining item/stack in each of the
    /// owner's clocked, non-empty caches rolls one independent seeded per-mille survival check
    /// for the single newly-committed turn — never a catch-up loop (`CommitWorldTurn` never
    /// advances more than one turn per call). Guarded by the `(cache_id, tick)` log row before
    /// touching any item row (the `CommandExistsUnlocked` shape), so a replayed commit never
    /// re-rolls and never re-destroys. A `kind='stack'` row is destroyed or survives as one unit
    /// (EVE's per-stack coin flip, never per-unit-within-a-stack); a destroyed `kind='instance'`
    /// row cascades through the inlined `effect_instance`/`rpg_item` deletes (`DeleteInstance`
    /// opens its own transaction and cannot be called inside this one). Empty headers are left
    /// in place, never deleted. Returns the number of caches processed.</summary>
    internal int TickCorpseCacheDecayForPlayerUnlocked(
        SqliteConnection db, SqliteTransaction? tx, long ownerPlayerId, int newTurn,
        ulong worldSeed, string rolledUtc)
    {
        var caches = new List<string>();
        using (var q = db.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT c.cache_id FROM rpg_corpse_cache c
                WHERE c.owner_player_id = $p AND c.decay_started_turn IS NOT NULL
                  AND EXISTS (SELECT 1 FROM rpg_corpse_cache_item i WHERE i.cache_id = c.cache_id)
                  AND NOT EXISTS (SELECT 1 FROM rpg_corpse_cache_decay_log l
                                  WHERE l.cache_id = c.cache_id AND l.tick = $t)
                ORDER BY c.cache_id;
                """;
            q.Parameters.AddWithValue("$p", ownerPlayerId);
            q.Parameters.AddWithValue("$t", newTurn);
            using var r = q.ExecuteReader();
            while (r.Read()) caches.Add(r.GetString(0));
        }

        var processed = 0;
        foreach (var cacheId in caches)
        {
            var items = new List<(int Seq, string Kind, string? InstanceId)>();
            using (var q = db.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT seq, kind, instance_id FROM rpg_corpse_cache_item
                    WHERE cache_id = $id ORDER BY seq;
                    """;
                q.Parameters.AddWithValue("$id", cacheId);
                using var r = q.ExecuteReader();
                while (r.Read())
                    items.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }

            var outcomes = new List<CacheDecayOutcome>(items.Count);
            foreach (var (seq, kind, instanceId) in items)
            {
                // §Design 3a: the item's own rarity-ordinal raises its personal per-tick
                // survival ‰, capped below 1000 — the same axis `loot-pack`'s `valuePerCellMilli`
                // already reads. Absent from `item_generation` (hand-seeded test rows) reads as 0.
                var rarityOrdinal = 0;
                if (string.Equals(kind, CorpseCacheItemKindInstance, StringComparison.Ordinal) &&
                    instanceId is not null)
                    rarityOrdinal = ReadRarityOrdinalUnlocked(db, tx, instanceId);

                var stream = SeededRng.DeriveStream(worldSeed, $"corpse-decay:{cacheId}:{newTurn}:{seq}");
                var decay = DeploymentHierarchyTuningHub.Tuning.CacheDecay;
                var effectiveMilli = Math.Min(decay.CapMilli, checked(decay.BaseMilli + rarityOrdinal * decay.RarityStepMilli));
                var survives = stream.NextPerMille() < effectiveMilli;
                if (!survives)
                    DestroyCacheItemUnlocked(db, tx, cacheId, seq, kind, instanceId);
                outcomes.Add(new CacheDecayOutcome(
                    seq, kind, survives ? DecayOutcomeSurvived : DecayOutcomeDestroyed));
            }

            using var log = db.CreateCommand();
            log.Transaction = tx;
            log.CommandText = """
                INSERT INTO rpg_corpse_cache_decay_log (cache_id, tick, rolled_utc, outcomes_json)
                VALUES ($c, $t, $now, $json);
                """;
            log.Parameters.AddWithValue("$c", cacheId);
            log.Parameters.AddWithValue("$t", newTurn);
            log.Parameters.AddWithValue("$now", rolledUtc);
            log.Parameters.AddWithValue("$json", JsonSerializer.Serialize(outcomes, Json));
            log.ExecuteNonQuery();
            processed++;
        }

        return processed;
    }

    static int ReadRarityOrdinalUnlocked(SqliteConnection db, SqliteTransaction? tx, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT rarity_ordinal FROM item_generation WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
    }

    static void DestroyCacheItemUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string cacheId, int seq, string kind,
        string? instanceId)
    {
        // The `DeleteInstance` cascade inlined (`RpgStore.AtomInstances.cs` opens its own
        // transaction, mirroring `RpgStore.Delve.cs`'s own pack-settlement precedent) — then the
        // cache row itself. A stack dies as one unit: no per-unit roll, one row delete.
        if (string.Equals(kind, CorpseCacheItemKindInstance, StringComparison.Ordinal) &&
            instanceId is not null)
        {
            foreach (var sql in new[]
            {
                "DELETE FROM effect_binding WHERE instance_id = $id;",
                "DELETE FROM effect_instance_atom WHERE instance_id = $id;",
                "DELETE FROM rpg_item WHERE instance_id = $id;",
                "DELETE FROM effect_instance WHERE instance_id = $id;",
            })
            {
                using var del = db.CreateCommand();
                del.Transaction = tx;
                del.CommandText = sql;
                del.Parameters.AddWithValue("$id", instanceId);
                del.ExecuteNonQuery();
            }
        }

        using var item = db.CreateCommand();
        item.Transaction = tx;
        item.CommandText = "DELETE FROM rpg_corpse_cache_item WHERE cache_id = $c AND seq = $s;";
        item.Parameters.AddWithValue("$c", cacheId);
        item.Parameters.AddWithValue("$s", seq);
        item.ExecuteNonQuery();
    }

    /// <summary>One item's fate on one tick — audit only, never read by arithmetic (spec §Design 1).</summary>
    sealed record CacheDecayOutcome(int Seq, string Kind, string Outcome);

    /// <summary>NS6.1 (cache-notify-source spec §1) — every cache this owner's decay clock started
    /// within <c>[from, to]</c> inclusive. Read-only; no schema change, no change to the tick.</summary>
    public IReadOnlyList<CacheClockStartedRow> ListCacheClocksStarted(long ownerPlayerId, int from, int to)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT c.cache_id, c.place_kind, c.place_ref, c.source_kind, c.decay_started_turn,
                       (SELECT COUNT(*) FROM rpg_corpse_cache_item i WHERE i.cache_id = c.cache_id)
                FROM rpg_corpse_cache c
                WHERE c.owner_player_id = $p AND c.decay_started_turn BETWEEN $from AND $to
                ORDER BY c.decay_started_turn, c.cache_id;
                """;
            cmd.Parameters.AddWithValue("$p", ownerPlayerId);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);

            var rows = new List<CacheClockStartedRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new CacheClockStartedRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5)));
            return rows;
        }
    }

    /// <summary>NS6.1 (cache-notify-source spec §1) — every decay-log row for this owner's caches
    /// within <c>[from, to]</c> inclusive, `outcomes_json` reduced to destroyed/remaining counts
    /// here so no consumer touches the raw JSON. Read-only; no schema change, no change to the
    /// tick.</summary>
    public IReadOnlyList<CacheDecayTickRow> ListCacheDecayTicks(long ownerPlayerId, int from, int to)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT l.cache_id, l.tick, c.place_kind, c.place_ref, l.outcomes_json
                FROM rpg_corpse_cache_decay_log l
                JOIN rpg_corpse_cache c ON c.cache_id = l.cache_id
                WHERE c.owner_player_id = $p AND l.tick BETWEEN $from AND $to
                ORDER BY l.tick, l.cache_id;
                """;
            cmd.Parameters.AddWithValue("$p", ownerPlayerId);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);

            var rows = new List<CacheDecayTickRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cacheId = r.GetString(0);
                var tick = r.GetInt32(1);
                var placeKind = r.GetString(2);
                var placeRef = r.GetString(3);
                using var doc = JsonDocument.Parse(r.GetString(4));

                var destroyed = 0;
                var remaining = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var outcome = el.GetProperty("outcome").GetString();
                    if (string.Equals(outcome, DecayOutcomeDestroyed, StringComparison.Ordinal)) destroyed++;
                    else if (string.Equals(outcome, DecayOutcomeSurvived, StringComparison.Ordinal)) remaining++;
                }
                rows.Add(new CacheDecayTickRow(cacheId, tick, placeKind, placeRef, destroyed, remaining));
            }
            return rows;
        }
    }
}
