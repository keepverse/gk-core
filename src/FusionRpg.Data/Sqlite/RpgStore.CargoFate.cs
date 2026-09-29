using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

// scoped-inventory-hierarchy module 4 (`cargo-fate`), spec
// `docs/architecture/scoped-inventory-hierarchy/spec-cargo-fate.md` §Design 2.
//
// Move-on-legion-death: a destroyed legion's `rpg_world_entity_cargo` rows become
// `rpg_corpse_cache_item` rows under a `source_kind='legion_death'` header keyed to the legion's
// last-known place (`place_kind='world_sector'` + `AtSectorId`, or `place_kind='world_lane'` +
// `OnLaneId`). Called from `DiffEntities` (`RpgStore.WorldGraphDiff.cs`) BEFORE `DeleteMissing`,
// in the same transaction: the cargo table's FK to `rpg_world_entities` is ON DELETE CASCADE, so
// a delete-first ordering would silently destroy the cargo before any cache could be created.
// The move (cargo-row DELETE + cache-item INSERT) lands first; the cascade then finds an
// already-empty cargo table for that entity.
//
// SQL lives here, only here (guard-dal). This module never touches `rpg_world_sector_storage` /
// `DiffSectors` (module 2's resolved no-op territory) and never invents a second cache table —
// rows go through `deployment-hierarchy`'s own `ResolveOrCreateCacheUnlocked`.
public sealed partial class RpgStore
{
    /// <summary>
    /// Move-on-legion-death (spec §Design 2): delete every <c>rpg_world_entity_cargo</c> row for
    /// <paramref name="entityId"/> and insert one <c>rpg_corpse_cache_item</c> row per moved cargo
    /// row (same shape module 1's own legion-to-legion transfer uses — delete + insert, same
    /// transaction, move never copy), in the caller's transaction. The cargo row's
    /// <c>weight_each</c> snapshot column has no counterpart on the cache side (sector storage has
    /// none either, and the claim path re-resolves weight fresh from the item's derived field), so
    /// it is dropped at the move — <c>kind</c>/<c>instance_id</c>/<c>container_id</c>/<c>qty</c>
    /// transfer verbatim. The item's <c>rpg_item.disposition</c> is left at
    /// <c>'cargo'</c> (still out of armoury listings, still owned, never swept), exactly as the
    /// transfer path leaves it. Returns the cache id, or <c>null</c> when the legion carried
    /// nothing — no empty cache row is ever created.
    ///
    /// <para>Static because the caller (<c>DiffEntities</c>) is static. The reads/writes below
    /// mirror <c>ListCargoUnlocked</c> / <c>ReadWorldPlayerUnlocked</c> /
    /// <c>ResolveOrCreateCacheUnlocked</c> + <c>TryStartDecayClockUnlocked</c>'s INSERT branch
    /// without calling them (all instance members — this context has no <c>this</c>); the decay
    /// clock's V5 <c>world_sector</c>/<c>world_lane</c>-stay-reachable branch is reproduced
    /// verbatim and cited, not re-decided.</para>
    ///
    /// <para>The decay clock's owner is the map world's own <c>player_id</c>:
    /// <c>OwnerFactionId</c> is a faction string, not the numeric player the clock reads, and there
    /// is exactly one map world per player. A null owner (no world row) leaves the cache
    /// clockless, module 3's pre-decay shape.</para>
    /// </summary>
    internal static string? MoveLegionCargoToCorpseCacheUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId,
        string placeKind, string placeRef)
    {
        RequireCorpseCachePlaceKind(placeKind);
        RequireCorpseCacheSourceKind("legion_death");

        // Read first, resolve never: a cargo-less legion must not leave an empty header behind.
        var rows = ReadCargoRowsUnlocked(db, tx, worldId, entityId);
        if (rows.Count == 0)
            return null;

        var cacheId = ResolveLegionCacheUnlocked(
            db, tx, placeKind, placeRef, ReadWorldPlayerIdUnlocked(db, tx, worldId));

        // Source rows arrive in seq order; cache seqs append in that same order.
        foreach (var row in rows)
        {
            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM rpg_world_entity_cargo
                    WHERE world_id = $w AND entity_id = $e AND seq = $s;
                    """;
                del.Parameters.AddWithValue("$w", worldId);
                del.Parameters.AddWithValue("$e", entityId);
                del.Parameters.AddWithValue("$s", row.Seq);
                del.ExecuteNonQuery();
            }
            InsertCargoCacheItemUnlocked(
                db, tx, cacheId, row.Kind, row.InstanceId, row.ContainerId, row.Qty, entityId);
        }
        return cacheId;
    }

    /// <summary>Static cargo-list read for the death path (mirrors <c>ListCargoUnlocked</c>'s
    /// column shape and seq order; see the caller for why it cannot call it directly).</summary>
    static List<LegionCargoRow> ReadCargoRowsUnlocked(
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
        while (r.Read())
            list.Add(new LegionCargoRow(
                r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                r.GetInt64(7)));
        return list;
    }

    /// <summary>Static world-player read for the death path (same single-row SELECT as
    /// <c>RpgStore.LegionCargo.ReadWorldPlayerUnlocked</c>, restated as a static because this
    /// context has no <c>this</c>).</summary>
    static long? ReadWorldPlayerIdUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT player_id FROM rpg_worlds WHERE world_id = $w;";
        cmd.Parameters.AddWithValue("$w", worldId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? null : Convert.ToInt64(v);
    }

    /// <summary>
    /// Place-keyed resolve-or-create for the death path (mirrors
    /// <c>ResolveOrCreateCacheUnlocked</c>'s INSERT branch + <c>TryStartDecayClockUnlocked</c>'s
    /// per-kind branch; see the caller for why it cannot call them directly). Reuse by
    /// <c>(place_kind, place_ref)</c> is identical: two legions dying at the same place share one
    /// header. The clock starts on a genuine INSERT only, stamping the map world's current turn;
    /// <c>world_sector</c>/<c>world_lane</c> stay field-reachable (<c>in_void = 0</c>, V5
    /// amendment). A null owner (or no MAP-kind world for the owner) leaves the cache clockless.
    /// </summary>
    static string ResolveLegionCacheUnlocked(
        SqliteConnection db, SqliteTransaction tx, string placeKind, string placeRef, long? ownerPlayerId)
    {
        using (var find = db.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT cache_id FROM rpg_corpse_cache WHERE place_kind = $k AND place_ref = $r LIMIT 1;";
            find.Parameters.AddWithValue("$k", placeKind);
            find.Parameters.AddWithValue("$r", placeRef);
            if (find.ExecuteScalar() is string existing && !string.IsNullOrEmpty(existing))
                return existing;
        }

        var cacheId = "cc_" + Guid.NewGuid().ToString("N");
        var now = ServerClock.UtcNowDateTime.ToString("o");
        using (var ins = db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision)
                VALUES ($id, $k, $r, 'legion_death', $now, NULL, 0, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRef);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }
        if (ownerPlayerId.HasValue)
            StartLegionCacheClockUnlocked(db, tx, cacheId, placeKind, ownerPlayerId.Value, now);
        return cacheId;
    }

    /// <summary>
    /// Death-path decay-clock start (mirrors <c>TryStartDecayClockUnlocked</c>, which this static
    /// context cannot call; the V5 branch below is copied from it, not re-derived). Second deaths
    /// joining an already-clocked cache never re-stamp (<c>AND decay_started_turn IS NULL</c>).
    /// </summary>
    static void StartLegionCacheClockUnlocked(
        SqliteConnection db, SqliteTransaction tx, string cacheId, string placeKind,
        long ownerPlayerId, string startedUtc)
    {
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
            if (v is null || v == DBNull.Value) return;
            turn = Convert.ToInt32(v);
        }

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
        u.ExecuteNonQuery();
    }

    /// <summary>
    /// The cargo-shape cache-item insert: module 3's own <c>InsertCacheItemUnlocked</c> only writes
    /// <c>kind='instance'</c> rows (NULL container/qty), but cargo also carries <c>'stack'</c> rows
    /// whose <c>container_id</c>/<c>qty</c> must survive the move (the decay tick destroys a stack
    /// as one unit). Same <c>MAX(seq) + 1</c> append discipline.
    /// </summary>
    static void InsertCargoCacheItemUnlocked(
        SqliteConnection db, SqliteTransaction tx, string cacheId, string kind,
        string? instanceId, string? containerId, long? qty, string originOwner)
    {
        int seq;
        using (var max = db.CreateCommand())
        {
            max.Transaction = tx;
            max.CommandText = "SELECT COALESCE(MAX(seq), -1) FROM rpg_corpse_cache_item WHERE cache_id = $id;";
            max.Parameters.AddWithValue("$id", cacheId);
            seq = Convert.ToInt32(max.ExecuteScalar()) + 1;
        }
        using (var ins = db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache_item (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
                VALUES ($id, $seq, $kind, $inst, $cont, $qty, $owner);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$seq", seq);
            ins.Parameters.AddWithValue("$kind", kind);
            ins.Parameters.AddWithValue("$inst", (object?)instanceId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$cont", (object?)containerId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$qty", (object?)qty ?? DBNull.Value);
            ins.Parameters.AddWithValue("$owner", originOwner);
            ins.ExecuteNonQuery();
        }
    }
}
