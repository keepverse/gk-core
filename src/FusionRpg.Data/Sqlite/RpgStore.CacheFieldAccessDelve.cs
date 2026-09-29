using System.Text.Json;
using System.Text.Json.Nodes;
using FusionRpg.Core.Delve.Pack;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

// deployment-hierarchy module 5 (`cache-field-access`), §Design 2 delve-claim verb (Task 4C.4 part 1).
// Spec: `docs/architecture/deployment-hierarchy/spec-cache-field-access.md` §2 (delve baggage into a
// `PackGrid`) + §Locked anchors (Active-only, certain-on-reach, move-never-copy) + §Boundaries.
//
// A separate file from `RpgStore.CacheFieldAccess.cs` (the §1a/§2a world-map read + cargo verb) on
// purpose: this verb's destination (a delve party's `PackGrid` + room floor) shares nothing with the
// cargo overlay except the replay-safety table, so it composes rather than edits.
//
// What this verb does, in one transaction (move-never-copy):
//   1. Replay check on the shared `rpg_corpse_cache_claim_log` (delve-keyed rows: delve_id +
//      party EntityId; world-keyed rows from §2a leave those columns at their sentinels).
//   2. Presence proof: the delve is `Active`, the cache is a live (`in_void = 0`) `delve_room` row,
//      and the claiming party's recorded route ends in the cache's room. A stale/forged cacheId, a
//      party standing elsewhere, or a closed raid refuses `cache.unreachable` with zero writes.
//   3. Placement via `PackArranger.Arrange` on the party's live grid — the SAME call an ordinary loot
//      reveal makes, never re-implemented here.
//   4. Per-row `DELETE` from `rpg_corpse_cache_item` (the DELETE's own row count IS the
//      mutual-exclusion result, spec §Design 2/3), `rpg_delve_pack_lock` rows for claimed instances,
//      the new grid into `parties_json`, overflow onto the room's `floor_json`, one `cache.claim`
//      entry appended to `decisions_json`, one claim-log row. Commit only on success.
//
// Refuse-or-spill, decided with evidence (spec §2 EXACT vs code, read fresh this session):
//   - Over-full (free rectangle nowhere, dimensions fit): SPILL to the floor. `PackArranger.Arrange`
//     (`gk-core/src/FusionRpg.Core/Delve/Pack/PackArranger.cs:13-31`) resolves EVERY input item to exactly one
//     of grid/floor (`floor.Add(item)` at :28) — spec §2's "never a third failed state" holds for this
//     case in code as written.
//   - Oversized (W > grid cols or H > grid rows): THROW `PackRejection`
//     (`pack.footprint-exceeds-grid`, `PackArranger.cs:22`). Spec §2's prose claims Arrange "always
//     resolves EVERY item", but the shipped code throws for this case — and there is no production
//     reveal caller to establish a catch-and-skip precedent (no `PackArranger.Arrange` caller exists
//     outside tests). This verb propagates the throw unchanged (the transaction rolls back: cache,
//     pack, floor, log all untouched), so a content error stays loud like a reveal's would, never a
//     silent skip or a partial move. Never a whole-claim refusal for fit: every row that resolves
//     leaves the cache.
//
// Presence source, decided with evidence: the party's `DelvePartyState.Route` last room — the same
// read `CloseDelve` already uses as a party's position (`RouteFacts(party.Route, rooms)` and
// `DelveDeathPlaceRef`, `RpgStore.Delve.cs:1021-1049`). `MoveParty`'s `WorldEntity.AtSectorId` write
// (`RpgStore.Delve.cs:347-350`) is keyed by a string entity id whose bridge to the delve party key
// (`DelvePartyState.EntityId`, a long matched by every `WriteParty*` writer) is the spec's own
// untraced gap (§Wiring gap: "almost certainly the bridge ... implementation's first task should
// confirm it"). This verb does not guess that bridge: parties are keyed by `EntityId` (the key every
// real `WriteParty*` writer uses), position by `Route` (the key every real settlement reader uses).
//
// Test-probe seam (spec §Real gap, first implementation task): a cache row carries no base-type/refId
// and `rpg_item` carries no footprint, so the `(RefId, W, H)` placement shape arrives via the
// caller-supplied `packShapeFor` delegate — the same caller-supplied-weight discipline
// `ClaimCorpseCacheIntoCargoUnlocked` already uses for `weightEachFor`. This module never builds a
// second footprint table (§Boundaries); loot-pack's own resolution plugs in here when wired.
//
// Retrieval-mission module 6 (`spec-cache-retrieval-mission.md`) is NOT built here — this verb only
// ever reads `in_void = 0` rows, module 6 targets only `in_void = 1` rows (spec §4), so the two never
// contend for the same cache.
///
/// <summary>One cache row offered to the pack-shape probe: the cache-side identity. The placement
/// shape (`RefId`/`W`/`H`) is resolved by the caller, never stored here.</summary>
public sealed record CorpseCachePackItem(
    string CacheId,
    int Seq,
    string Kind,
    string? InstanceId,
    string? ContainerId,
    long? Qty);

/// <summary>The caller-resolved placement shape for one cache row (spec §Real gap probe):
/// <c>RefId</c> is the resolved base type (instance) or container id (stack); <c>W</c>/<c>H</c> the
/// resolved footprint. <c>GrantIndex</c> is forced to the row's <c>seq</c> and <c>Origin</c> to
/// <c>Haul</c> by the verb itself, never taken from the probe.</summary>
public sealed record CorpseCachePackShape(
    string RefId,
    int W,
    int H,
    int? RarityOrdinal = null,
    int? ItemLevel = null);

/// <summary>One delve pack-claim result. <c>ClaimedSeqs</c> is every cache-side <c>seq</c> moved
/// (grid + floor); <c>PlacedSeqs</c>/<c>FloorSeqs</c> partition it. A replayed claim returns the
/// byte-identical record (compared as serialized JSON).</summary>
public sealed record CorpseCachePackClaimResult(
    bool Ok,
    string Reason,
    IReadOnlyList<int> ClaimedSeqs,
    IReadOnlyList<int> PlacedSeqs,
    IReadOnlyList<int> FloorSeqs);

public sealed partial class RpgStore
{
    // The one replay-safety table, shared with the §2a cargo verb (spec §Structure: one table keyed
    // generically, never a second table). Delve claims key by (delve_id, party EntityId) and leave the
    // world columns at their sentinels; cargo claims do the reverse. CREATE is idempotent, so this
    // Ensure is safe to run even when the sibling verb's file already created the table.
    static void EnsureDelveClaimSchemaUnlocked(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
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
            """;
        cmd.ExecuteNonQuery();
    }

    static CorpseCachePackClaimResult? ReadDelveClaimUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        long delveId, long partyEntityId, string cacheId, string correlationId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT result_json FROM rpg_corpse_cache_claim_log
            WHERE cache_id = $c AND delve_id = $d AND party_index = $p
              AND world_id = '' AND entity_id = '' AND correlation_id = $corr;
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$d", delveId.ToString());
        cmd.Parameters.AddWithValue("$p", partyEntityId);
        cmd.Parameters.AddWithValue("$corr", correlationId);
        var v = cmd.ExecuteScalar();
        if (v is null || v == DBNull.Value)
            return null;
        return JsonSerializer.Deserialize<CorpseCachePackClaimResult>((string)v);
    }

    static void InsertDelveClaimUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        long delveId, long partyEntityId, string cacheId, string correlationId, string nowUtc,
        CorpseCachePackClaimResult result)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache_claim_log
              (cache_id, delve_id, party_index, world_id, entity_id, correlation_id, claimed_utc, result_json)
            VALUES ($c, $d, $p, '', '', $corr, $now, $json);
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$d", delveId.ToString());
        cmd.Parameters.AddWithValue("$p", partyEntityId);
        cmd.Parameters.AddWithValue("$corr", correlationId);
        cmd.Parameters.AddWithValue("$now", nowUtc);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result));
        cmd.ExecuteNonQuery();
    }

    static string? ReadDelveJsonColumnTx(SqliteConnection db, SqliteTransaction tx, long delveId, string column)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        // Column is an internal constant at every call site (never caller input).
        cmd.CommandText = $"SELECT {column} FROM rpg_delves WHERE delve_id = $id;";
        cmd.Parameters.AddWithValue("$id", delveId);
        return cmd.ExecuteScalar() as string;
    }

    // The cache's room as a sector id. Production packing is DelveDeathPlaceRef's
    // "delve:{delveId}:r{r}:c{c}" (RpgStore.Delve.cs:1040-1049) resolved through rpg_delve_rooms; a
    // bare sector id (the spec §Wiring-gap reconciliation) is accepted when this delve has such a
    // room. Anything else — wrong delve, unknown room, malformed ref — is null (unreachable).
    static string? ResolveDelveCacheSectorUnlocked(
        SqliteConnection db, SqliteTransaction tx, long delveId, string placeRef)
    {
        if (placeRef.StartsWith("delve:", StringComparison.Ordinal))
        {
            var parts = placeRef.Split(':');
            if (parts.Length != 4
                || !long.TryParse(parts[1], out var refDelve) || refDelve != delveId
                || parts[2].Length < 2 || parts[2][0] != 'r' || !int.TryParse(parts[2][1..], out var r)
                || parts[3].Length < 2 || parts[3][0] != 'c' || !int.TryParse(parts[3][1..], out var c))
                return null;
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT sector_id FROM rpg_delve_rooms
                WHERE delve_id = $id AND row_index = $r AND col_index = $c;
                """;
            cmd.Parameters.AddWithValue("$id", delveId);
            cmd.Parameters.AddWithValue("$r", r);
            cmd.Parameters.AddWithValue("$c", c);
            return cmd.ExecuteScalar() as string;
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT 1 FROM rpg_delve_rooms WHERE delve_id = $id AND sector_id = $s;
                """;
            cmd.Parameters.AddWithValue("$id", delveId);
            cmd.Parameters.AddWithValue("$s", placeRef);
            if (cmd.ExecuteScalar() is null)
                return null;
        }
        return placeRef;
    }

    static List<CorpseCachePackItem> ListDelveCacheItemsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string cacheId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT cache_id, seq, kind, instance_id, container_id, qty
            FROM rpg_corpse_cache_item WHERE cache_id = $id ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("$id", cacheId);
        using var r = cmd.ExecuteReader();
        var list = new List<CorpseCachePackItem>();
        while (r.Read())
            list.Add(new CorpseCachePackItem(
                r.GetString(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt64(5)));
        return list;
    }

    static CorpseCachePackClaimResult PackRefusal(string reason) =>
        new(false, reason,
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

    /// <summary>
    /// Delve pack-claim (spec §Design 2): every surviving row of a reachable <c>delve_room</c> cache
    /// moves into the claiming party's live <c>PackGrid</c> via <c>PackArranger.Arrange</c>, overflow
    /// spilling onto the room's <c>floor_json</c> — never a whole-claim refusal for fit, never a lost
    /// item. One transaction, move-never-copy: each claimed row's cache-side <c>DELETE</c>, pack-lock
    /// <c>INSERT</c>, grid write and floor write commit together or not at all. The caller owns the
    /// commit.
    ///
    /// <para><paramref name="packShapeFor"/> is the spec §Real-gap probe: it resolves one cache row's
    /// placement shape (<c>RefId</c>/<c>W</c>/<c>H</c>). <c>GrantIndex</c> is forced to the row's
    /// <c>seq</c> and <c>Origin</c> to <c>Haul</c> here, so a claimed item is indistinguishable from
    /// ordinary haul the instant it is placed (spec §5).</para>
    ///
    /// <para>Refusals (<c>correlation.missing</c>, <c>cache.unreachable</c>) write nothing. An oversize
    /// footprint throws <c>PackRejection</c> like a reveal's would — the transaction rolls back, so
    /// the cache, pack, floor and log are all untouched.</para>
    /// </summary>
    internal CorpseCachePackClaimResult ClaimCorpseCacheIntoPackUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        long delveId, long partyEntityId,
        string cacheId, string correlationId, string nowUtc,
        Func<CorpseCachePackItem, CorpseCachePackShape> packShapeFor,
        int gridRows = 4, int gridCols = 10)
    {
        var corr = (correlationId ?? "").Trim();
        if (corr.Length == 0)
            return PackRefusal("correlation.missing");
        ArgumentNullException.ThrowIfNull(packShapeFor);
        if (gridRows <= 0) throw new ArgumentOutOfRangeException(nameof(gridRows));
        if (gridCols <= 0) throw new ArgumentOutOfRangeException(nameof(gridCols));

        EnsureDelveClaimSchemaUnlocked(db, tx);

        // Replay safety first: the identical (cacheId, delveId, party, correlationId) returns the
        // SAME recorded result — never reprocessed, never re-resolved.
        var replayed = ReadDelveClaimUnlocked(db, tx, delveId, partyEntityId, cacheId, corr);
        if (replayed is not null)
            return replayed;

        // Presence proof, all before any write. Delve-scope reads only (parties_json route), never
        // the untraced PartyIndex↔WorldEntity bridge (see file header).
        var state = ReadDelveJsonColumnTx(db, tx, delveId, "state");
        if (state is null || !string.Equals(state, "Active", StringComparison.Ordinal))
            return PackRefusal("cache.unreachable");
        var partiesJson = ReadDelveJsonColumnTx(db, tx, delveId, "parties_json") ?? "[]";

        string placeKind;
        string placeRef;
        bool inVoid;
        using (var h = db.CreateCommand())
        {
            h.Transaction = tx;
            h.CommandText = "SELECT place_kind, place_ref, in_void FROM rpg_corpse_cache WHERE cache_id = $c;";
            h.Parameters.AddWithValue("$c", cacheId);
            using var r = h.ExecuteReader();
            if (!r.Read())
                return PackRefusal("cache.unreachable");
            placeKind = r.GetString(0);
            placeRef = r.GetString(1);
            inVoid = r.GetInt32(2) != 0;
        }
        // Structural exclusion (spec §Locked anchors, §Boundaries): lawn/siege/world kinds never
        // enter a pack via this verb; voided rows belong to module 6, never this one.
        if (!string.Equals(placeKind, "delve_room", StringComparison.Ordinal) || inVoid)
            return PackRefusal("cache.unreachable");

        var sector = ResolveDelveCacheSectorUnlocked(db, tx, delveId, placeRef);
        if (sector is null)
            return PackRefusal("cache.unreachable");

        var parties = JsonNode.Parse(partiesJson) as JsonArray ?? new JsonArray();
        JsonObject? party = null;
        foreach (var node in parties.OfType<JsonObject>())
        {
            if (node["EntityId"]?.GetValue<long>() == partyEntityId)
            {
                party = node;
                break;
            }
        }
        var routeLast = (party?["Route"] as JsonArray)
            ?.Select(n => n?.GetValue<string>())
            .LastOrDefault(s => s is not null);
        if (party is null || !string.Equals(routeLast, sector, StringComparison.Ordinal))
            return PackRefusal("cache.unreachable");

        var rows = ListDelveCacheItemsUnlocked(db, tx, cacheId);
        if (rows.Count == 0)
        {
            var empty = new CorpseCachePackClaimResult(
                true, "ok",
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
            InsertDelveClaimUnlocked(db, tx, delveId, partyEntityId, cacheId, corr, nowUtc, empty);
            return empty;
        }

        // Build PackItems: identity from the row (source of truth), placement shape from the probe.
        var packItems = new List<PackItem>(rows.Count);
        foreach (var row in rows)
        {
            var shape = packShapeFor(row);
            if (shape is null) throw new ArgumentException("pack shape probe returned null", nameof(packShapeFor));
            if (string.IsNullOrWhiteSpace(shape.RefId))
                throw new ArgumentException($"pack shape probe returned no RefId for seq {row.Seq}", nameof(packShapeFor));
            if (shape.W <= 0 || shape.H <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(packShapeFor), $"pack shape probe returned non-positive footprint for seq {row.Seq}");
            packItems.Add(new PackItem(
                row.Kind, shape.RefId, row.InstanceId, row.Qty ?? 1,
                shape.W, shape.H, row.Seq, PackItemOrigin.Haul,
                shape.RarityOrdinal, shape.ItemLevel));
        }

        // The party's live grid: stored cells rebuilt at their anchors, or the shipped 4×10 starting
        // shape (spec-loot-pack.md §1) when this party has no pack yet.
        PackGrid grid;
        var packNode = party["Pack"] as JsonObject;
        if (packNode is null)
        {
            grid = new PackGrid(gridRows, gridCols);
        }
        else
        {
            var storedRows = packNode["Rows"]?.GetValue<int>() ?? gridRows;
            var storedCols = packNode["Cols"]?.GetValue<int>() ?? gridCols;
            var cells = JsonSerializer.Deserialize<List<PackCell>>(packNode["Cells"]?.ToJsonString() ?? "[]")
                ?? new List<PackCell>();
            grid = new PackGrid(storedRows, storedCols);
            foreach (var cell in cells)
                grid = grid.With(cell.Item, cell.Row, cell.Col);
        }

        // THE placement call — the same one an ordinary loot reveal makes, unmodified. Over-full
        // spills to the floor; oversize throws PackRejection (propagates, tx rolls back).
        var (newGrid, overflow) = PackArranger.Arrange(grid, packItems);

        // The claim key at the item level: each DELETE's own row count IS the mutual-exclusion
        // result — no reservation table, no timing window (spec §Design 2/3). Under the store's
        // single-writer lock no interleaving is possible; the check stays as defense in depth.
        var claimed = new List<int>(rows.Count);
        foreach (var row in rows)
        {
            using var del = db.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM rpg_corpse_cache_item WHERE cache_id = $c AND seq = $s;";
            del.Parameters.AddWithValue("$c", cacheId);
            del.Parameters.AddWithValue("$s", row.Seq);
            if (del.ExecuteNonQuery() == 0)
                continue; // another claimant already took this row — not this claim's error
            claimed.Add(row.Seq);
        }

        var floorSeqs = overflow.Select(i => i.GrantIndex).OrderBy(s => s).ToList();
        var floorSet = new HashSet<int>(floorSeqs);
        var placedSeqs = claimed.Where(s => !floorSet.Contains(s)).OrderBy(s => s).ToList();

        // A claimed instance is already owned (corpse-cache Locked anchor: rpg_item untouched by any
        // move) — only the pack-lock row is needed, reused unmodified (INSERT OR IGNORE, the same
        // statement LockPackInstanceUnlocked owns). No AcquireItem call, no assignment write, ever.
        foreach (var row in rows)
        {
            if (!claimed.Contains(row.Seq)
                || !string.Equals(row.Kind, "instance", StringComparison.Ordinal)
                || row.InstanceId is not { Length: > 0 } instanceId)
                continue;
            using var l = db.CreateCommand();
            l.Transaction = tx;
            l.CommandText = "INSERT OR IGNORE INTO rpg_delve_pack_lock(delve_id, instance_id) VALUES ($d, $i);";
            l.Parameters.AddWithValue("$d", delveId);
            l.Parameters.AddWithValue("$i", instanceId);
            l.ExecuteNonQuery();
        }

        // Grid write: surgical Pack replacement inside parties_json — every other party and every
        // other field (Route, Members, Haul, Pity) round-trips untouched.
        party["Pack"] = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            Rows = newGrid.Rows,
            Cols = newGrid.Cols,
            Cells = newGrid.Cells,
        }));
        using (var up = db.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "UPDATE rpg_delves SET parties_json = $j, revision = revision + 1 WHERE delve_id = $id;";
            up.Parameters.AddWithValue("$j", parties.ToJsonString());
            up.Parameters.AddWithValue("$id", delveId);
            up.ExecuteNonQuery();
        }

        // Floor write: overflow appends to the room's floor_json (the SAME column an ordinary
        // reveal's floor-overflow already uses), preserving whatever was already on the floor.
        List<PackItem> floor;
        using (var rf = db.CreateCommand())
        {
            rf.Transaction = tx;
            rf.CommandText = "SELECT floor_json FROM rpg_delve_rooms WHERE delve_id = $id AND sector_id = $s;";
            rf.Parameters.AddWithValue("$id", delveId);
            rf.Parameters.AddWithValue("$s", sector);
            floor = JsonSerializer.Deserialize<List<PackItem>>((rf.ExecuteScalar() as string) ?? "[]")
                ?? new List<PackItem>();
        }
        floor.AddRange(overflow);
        using (var wf = db.CreateCommand())
        {
            wf.Transaction = tx;
            wf.CommandText = "UPDATE rpg_delve_rooms SET floor_json = $j, revision = revision + 1 WHERE delve_id = $id AND sector_id = $s;";
            wf.Parameters.AddWithValue("$j", JsonSerializer.Serialize(floor));
            wf.Parameters.AddWithValue("$id", delveId);
            wf.Parameters.AddWithValue("$s", sector);
            wf.ExecuteNonQuery();
        }

        // Audit: one additive `cache.claim` entry on the generic decision log (spec §2). Inline
        // read-modify-write in THIS transaction — AppendDecision opens its own tx and cannot be
        // composed here. Raw object shape, so the closed DelveDecisionKinds vocabulary
        // (delve-battle-profile's own) is never edited for this module's additive kind.
        var decisionsRaw = ReadDelveJsonColumnTx(db, tx, delveId, "decisions_json") ?? "[]";
        var decisions = JsonSerializer.Deserialize<List<JsonElement>>(decisionsRaw) ?? new List<JsonElement>();
        var entry = new
        {
            seq = decisions.Count,
            kind = "cache.claim",
            partyIndex = partyEntityId,
            tick = (long?)null,
            payload = new
            {
                cacheId,
                claimedSeqs = claimed.ToArray(),
                placedGrid = placedSeqs.ToArray(),
                placedFloor = floorSeqs.ToArray(),
            },
        };
        var appended = decisions.Select(e => (object)e).Append(entry).ToList();
        using (var wd = db.CreateCommand())
        {
            wd.Transaction = tx;
            wd.CommandText = "UPDATE rpg_delves SET decisions_json = $j, revision = revision + 1 WHERE delve_id = $id;";
            wd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(appended));
            wd.Parameters.AddWithValue("$id", delveId);
            wd.ExecuteNonQuery();
        }

        var result = new CorpseCachePackClaimResult(
            true, "ok", claimed.OrderBy(s => s).ToList(), placedSeqs, floorSeqs);
        InsertDelveClaimUnlocked(db, tx, delveId, partyEntityId, cacheId, corr, nowUtc, result);
        return result;
    }

    /// <summary>
    /// Public entry: the §2 delve pack-claim. One transaction; commits only on success (refusals
    /// write nothing, so there is nothing to commit; a <c>PackRejection</c> throw rolls back).
    /// </summary>
    public CorpseCachePackClaimResult ClaimCorpseCacheIntoPack(
        long delveId, long partyEntityId, string cacheId, string correlationId,
        Func<CorpseCachePackItem, CorpseCachePackShape> packShapeFor,
        int gridRows = 4, int gridCols = 10)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = ClaimCorpseCacheIntoPackUnlocked(
                db, tx, delveId, partyEntityId, cacheId, correlationId,
                ServerClock.UtcNowDateTime.ToString("o"), packShapeFor, gridRows, gridCols);
            if (result.Ok)
                tx.Commit();
            return result;
        }
    }
}
