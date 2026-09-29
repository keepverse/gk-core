using FusionRpg.Core.Items.Materials;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.World.Turn;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

// deployment-hierarchy module 6 (`cache-retrieval-mission`), Task 4C.4 part 2.
// Spec: `docs/architecture/deployment-hierarchy/spec-cache-retrieval-mission.md` (mission filing,
// resolution, reward as a live read of surviving rows, expiry/failure) + `spec-cache-decay-void.md`
// (the `in_void = 1`-only target contract, V2) + `spec-cache-field-access.md` §1a/§2 (the sibling
// shapes this module must not overlap: §1a/§2a read and write only `in_void = 0` rows).
//
// What this file builds, in one transaction per step (move-never-copy throughout):
//   1. Filing (`FileRetrievalMission`): a map-less mission naming a cache id, not a board (spec
//      Objective) — no squad-on-a-board, no position check, no `place_kind`/`source_kind` branch
//      (spec §Boundaries: never branch beyond the `in_void` filter). Filing refuses
//      `cache.not-void` for a still-reachable cache (spec Testing strategy, the `in_void` gate),
//      so this module and `cache-field-access` (delve pack verb + world cargo verb, both
//      `in_void = 0`-only) can never contend for the same row — the partition is by construction
//      of the void flag (spec §4), not by hoping the modules agree.
//   2. Resolution on world-turn commit (`ResolveRetrievalMissionsUnlocked`, called from
//      `CommitWorldTurn` after the cargo pass and before the decay tick — the same claim-then-decay
//      ordering the cargo pass already uses): pending missions whose due turn arrived run the
//      two-stage gate (spec §Locked anchors) — a mission-level pass/fail contest (the journey,
//      CAN fail), then certain-on-reach delivery (every surviving row transfers, no per-item roll).
//   3. Terminal states (closed four, `RetrievalMissionStates`): `succeeded` (contest pass — possibly
//      with zero rows, an honest empty haul when beaten to it, never an error), `failed` (contest
//      fail — cache untouched), `expired` (target header gone, or the delivery legion's row gone —
//      cache untouched). A `filed` mission the commit has not reached yet is simply pending.
//      (No routed-legion expiry: `TurnEngine.Step` unroutes every entity pre-resolution, so a routed
//      check here could never fire — dead branches are not shipped to prove a path.)
//
// Reuse map — this file calls, never redefines (spec §Design 7 "reuse, never duplicate move logic",
// adapted from the expedition-manifest shape to this worktree's commit-resolved world missions):
//   - `legion-cargo`'s own capacity functions (`WeightCapacityUnlocked`/`SlotCapacityUnlocked` +
//     `WeightUsedUnlocked`/`SlotsUsedUnlocked`/`NextSeqUnlocked`) and the exact
//     `rpg_world_entity_cargo` INSERT shape the §2a cargo verb uses — no second capacity formula.
//   - The commit path's server-side mass lookup (`TryResolveCargoWeight`, probe-backed) with the
//     §2a verb's own whole-act pre-flight discipline: one unknown row fails the mission with zero
//     writes, never a half-moved transfer.
//   - The item-level claim key (spec §Design 2/3): each cache-side DELETE's own row count IS the
//     mutual-exclusion result — two missions racing one cache partition its rows exactly once each,
//     no reservation table (a second claim table is explicitly NOT built: spec §4 keeps
//     table-per-module, and this module's own mission row is already its replay record).
//   - `SeededRng.DeriveStream`/`NextPerMille()` for the contest — never `System.Random`, never the
//     live-lawn `ICombatRng` sigmoid regime (spec §Design 5).
//
// Deliberately NOT built here (named, not smuggled):
//   - No souls/act price. The spec's souls fee is BLOCKED on the expedition program's sink ask
//     (map §External dependencies) and this worktree's act-pricing verbs (4A.8) do not name a
//     retrieval kind — pricing one here would invent a second debit site beside the budget-debit
//     seam. Filing and resolution spend nothing.
//   - No `origin_theta` producer write. The column is ensured here (spec §Design 2, additive —
//     buildable independent of the ask) and READ here; cache-creation sites do not stamp it yet,
//     so an unset origin reads as `SoulSinkPolicy.VanillaPvzTheta` (spec §Design 2's own explicit
//     placeholder for caches with no per-death depth signal), never a silent zero.
//   - No expedition-tier wall clock. The window is world turns (`cacheRetrieval.dueTurns`), the one legal
//     turn clock on this path — the mission resolves on the next commit of its own world.
//   - No report-replay path reads (`GetWorldTurnReport` replays `TurnEngine.Step` only): mission
//     rows live beside the hashed graph like every other SQL-overlay table, never inside it.
//
// SQL lives here, only here (guard-dal). No wall clock is read on the resolution path: `nowUtc`
// arrives from the commit; filing stamps are audit-only, never read by arithmetic.
//
/// <summary>One filed retrieval mission — the module-6 counterpart to the delve/cargo claim rows.
/// <c>ResolvedTurn</c>/<c>ResultJson</c> are set once, at the terminal transition; a replayed filing
/// returns this same row, never a second one.</summary>
public sealed record RetrievalMissionRow(
    string MissionId,
    string CacheId,
    string WorldId,
    string EntityId,
    long PlayerId,
    int RetrieverTheta,
    string State,
    int FiledTurn,
    int DueTurn,
    int? ResolvedTurn,
    string? ResultJson);

/// <summary>One mission's terminal outcome. <c>ClaimedSeqs</c>/<c>SkippedSeqs</c> are cache-side
/// <c>seq</c> values in ascending order; a contested failure or expiry carries empty lists and a
/// naming <c>Reason</c>, never an exception.</summary>
public sealed record RetrievalMissionOutcome(
    bool Ok,
    string Reason,
    IReadOnlyList<int> ClaimedSeqs,
    IReadOnlyList<int> SkippedSeqs);

/// <summary>Closed mission lifecycle (structural, not tunable — a fifth state is a reviewed add,
/// exactly like `place_kind`/`claimed_by_kind`).</summary>
public static class RetrievalMissionStates
{
    public const string Filed = "filed";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public sealed partial class RpgStore
{
    // The retrieval curve and its mission window live in config: `cacheRetrieval` in
    // gk-core/data/tuning/deployment-hierarchy.v5.json, read through DeploymentHierarchyTuningHub
    // (solid-enforcement SE3.14, 2026-09-18). Their own comment called them "starting shape, not
    // balance truth", which is exactly a balance pass's number. Values unchanged: base 500 (equal
    // Theta is a coin flip), Theta step 25, floor 50, cap 950, due 1 world turn (a mission filed at
    // open turn T resolves on the first commit reaching T + 1; a `filed` row before that is pending).

    // Spec §Design 2's explicit placeholder for caches carrying no per-death depth signal — reused,
    // never re-declared (an unset `origin_theta` reads as this, never a silent zero).
    static int RetrievalFallbackOriginTheta => SoulSinkPolicy.VanillaPvzTheta;

    /// <summary>Additive schema for this module (spec §Design 2 + §Design 7): the one mission table
    /// (filing record AND replay record AND terminal outcome — table-per-module, never shared with
    /// the field-access claim log) plus the `origin_theta` depth signal on the corpse-cache header.
    /// Idempotent — safe to run on every filing and every commit, so no `RpgStore.cs` Init edit is
    /// needed and no other file's schema hunk is touched.</summary>
    void EnsureCacheRetrievalSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_cache_retrieval_mission (
              mission_id      TEXT NOT NULL PRIMARY KEY,
              cache_id        TEXT NOT NULL,
              world_id        TEXT NOT NULL,
              entity_id       TEXT NOT NULL,
              player_id       INTEGER NOT NULL,
              retriever_theta INTEGER NOT NULL,
              state           TEXT NOT NULL,
              filed_turn      INTEGER NOT NULL,
              due_turn        INTEGER NOT NULL,
              resolved_turn   INTEGER,
              result_json     TEXT,
              filed_utc       TEXT NOT NULL
            );
            -- No FOREIGN KEY to `rpg_corpse_cache`: a mission row must OUTLIVE its target — the
            -- target-gone expiry proof (`Expired_when_target_header_deleted`) deletes the header,
            -- and a cascading FK would delete the mission under test instead of expiring it.
            -- Filing and resolution validate the header's existence explicitly.
            CREATE INDEX IF NOT EXISTS ix_rpg_cache_retrieval_mission_due
              ON rpg_cache_retrieval_mission(world_id, state, due_turn);
            """);
        // The cache's own depth signal, priced the way `DelvePrices.RecoveryRitual` prices off the
        // wounding delve's Theta (spec §Locked anchors) — stored once at cache-creation time so
        // contest reads never re-derive it from a place that may no longer exist. Producers do not
        // stamp it yet (named above); until they do it stays NULL and reads as the fallback.
        EnsureColumn(db, "rpg_corpse_cache", "origin_theta", "INTEGER");
    }

    // ---- pure contest (no SQL, no clock — unit-testable in isolation) ---------------------------

    /// <summary>The success curve, shape-only (spec §Design 5): a bounded per-mille ratio over the
    /// retriever-minus-origin delta, floored and capped. `checked` — integer overflow throws, never
    /// wraps (repo numeric rule).</summary>
    internal static long RetrievalSuccessMilli(int retrieverTheta, int originTheta)
    {
        var delta = checked((long)retrieverTheta - originTheta);
        var t = DeploymentHierarchyTuningHub.Tuning.CacheRetrieval;
        var milli = checked(t.BaseMilli + delta * t.ThetaStepMilli);
        return Math.Min(t.CapMilli, Math.Max(t.FloorMilli, milli));
    }

    /// <summary>One deterministic contest draw: the same
    /// <c>(worldSeed, missionId, dueTurn)</c> always agrees with itself (spec Testing strategy,
    /// contest determinism) — the `tick:{t}` seeded-stream regime, never a shared RNG.</summary>
    internal static bool RetrievalContestPass(ulong worldSeed, string missionId, int dueTurn, long successMilli)
    {
        var stream = SeededRng.DeriveStream(worldSeed, $"cache-retrieval:{missionId}:{dueTurn}");
        return stream.NextPerMille() < successMilli;
    }

    // ---- filing ---------------------------------------------------------------------------------

    static RetrievalMissionRow? ReadRetrievalMissionUnlocked(SqliteConnection db, SqliteTransaction? tx, string missionId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT mission_id, cache_id, world_id, entity_id, player_id, retriever_theta,
                   state, filed_turn, due_turn, resolved_turn, result_json
            FROM rpg_cache_retrieval_mission WHERE mission_id = $m;
            """;
        cmd.Parameters.AddWithValue("$m", missionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new RetrievalMissionRow(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            r.GetInt32(5), r.GetString(6), r.GetInt32(7), r.GetInt32(8),
            r.IsDBNull(9) ? null : r.GetInt32(9),
            r.IsDBNull(10) ? null : r.GetString(10));
    }

    /// <summary>
    /// Files a retrieval mission against a voided cache (spec §Design 3, adapted from the
    /// expedition-dispatch shape to a commit-resolved world mission): the target is a cache id, not
    /// a board — no position check, no place/source branch. Refuses <c>cache.not-void</c> for a
    /// still-reachable cache (the `in_void` gate), so field access and retrieval never contend.
    /// <paramref name="retrieverTheta"/> is snapshotted here (spec §Design 5: read once at dispatch,
    /// never re-read at resolve). One transaction; commits only on success (refusals write nothing).
    /// </summary>
    public (bool Ok, string Reason, RetrievalMissionRow? Mission) FileRetrievalMission(
        string worldId, string entityId, string cacheId, string missionId, int retrieverTheta)
    {
        var corr = (missionId ?? "").Trim();
        if (corr.Length == 0)
            return (false, "correlation.missing", null);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            EnsureCacheRetrievalSchemaUnlocked(db);

            // Replay safety first (the `DispatchExpedition` correlation precedent): the identical
            // mission id returns the SAME recorded row — never a second mission.
            var replayed = ReadRetrievalMissionUnlocked(db, tx, corr);
            if (replayed is not null)
                return (true, "replay", replayed);

            int openTurn;
            using (var w = db.CreateCommand())
            {
                w.Transaction = tx;
                w.CommandText = "SELECT current_turn FROM rpg_worlds WHERE world_id = $w;";
                w.Parameters.AddWithValue("$w", worldId);
                var v = w.ExecuteScalar();
                if (v is null || v == DBNull.Value)
                    return (false, "world.unknown", null);
                openTurn = Convert.ToInt32(v);
            }

            // The one adversarial read this module owns: the header must exist AND already be voided.
            // No `place_kind`/`source_kind` read, no branch — lawn, delve-room, siege, death, wipe,
            // and legion-sourced rows are all equally eligible once voided, and `world_sector`/
            // `world_lane` rows are structurally excluded BY DATA (they never reach `in_void = 1`
            // under the V5 amendment), never by a kind filter here.
            using (var h = db.CreateCommand())
            {
                h.Transaction = tx;
                h.CommandText = "SELECT in_void FROM rpg_corpse_cache WHERE cache_id = $c;";
                h.Parameters.AddWithValue("$c", cacheId);
                var v = h.ExecuteScalar();
                if (v is null || v == DBNull.Value)
                    return (false, "cache.unknown", null);
                if (Convert.ToInt32(v) == 0)
                    return (false, "cache.not-void", null);
            }

            using (var e = db.CreateCommand())
            {
                e.Transaction = tx;
                e.CommandText = "SELECT 1 FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;";
                e.Parameters.AddWithValue("$w", worldId);
                e.Parameters.AddWithValue("$e", entityId);
                if (e.ExecuteScalar() is null)
                    return (false, "entity.gone", null);
            }

            // The world's one player row (the `CargoResolveUnlocked` precedent: derived mid-path,
            // never from the wire). A missing row is impossible with a live world header above.
            var playerId = ReadWorldPlayerUnlocked(db, tx, worldId)
                ?? throw new InvalidOperationException($"Retrieval filing: no player row for world '{worldId}'.");

            var dueTurn = openTurn + DeploymentHierarchyTuningHub.Tuning.CacheRetrieval.DueTurns;
            var nowUtc = ServerClock.UtcNowDateTime.ToString("o");
            using (var ins = db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO rpg_cache_retrieval_mission
                      (mission_id, cache_id, world_id, entity_id, player_id, retriever_theta,
                       state, filed_turn, due_turn, resolved_turn, result_json, filed_utc)
                    VALUES ($m, $c, $w, $e, $p, $theta, $st, $filed, $due, NULL, NULL, $now);
                    """;
                ins.Parameters.AddWithValue("$m", corr);
                ins.Parameters.AddWithValue("$c", cacheId);
                ins.Parameters.AddWithValue("$w", worldId);
                ins.Parameters.AddWithValue("$e", entityId);
                ins.Parameters.AddWithValue("$p", playerId);
                ins.Parameters.AddWithValue("$theta", retrieverTheta);
                ins.Parameters.AddWithValue("$st", RetrievalMissionStates.Filed);
                ins.Parameters.AddWithValue("$filed", openTurn);
                ins.Parameters.AddWithValue("$due", dueTurn);
                ins.Parameters.AddWithValue("$now", nowUtc);
                ins.ExecuteNonQuery();
            }

            tx.Commit();
            return (true, "ok", new RetrievalMissionRow(
                corr, cacheId, worldId, entityId, playerId, retrieverTheta,
                RetrievalMissionStates.Filed, openTurn, dueTurn, null, null));
        }
    }

    /// <summary>Public read: one mission by id, or null. Read-only — performs no write of any kind.</summary>
    public RetrievalMissionRow? TryGetRetrievalMission(string missionId)
    {
        var corr = (missionId ?? "").Trim();
        if (corr.Length == 0)
            return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureCacheRetrievalSchemaUnlocked(db);
            return ReadRetrievalMissionUnlocked(db, null, corr);
        }
    }

    // ---- resolution on commit -------------------------------------------------------------------

    static int? ReadRetrievalOriginUnlocked(SqliteConnection db, SqliteTransaction tx, string cacheId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT origin_theta FROM rpg_corpse_cache WHERE cache_id = $c;";
        cmd.Parameters.AddWithValue("$c", cacheId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? null : Convert.ToInt32(v);
    }

    static List<(string MissionId, string CacheId, string EntityId, int RetrieverTheta, int DueTurn)>
        ListDueRetrievalMissionsUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId, int newTurn)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT mission_id, cache_id, entity_id, retriever_theta, due_turn
            FROM rpg_cache_retrieval_mission
            WHERE world_id = $w AND state = $st AND due_turn <= $t
            ORDER BY mission_id;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$st", RetrievalMissionStates.Filed);
        cmd.Parameters.AddWithValue("$t", newTurn);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string, int, int)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4)));
        return list;
    }

    void FinishRetrievalMissionUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string missionId, string state, int resolvedTurn, RetrievalMissionOutcome outcome)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE rpg_cache_retrieval_mission
            SET state = $st, resolved_turn = $t, result_json = $json
            WHERE mission_id = $m;
            """;
        cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$t", resolvedTurn);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(outcome));
        cmd.Parameters.AddWithValue("$m", missionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Resolves every due mission of one world inside the commit's own transaction (spec §Design
    /// 4/5, adapted): the contest first (the journey — can fail, cache untouched), then
    /// certain-on-reach delivery (every surviving row, no second roll). Runs after the cargo pass
    /// and before the decay tick — a retrieved row is gone before decay sees it; a skipped row
    /// remains and decays normally. The caller owns the commit. Returns missions resolved.
    /// </summary>
    internal int ResolveRetrievalMissionsUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, int newTurn, ulong worldSeed, string nowUtc, TurnReport report)
    {
        EnsureCacheRetrievalSchemaUnlocked(db);
        var due = ListDueRetrievalMissionsUnlocked(db, tx, worldId, newTurn);
        if (due.Count == 0)
            return 0;

        var resolved = 0;
        foreach (var (missionId, cacheId, entityId, retrieverTheta, dueTurn) in due)
        {
            // Target re-check at resolve time (never trusted from filing): a vanished header, or a
            // header that somehow left the void, expires the mission — never a crash, never a grant
            // from a cache field access could still reach (the partition invariant, re-proved here).
            bool targetVoided;
            using (var h = db.CreateCommand())
            {
                h.Transaction = tx;
                h.CommandText = "SELECT in_void FROM rpg_corpse_cache WHERE cache_id = $c;";
                h.Parameters.AddWithValue("$c", cacheId);
                var v = h.ExecuteScalar();
                targetVoided = v is not null && v != DBNull.Value && Convert.ToInt32(v) != 0;
            }
            if (!targetVoided)
            {
                var gone = new RetrievalMissionOutcome(false, "target.gone",
                    Array.Empty<int>(), Array.Empty<int>());
                FinishRetrievalMissionUnlocked(db, tx, missionId, RetrievalMissionStates.Expired, newTurn, gone);
                report.AddPostStep(TurnEngine.Phases.Snapshot, TurnReportKinds.Event, missionId, "retrieval.expired:target.gone");
                resolved++;
                continue;
            }

            // Delivery re-check: the filing legion's row must still exist — a mission whose
            // carrier is gone has nowhere to land, so it expires with the cache untouched (the
            // `entity.gone` vocabulary every other resolver already uses).
            bool legionGone;
            using (var e = db.CreateCommand())
            {
                e.Transaction = tx;
                e.CommandText = "SELECT 1 FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;";
                e.Parameters.AddWithValue("$w", worldId);
                e.Parameters.AddWithValue("$e", entityId);
                legionGone = e.ExecuteScalar() is null;
            }
            if (legionGone)
            {
                var expired = new RetrievalMissionOutcome(false, "entity.gone",
                    Array.Empty<int>(), Array.Empty<int>());
                FinishRetrievalMissionUnlocked(db, tx, missionId, RetrievalMissionStates.Expired, newTurn, expired);
                report.AddPostStep(TurnEngine.Phases.Snapshot, TurnReportKinds.Event, missionId, "retrieval.expired:entity.gone");
                resolved++;
                continue;
            }

            // The journey: one deterministic per-mille draw of retriever-Theta against the cache's
            // own origin depth (spec §Locked anchors — origin, never the retriever's own depth for
            // the price side; the contest reads both). A fail spends the mission and leaves the
            // cache untouched — the priced-shape bite, minus the price this module does not own.
            var originTheta = ReadRetrievalOriginUnlocked(db, tx, cacheId) ?? RetrievalFallbackOriginTheta;
            var milli = RetrievalSuccessMilli(retrieverTheta, originTheta);
            if (!RetrievalContestPass(worldSeed, missionId, dueTurn, milli))
            {
                var failed = new RetrievalMissionOutcome(false, "contest.failed",
                    Array.Empty<int>(), Array.Empty<int>());
                FinishRetrievalMissionUnlocked(db, tx, missionId, RetrievalMissionStates.Failed, newTurn, failed);
                report.AddPostStep(TurnEngine.Phases.Snapshot, TurnReportKinds.Event, missionId, "retrieval.failed");
                resolved++;
                continue;
            }

            // Certain-on-reach (spec §Locked anchors): whatever decay has not yet taken transfers —
            // proven below by the absence of any second `NextPerMille` between this contest and the
            // last row moved. An empty cache is an honest empty haul, never an error.
            var rows = ListCacheItemsUnlocked(db, tx, cacheId);
            if (rows.Count == 0)
            {
                var empty = new RetrievalMissionOutcome(true, "ok", Array.Empty<int>(), Array.Empty<int>());
                FinishRetrievalMissionUnlocked(db, tx, missionId, RetrievalMissionStates.Succeeded, newTurn, empty);
                report.AddPostStep(TurnEngine.Phases.Snapshot, TurnReportKinds.Event, missionId, "cache.retrieved:0+0");
                resolved++;
                continue;
            }

            // Mass pre-flight BEFORE the loop (the `ResolveClaimUnlocked` precedent): every row
            // resolved up front, so one unknown row fails the mission with zero writes — never a
            // half-moved transfer. `nowUtc` is unused here; kept out (no audit write needs it).
            _ = nowUtc;
            var weights = new Dictionary<int, long>(rows.Count);
            string? weightMiss = null;
            foreach (var row in rows)
            {
                var (found, weightEach) = TryResolveCargoWeight(
                    new CargoWeightKey(row.Kind, row.InstanceId, row.ContainerId));
                if (!found)
                {
                    weightMiss = "cargo.weight-unknown";
                    break;
                }
                weights[row.Seq] = weightEach;
            }
            if (weightMiss is not null)
            {
                var refused = new RetrievalMissionOutcome(false, weightMiss,
                    Array.Empty<int>(), Array.Empty<int>());
                FinishRetrievalMissionUnlocked(db, tx, missionId, RetrievalMissionStates.Failed, newTurn, refused);
                report.AddPostStep(TurnEngine.Phases.Snapshot, TurnReportKinds.Event, missionId, $"retrieval.failed:{weightMiss}");
                resolved++;
                continue;
            }

            // The delivery loop — `legion-cargo`'s own gates row by row (`ClaimCorpseCacheIntoCargo-
            // Unlocked`'s exact discipline, reused through the same functions, never re-derived): a
            // row that fits moves (cache DELETE + cargo INSERT, one tx); a row that does not fit
            // stays uncleared for whoever reaches the cache next — never a whole-claim refusal,
            // never a partial-row insert. The DELETE's own row count IS the mutual-exclusion result
            // for missions racing one cache (spec §Design 2/3).
            var claimed = new List<int>();
            var skipped = new List<int>();
            foreach (var row in rows)
            {
                var weightEach = weights[row.Seq];
                var rowWeight = string.Equals(row.Kind, CorpseCacheItemKindStack, StringComparison.Ordinal)
                    ? checked((row.Qty ?? 0) * weightEach)
                    : weightEach;

                if (checked(WeightUsedUnlocked(db, tx, worldId, entityId) + rowWeight)
                        > WeightCapacityUnlocked(db, worldId, entityId)
                    || SlotsUsedUnlocked(db, tx, worldId, entityId) + 1
                        > SlotCapacityUnlocked(db, worldId, entityId))
                {
                    skipped.Add(row.Seq);
                    continue;
                }

                var deleted = ExecInCounted(db, tx,
                    "DELETE FROM rpg_corpse_cache_item WHERE cache_id = $c AND seq = $s;",
                    ("$c", cacheId), ("$s", row.Seq));
                if (deleted == 0)
                    continue; // another claimant already took this row — not this mission's error

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

            var outcome = new RetrievalMissionOutcome(true, "ok", claimed, skipped);
            FinishRetrievalMissionUnlocked(db, tx, missionId, RetrievalMissionStates.Succeeded, newTurn, outcome);
            report.AddPostStep(TurnEngine.Phases.Snapshot, TurnReportKinds.Event, missionId,
                $"cache.retrieved:{claimed.Count}+{skipped.Count}");
            resolved++;
        }

        return resolved;
    }
}
