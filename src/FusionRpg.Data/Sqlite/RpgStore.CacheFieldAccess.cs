using System.Text.Json;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

// deployment-hierarchy module 5 (`cache-field-access`): the §1a reachability READ plus the §2a
// claim-into-cargo WRITE (Task 0.3b).
// Spec: `docs/architecture/deployment-hierarchy/spec-cache-field-access.md` §1a (amended 2026-09-13)
// and §2a (added 2026-09-13, later session — the world-map claim's cargo write lives here, not in
// `cargo-fate`, whose own scope table shows it never calls such a verb).
//
// A world-map corpse-cache is reachable by a querying legion iff the legion's own LIVE position
// matches the cache's `place_ref`, read fresh off `rpg_world_entities` (the world-map analogue of
// the delve's `MoveParty`-written position):
//   - `world_sector`: the legion's `WorldEntity.AtSectorId` equals the cache's `place_ref`
//     (`WorldState.cs:292-293`).
//   - `world_lane`: the legion's `WorldEntity.OnLaneId` equals the cache's `place_ref`
//     (`WorldState.cs:295`), regardless of `OnLaneTowardSectorId`/`LaneProgressMilli` — a legion
//     travelling either direction on the lane is equally "on" it, per `WorldLane`'s own
//     bidirectional-travel comment (`WorldState.cs:297-299`).
//
// What this file deliberately does NOT build: §Design 2's `ClaimCorpseCacheUnlocked` (delve
// baggage into a `PackGrid` — that verb, its pack-grid destination, and its delve-keyed replay
// rows are unbuilt; the claim-log table below already carries the generic shape for it).
//
// SQL lives here, only here (guard-dal). The claim reuses `legion-cargo`'s own
// `WeightCapacityUnlocked`/`SlotCapacityUnlocked` (+ `WeightUsedUnlocked`/`SlotsUsedUnlocked`/
// `NextSeqUnlocked`) and writes `rpg_world_entity_cargo` rows shaped exactly like
// `LoadCargoUnlocked`'s own INSERT — reused, never redefined (spec §2a, AC5).
/// <summary>One item claimed from a corpse-cache into a legion's cargo — the §2a result.
/// <c>ClaimedSeqs</c>/<c>SkippedSeqs</c> are cache-side <c>seq</c> values in ascending order; a
/// replayed claim returns the byte-identical record (spec §2a replay safety).</summary>
public sealed record CorpseCacheCargoClaimResult(
    bool Ok,
    string Reason,
    IReadOnlyList<int> ClaimedSeqs,
    IReadOnlyList<int> SkippedSeqs);

public sealed partial class RpgStore
{
    /// <summary>
    /// World-map reachability read (spec §1a). Returns the non-void, non-empty `world_sector` /
    /// `world_lane` caches pinned exactly where legion <paramref name="entityId"/> stands right now.
    /// Callable on an open connection/transaction; the caller owns the commit. An unknown legion, or
    /// a legion row that violates `WorldState`'s own "at a sector, or on a lane — never both, never
    /// neither" invariant (`WorldState.cs:292-295`), sees nothing rather than a guess.
    /// </summary>
    internal List<RpgCorpseCacheRow> ListClaimableCachesUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string worldId, string entityId)
    {
        var (found, atSector, onLane) = ReadLegionPositionUnlocked(db, tx, worldId, entityId);
        if (!found)
            return new List<RpgCorpseCacheRow>();

        // Exactly one side of the invariant may be set; anything else (both-set/neither-set
        // corrupt rows) is unpositioned and therefore unreachable.
        string placeKind;
        string placeRef;
        if (atSector is not null && onLane is null)
        {
            placeKind = "world_sector";
            placeRef = atSector;
        }
        else if (onLane is not null && atSector is null)
        {
            placeKind = "world_lane";
            placeRef = onLane;
        }
        else
        {
            return new List<RpgCorpseCacheRow>();
        }

        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision
            FROM rpg_corpse_cache
            WHERE place_kind = $k AND place_ref = $r AND in_void = 0
              AND EXISTS (SELECT 1 FROM rpg_corpse_cache_item WHERE cache_id = rpg_corpse_cache.cache_id)
            ORDER BY created_utc, cache_id;
            """;
        cmd.Parameters.AddWithValue("$k", placeKind);
        cmd.Parameters.AddWithValue("$r", placeRef);
        using var rows = cmd.ExecuteReader();
        var list = new List<RpgCorpseCacheRow>();
        while (rows.Read())
            list.Add(new RpgCorpseCacheRow(
                rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                rows.GetString(4), rows.IsDBNull(5) ? null : rows.GetString(5),
                rows.GetInt32(6) != 0, rows.GetInt64(7)));
        return list;
    }

    /// <summary>
    /// Public entry: the world-map caches legion <paramref name="entityId"/> can reach right now
    /// (spec §1a). Read-only — performs no write of any kind.
    /// </summary>
    public IReadOnlyList<RpgCorpseCacheRow> ListClaimableCaches(string worldId, string entityId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ListClaimableCachesUnlocked(db, null, worldId, entityId);
        }
    }

    // ---- §2a claim into legion cargo (Task 0.3b) ---------------------------------------------

    /// <summary>
    /// One legion's live position, read fresh — the shared §1a primitive behind both the list
    /// read above and the claim-time re-check below. <c>Found</c> is false for an unknown legion;
    /// a both-set/neither-set row violates <c>WorldState</c>'s own "at a sector, or on a lane —
    /// never both, never neither" invariant (<c>WorldState.cs:292-295</c>) and is unpositioned.
    /// </summary>
    internal (bool Found, string? AtSector, string? OnLane) ReadLegionPositionUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string worldId, string entityId)
    {
        using var pos = db.CreateCommand();
        pos.Transaction = tx;
        pos.CommandText = """
            SELECT at_sector_id, on_lane_id FROM rpg_world_entities
            WHERE world_id = $w AND entity_id = $e;
            """;
        pos.Parameters.AddWithValue("$w", worldId);
        pos.Parameters.AddWithValue("$e", entityId);
        using var r = pos.ExecuteReader();
        if (!r.Read())
            return (false, null, null);
        return (true, r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    /// <summary>
    /// Additive schema for the claim write (spec §Structure): the one replay-safety claim-log
    /// table, keyed generically enough for both claimant shapes — delve claims by
    /// <c>(delve_id, party_index)</c> (§Design 2, unbuilt), world-map claims by
    /// <c>(world_id, entity_id)</c> (§2a); each side leaves the other's columns at their
    /// sentinels. Never a second table (spec §Boundaries).
    /// </summary>
    void EnsureCacheClaimSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_corpse_cache_claim_log (
              cache_id       TEXT NOT NULL,
              delve_id       TEXT NOT NULL DEFAULT '',
              party_index    INTEGER NOT NULL DEFAULT -1,
              world_id       TEXT NOT NULL DEFAULT '',
              entity_id      TEXT NOT NULL DEFAULT '',
              correlation_id TEXT NOT NULL,
              claimed_utc    TEXT NOT NULL,
              result_json    TEXT NOT NULL,
              PRIMARY KEY (cache_id, delve_id, party_index, world_id, entity_id, correlation_id)
            );
            """);
    }

    /// <summary>
    /// The §1a reachability query re-run at claim time (spec §2a): a caller-supplied
    /// <paramref name="cacheId"/> is never trusted without this re-check — a stale/forged id from
    /// a legion no longer at the matching sector/lane refuses <c>cache.unreachable</c>. Only the
    /// two world-map kinds ever pass; delve/lawn/siege caches never enter cargo via this verb.
    /// </summary>
    internal bool IsCargoClaimReachableUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string worldId, string entityId, string cacheId)
    {
        string placeKind;
        string placeRef;
        using (var h = db.CreateCommand())
        {
            h.Transaction = tx;
            h.CommandText = """
                SELECT place_kind, place_ref, in_void FROM rpg_corpse_cache WHERE cache_id = $c;
                """;
            h.Parameters.AddWithValue("$c", cacheId);
            using var r = h.ExecuteReader();
            if (!r.Read())
                return false;
            placeKind = r.GetString(0);
            placeRef = r.GetString(1);
            if (r.GetInt32(2) != 0)
                return false;
        }

        var isSector = string.Equals(placeKind, "world_sector", StringComparison.Ordinal);
        var isLane = string.Equals(placeKind, "world_lane", StringComparison.Ordinal);
        if (!isSector && !isLane)
            return false;

        var (found, atSector, onLane) = ReadLegionPositionUnlocked(db, tx, worldId, entityId);
        if (!found)
            return false;
        // The same exactly-one-side invariant the list read enforces: a both-set row has no valid
        // live position, so it matches nothing rather than a guess.
        if (isSector && atSector is not null && onLane is null)
            return string.Equals(atSector, placeRef, StringComparison.Ordinal);
        if (isLane && onLane is not null && atSector is null)
            return string.Equals(onLane, placeRef, StringComparison.Ordinal);
        return false;
    }

    List<RpgCorpseCacheItemRow> ListCacheItemsUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string cacheId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT cache_id, seq, kind, instance_id, container_id, qty, origin_owner
            FROM rpg_corpse_cache_item WHERE cache_id = $id ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("$id", cacheId);
        using var r = cmd.ExecuteReader();
        var list = new List<RpgCorpseCacheItemRow>();
        while (r.Read())
            list.Add(new RpgCorpseCacheItemRow(
                r.GetString(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.GetString(6)));
        return list;
    }

    CorpseCacheCargoClaimResult? ReadCargoClaimUnlocked(
        SqliteConnection db, SqliteTransaction? tx,
        string cacheId, string worldId, string entityId, string correlationId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT result_json FROM rpg_corpse_cache_claim_log
            WHERE cache_id = $c AND delve_id = '' AND party_index = -1
              AND world_id = $w AND entity_id = $e AND correlation_id = $corr;
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        cmd.Parameters.AddWithValue("$corr", correlationId);
        var v = cmd.ExecuteScalar();
        if (v is null || v == DBNull.Value)
            return null;
        return JsonSerializer.Deserialize<CorpseCacheCargoClaimResult>((string)v);
    }

    void InsertCargoClaimUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string cacheId, string worldId, string entityId, string correlationId, string nowUtc,
        CorpseCacheCargoClaimResult result)
    {
        ExecInCounted(db, tx, """
            INSERT INTO rpg_corpse_cache_claim_log
              (cache_id, delve_id, party_index, world_id, entity_id, correlation_id, claimed_utc, result_json)
            VALUES ($c, '', -1, $w, $e, $corr, $now, $json);
            """,
            ("$c", cacheId), ("$w", worldId), ("$e", entityId), ("$corr", correlationId),
            ("$now", nowUtc), ("$json", JsonSerializer.Serialize(result)));
    }

    /// <summary>
    /// World-map claim into legion cargo (spec §2a): every surviving row of a reachable
    /// <c>world_sector</c>/<c>world_lane</c> cache moves into the claiming legion's own
    /// <c>rpg_world_entity_cargo</c>, subject per row, in <c>seq</c> order, to `legion-cargo`'s
    /// own weight/slot gates (reused, never redefined). One transaction, move-never-copy: each
    /// claimed row's cache-side <c>DELETE</c> and cargo-side <c>INSERT</c> commit together or not
    /// at all. A row that does not fit stays in the cache, uncleared, for a later claim — never
    /// a whole-claim refusal, never a partial-row insert. The caller owns the commit.
    ///
    /// <para><paramref name="weightEachFor"/> resolves the derived weight for one cache row —
    /// the single-unit weight for <c>kind='instance'</c>, the per-unit weight for
    /// <c>kind='stack'</c> (whose row weight is <c>qty × weightEach</c>) — the same
    /// caller-supplied-weight discipline <c>LoadCargoUnlocked</c> already uses (spec §Design 4:
    /// no per-item weight column exists anywhere in code, so weight arrives as a parameter and
    /// is snapshotted into the cargo row's <c>weight_each</c>, never re-resolved on read).
    /// </para>
    ///
    /// <para><paramref name="playerId"/> is accepted for call-shape parity with the delve claim;
    /// it gates nothing — the admission gate for this claim is reachability alone (spec §2a: no
    /// <c>cargo.not-owned</c> check; a cache item has no armoury-resident owner row, exactly as
    /// the delve claim needs no ownership check either).</para>
    /// </summary>
    internal CorpseCacheCargoClaimResult ClaimCorpseCacheIntoCargoUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, string entityId, long playerId,
        string cacheId, string correlationId, string nowUtc,
        Func<RpgCorpseCacheItemRow, long> weightEachFor)
    {
        // Documented unused-by-design (see above): reachability is the whole admission gate.
        _ = playerId;
        ArgumentNullException.ThrowIfNull(weightEachFor);
        var corr = (correlationId ?? "").Trim();
        if (corr.Length == 0)
            return new CorpseCacheCargoClaimResult(
                false, "correlation.missing", Array.Empty<int>(), Array.Empty<int>());

        EnsureCacheClaimSchemaUnlocked(db);

        // Replay safety first: the identical (cacheId, worldId, entityId, correlationId) returns
        // the SAME recorded result — never reprocessed.
        var replayed = ReadCargoClaimUnlocked(db, tx, cacheId, worldId, entityId, corr);
        if (replayed is not null)
            return replayed;

        if (!IsCargoClaimReachableUnlocked(db, tx, worldId, entityId, cacheId))
            return new CorpseCacheCargoClaimResult(
                false, "cache.unreachable", Array.Empty<int>(), Array.Empty<int>());

        var rows = ListCacheItemsUnlocked(db, tx, cacheId);
        if (rows.Count == 0)
        {
            var empty = new CorpseCacheCargoClaimResult(true, "ok", Array.Empty<int>(), Array.Empty<int>());
            InsertCargoClaimUnlocked(db, tx, cacheId, worldId, entityId, corr, nowUtc, empty);
            return empty;
        }

        var claimed = new List<int>();
        var skipped = new List<int>();
        foreach (var row in rows)
        {
            var weightEach = weightEachFor(row);
            if (weightEach < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(weightEachFor), "claim weight_each must be non-negative");
            var rowWeight = row.Kind == CorpseCacheItemKindStack
                ? checked((row.Qty ?? 0) * weightEach)
                : weightEach;

            // `legion-cargo`'s own gates, read fresh every row — reused, never redefined.
            if (checked(WeightUsedUnlocked(db, tx, worldId, entityId) + rowWeight)
                    > WeightCapacityUnlocked(db, worldId, entityId)
                || SlotsUsedUnlocked(db, tx, worldId, entityId) + 1
                    > SlotCapacityUnlocked(db, worldId, entityId))
            {
                // This ONE row refuses (`cargo.over-weight`/`cargo.no-slots` vocabulary is the
                // gate that fired; the refusal is recorded as a skip, not an error) and stays in
                // the cache, uncleared, for whoever reaches it next with room.
                skipped.Add(row.Seq);
                continue;
            }

            // The claim key at the item level: the DELETE's own row count IS the
            // mutual-exclusion result — no reservation table, no timing window. A racing
            // claimant's DELETE affects zero rows for any seq already taken here.
            var deleted = ExecInCounted(db, tx,
                "DELETE FROM rpg_corpse_cache_item WHERE cache_id = $c AND seq = $s;",
                ("$c", cacheId), ("$s", row.Seq));
            if (deleted == 0)
                continue; // another claimant already took this row — not this claim's error

            ExecInCounted(db, tx, """
                INSERT INTO rpg_world_entity_cargo
                  (world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each)
                VALUES ($w, $e, $s, $k, $iid, $cid, $q, $wt);
                """,
                ("$w", worldId), ("$e", entityId),
                ("$s", NextSeqUnlocked(db, tx, worldId, entityId)),
                ("$k", row.Kind),
                ("$iid", (object?)row.InstanceId ?? DBNull.Value),
                ("$cid", (object?)row.ContainerId ?? DBNull.Value),
                ("$q", (object?)row.Qty ?? DBNull.Value),
                ("$wt", weightEach));
            claimed.Add(row.Seq);
        }

        var result = new CorpseCacheCargoClaimResult(true, "ok", claimed, skipped);
        InsertCargoClaimUnlocked(db, tx, cacheId, worldId, entityId, corr, nowUtc, result);
        return result;
    }

    /// <summary>
    /// Public entry: the §2a world-map claim (spec §2a). One transaction; commits only on
    /// success (refusals write nothing, so there is nothing to commit).
    /// </summary>
    public CorpseCacheCargoClaimResult ClaimCorpseCacheIntoCargo(
        string worldId, string entityId, long playerId, string cacheId,
        string correlationId, Func<RpgCorpseCacheItemRow, long> weightEachFor)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = ClaimCorpseCacheIntoCargoUnlocked(
                db, tx, worldId, entityId, playerId, cacheId, correlationId,
                ServerClock.UtcNowDateTime.ToString("o"), weightEachFor);
            if (result.Ok)
                tx.Commit();
            return result;
        }
    }
}
