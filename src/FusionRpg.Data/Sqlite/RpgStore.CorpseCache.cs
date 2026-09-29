using FusionRpg.Core.Items;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

// deployment-hierarchy module 3 (`corpse-cache`), spec
// `docs/architecture/deployment-hierarchy/spec-corpse-cache.md` §Design 1 (+ §1a amendment).
// Header+contents family mirroring `rpg_delves`/`rpg_delve_rooms`: the cache's *place* and *decay
// state* live on the header row; *what's in it* is a separate, growable list. An item is "in a
// cache" purely by existing in `rpg_corpse_cache_item` with no corresponding `rpg_item_assignment`
// row. `rpg_item`'s own columns (including `disposition`, whose closed four is never extended) are
// left untouched by a move.

/// <summary>One place-pinned corpse-cache header.</summary>
public sealed record RpgCorpseCacheRow(
    string CacheId,
    string PlaceKind,
    string PlaceRef,
    string SourceKind,
    string CreatedUtc,
    string? DecayStartedUtc,
    bool InVoid,
    long Revision);

/// <summary>One item inside a corpse-cache. <c>Kind</c> is <c>'instance'</c> (rolled gear,
/// <c>InstanceId</c> set) or <c>'stack'</c> (fungible haul, <c>ContainerId</c>/<c>Qty</c> set).
/// Only <c>'instance'</c> rows are written by this module's death path today.</summary>
public sealed record RpgCorpseCacheItemRow(
    string CacheId,
    int Seq,
    string Kind,
    string? InstanceId,
    string? ContainerId,
    long? Qty,
    string OriginOwner);

public sealed partial class RpgStore
{
    // Closed vocabularies (spec §Design 1, §1a amendment; §Tunables: structural, not tunable —
    // the insert path refuses an unrecognized value rather than accepting free text).
    // `siege` is still unused until that program lands; `world_sector`/`world_lane` go live with
    // `scoped-inventory-hierarchy/cargo-fate`, not here.
    public static readonly IReadOnlySet<string> CorpseCachePlaceKinds =
        new HashSet<string>(StringComparer.Ordinal) { "lawn", "delve_room", "siege", "world_sector", "world_lane" };

    // Audit trail only — nothing downstream branches on this (spec §Boundaries: modules 4/5/6
    // treat every cache identically regardless of how it was created).
    public static readonly IReadOnlySet<string> CorpseCacheSourceKinds =
        new HashSet<string>(StringComparer.Ordinal) { "death", "wipe", "legion_death" };

    public const string CorpseCacheItemKindInstance = "instance";
    public const string CorpseCacheItemKindStack = "stack";

    void EnsureCorpseCacheSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_corpse_cache (
              cache_id      TEXT NOT NULL PRIMARY KEY,
              place_kind    TEXT NOT NULL,
              place_ref     TEXT NOT NULL,
              source_kind   TEXT NOT NULL,
              created_utc   TEXT NOT NULL,
              decay_started_utc TEXT,
              in_void       INTEGER NOT NULL DEFAULT 0,
              revision      INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS rpg_corpse_cache_item (
              cache_id     TEXT NOT NULL,
              seq          INTEGER NOT NULL,
              kind         TEXT NOT NULL,
              instance_id  TEXT,
              container_id TEXT,
              qty          INTEGER,
              origin_owner TEXT NOT NULL,
              PRIMARY KEY (cache_id, seq),
              FOREIGN KEY (cache_id) REFERENCES rpg_corpse_cache(cache_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_corpse_cache_item_instance ON rpg_corpse_cache_item(instance_id) WHERE instance_id IS NOT NULL;
            """);

        // deployment-hierarchy module 4 (`cache-decay-void`, spec-cache-decay-void.md §Design 1):
        // this module's own additive columns plus the per-(cache, tick) log table.
        EnsureCacheDecaySchemaUnlocked(db);
    }

    static void RequireCorpseCachePlaceKind(string placeKind)
    {
        if (!CorpseCachePlaceKinds.Contains(placeKind))
            throw new ArgumentException($"unknown corpse-cache place_kind '{placeKind}'", nameof(placeKind));
    }

    static void RequireCorpseCacheSourceKind(string sourceKind)
    {
        if (!CorpseCacheSourceKinds.Contains(sourceKind))
            throw new ArgumentException($"unknown corpse-cache source_kind '{sourceKind}'", nameof(sourceKind));
    }

    /// <summary>Place-keyed resolve-or-create (spec §Interface: modules 5/6 key off the identical
    /// <c>(place_kind, place_ref)</c> pair). Callable on an open connection/transaction; the caller
    /// owns the commit. Two specimens dying at the same place share one header row and never
    /// collide — contents differ by <c>seq</c>, not by a repointed assignment PK.</summary>
    internal string ResolveOrCreateCacheUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string placeKind, string placeRef, string sourceKind,
        long? ownerPlayerId = null)
    {
        RequireCorpseCachePlaceKind(placeKind);
        RequireCorpseCacheSourceKind(sourceKind);
        if (string.IsNullOrWhiteSpace(placeRef))
            throw new ArgumentException("place ref is required", nameof(placeRef));

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
                VALUES ($id, $k, $r, $s, $now, NULL, 0, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRef);
            ins.Parameters.AddWithValue("$s", sourceKind);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }
        // deployment-hierarchy module 4 (`cache-decay-void`, spec-cache-decay-void.md §Design 2):
        // the clock starts on a genuine INSERT only — never on the reuse branch above — reusing
        // the same `now` audit stamp. Null owner (owner-unknown callers) leaves the cache
        // clockless, module 3's pre-decay shape.
        if (ownerPlayerId.HasValue)
            TryStartDecayClockUnlocked(db, tx, cacheId, placeKind, ownerPlayerId.Value, now);
        return cacheId;
    }

    /// <summary>Move-on-<c>Retired</c> (spec §Design 2): delete every <c>rpg_item_assignment</c> row
    /// with a real rolled instance behind it (<c>ref_kind == "rolled"</c> — the shipped vocabulary
    /// in <see cref="EquipRefKinds"/>, not the spec sketch's <c>"instance"</c> literal) and insert
    /// one <c>rpg_corpse_cache_item(kind='instance')</c> row per moved assignment, in the caller's
    /// transaction. Stock-backed assignments (<c>ref_kind == "stock"</c>) are left in place — they
    /// pin no instance row to move. <c>rpg_player_item_assignment</c> (commander pouch) is never
    /// read or written here — the commander-death path is struck, spec Real gap. Returns the cache
    /// id, or <c>null</c> when nothing was movable (no empty cache row is ever created).</summary>
    internal string? MoveAssignedGearToCorpseCacheUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string specimenId,
        string placeKind, string placeRef, string sourceKind)
    {
        RequireCorpseCachePlaceKind(placeKind);
        RequireCorpseCacheSourceKind(sourceKind);

        var rolled = new List<(string Role, string RefId)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT role, ref_kind, ref_id FROM rpg_item_assignment WHERE specimen_id = $sid ORDER BY role;";
            cmd.Parameters.AddWithValue("$sid", specimenId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), EquipRefKinds.Rolled, StringComparison.Ordinal))
                    rolled.Add((r.GetString(0), r.GetString(2)));
            }
        }

        if (rolled.Count == 0)
            return null;

        // The specimen's own player owns the decay clock (spec-cache-decay-void.md §Real gap);
        // absent (never guessed) leaves the cache clockless.
        var cacheId = ResolveOrCreateCacheUnlocked(
            db, tx, placeKind, placeRef, sourceKind, ReadDecayOwnerUnlocked(db, tx, specimenId));
        foreach (var (role, refId) in rolled)
        {
            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM rpg_item_assignment WHERE specimen_id = $sid AND role = $role;";
                del.Parameters.AddWithValue("$sid", specimenId);
                del.Parameters.AddWithValue("$role", role);
                del.ExecuteNonQuery();
            }
            InsertCacheItemUnlocked(db, tx, cacheId, CorpseCacheItemKindInstance, refId, originOwner: specimenId);
        }
        return cacheId;
    }

    void InsertCacheItemUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string cacheId, string kind,
        string instanceId, string originOwner)
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
                VALUES ($id, $seq, $kind, $inst, NULL, NULL, $owner);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$seq", seq);
            ins.Parameters.AddWithValue("$kind", kind);
            ins.Parameters.AddWithValue("$inst", instanceId);
            ins.Parameters.AddWithValue("$owner", originOwner);
            ins.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RpgCorpseCacheRow> ListCorpseCaches()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision
                FROM rpg_corpse_cache ORDER BY created_utc, cache_id;
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<RpgCorpseCacheRow>();
            while (r.Read())
                list.Add(new RpgCorpseCacheRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6) != 0, r.GetInt64(7)));
            return list;
        }
    }

    public IReadOnlyList<RpgCorpseCacheItemRow> ListCorpseCacheItems(string cacheId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
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
    }
}
