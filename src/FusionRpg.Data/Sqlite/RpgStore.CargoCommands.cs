using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

// empire-inventory-surfaces module 1 (`cargo-commands`),
// `docs/architecture/empire-inventory-surfaces/spec-cargo-commands.md` §Design 4-6.
//
// The Data-side post-Step pass: every filed cargo/cache order resolves here, inside the commit
// transaction `CommitWorldTurn` already opens, after `DiffWorldGraphUnlocked` (so every verb reads
// live post-turn positions/owners) and before the turn-log insert (so outcome entries land in the
// stored hot-tail report) and before the decay tick (claim-then-decay). `TurnEngine.Step` is never
// entered: Core owns kinds, payload, and admission only — never a DB read.
//
// Every kind resolves through the existing `*Unlocked` verbs (§Design 4 table), which already
// delete+insert atomically; this file calls them, never reimplements them, and reuses the shared
// capacity helpers through them (D1 locked — this file issues no per-table tally reads of its
// own; the D1 guard test pins that). SQL lives here, only here (guard-dal).
public sealed partial class RpgStore
{
    // ---- weightEachFor: implementation's first task (spec §Design 4 honest gap) -----------------
    //
    // No per-item mass column exists anywhere in code (RpgStore.LegionCargo.cs:30-35), so the load
    // verbs take `weightEach` as an explicit caller-supplied parameter — and on the command path
    // there is no caller left to supply it (mass is never on the wire, spec §Design 2). This named
    // lookup is that caller: the resolver consults it at resolve time and snapshots the answer
    // into the cargo row, the same snapshot discipline every direct verb already applies.
    //
    // Until a real mass source ships, the only supplier is the test probe below. A miss refuses
    // loudly (`cargo.weight-unknown`) at the call site, never zero-fills (D3 locked): every arm
    // resolves its mass BEFORE invoking its verb, so a miss writes nothing.
    //
    // `claim-endpoints` (Task 4A.3) consumes this lookup: its filer passes no mass and resolves
    // none — the commit path resolves through here.

    /// <summary>
    /// What a mass lookup needs: the overlay row shape (`'instance'` | `'stack'`), with the
    /// identity the row already carries. `InstanceId` set for `'instance'` rows, `ContainerId`
    /// for `'stack'` rows. The answer is the single-unit mass for `'instance'`, the per-unit mass
    /// for `'stack'` (whose row mass is `qty × answer`, the same arithmetic the verbs apply).
    /// </summary>
    internal sealed record CargoWeightKey(string Kind, string? InstanceId, string? ContainerId);

    /// <summary>
    /// Test probe (approach.test_probe_over_impossible): the mass source until a real one ships.
    /// Tests set this to resolve fixture identities; production never sets it, so production
    /// lookups miss loudly rather than inventing a mass. A null answer or a negative one both
    /// read as "unknown" — never as zero. Reset by each suite that sets it.
    /// </summary>
    internal static Func<CargoWeightKey, long?>? TestCargoWeightProbe;

    /// <summary>
    /// The named server-side mass lookup (spec §Design 4, D3). Pure today: consults the probe,
    /// nothing else. A future mass source plugs in here; every arm keeps calling this one
    /// function, so none of them changes when it lands.
    /// </summary>
    internal (bool Found, long WeightEach) TryResolveCargoWeight(CargoWeightKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var probed = TestCargoWeightProbe?.Invoke(key);
        if (probed is not { } w || w < 0)
            return (false, 0);
        return (true, w);
    }

    // ---- debit site for world-action-economy `budget-debit` (spec-budget-debit.md §Design 2) --
    //
    // Costs are tunables owned by `act-price-table`, read here through `WorldTuningHub` like
    // `BudgetFor` reads dowse. No `const`, no second file, no parallel loader. Same-tx,
    // gate-before-write: the refill already landed (post-Step post-Diff), so this spends next
    // turn's march — never this turn's leftover, never a reservation.

    static int CostForKind(string kind)
    {
        var mv = WorldTuningHub.Tuning.Movement;
        if (string.Equals(kind, WorldCommandKinds.ClaimCache, StringComparison.Ordinal))
            return mv.ClaimCostMilli;
        if (string.Equals(kind, WorldCommandKinds.DepositCargo, StringComparison.Ordinal))
            return mv.DepositCostMilli;
        if (string.Equals(kind, WorldCommandKinds.WithdrawCargo, StringComparison.Ordinal))
            return mv.WithdrawCostMilli;
        if (string.Equals(kind, WorldCommandKinds.LoadCargo, StringComparison.Ordinal))
            return mv.LoadCostMilli;
        if (string.Equals(kind, WorldCommandKinds.UnloadCargo, StringComparison.Ordinal))
            return mv.UnloadCostMilli;
        return 0;
    }

    internal int ReadActBudgetUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT movement_remaining FROM rpg_world_entities
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        var v = cmd.ExecuteScalar();
        if (v is null || v == DBNull.Value)
            throw new InvalidOperationException(
                $"Act debit: no budget row for '{entityId}' in '{worldId}'.");
        return Convert.ToInt32(v);
    }

    static void WriteActBudgetUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId, int remaining)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE rpg_world_entities SET movement_remaining = $m
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$m", remaining);
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(
                $"Act debit: budget write missed '{entityId}' in '{worldId}'.");
    }

    /// <summary>
    /// The one named debit seam (cargo-commands §Design 6, filled by `budget-debit` §Design 2).
    /// Lookup via `WorldTuningHub`, subtract checked, same tx. Debit-after-refill: the DB row
    /// already holds the refilled next-turn budget, so an act spends next turn's march.
    /// Short budget refuses `entity.spent` with nothing written.
    /// </summary>
    internal (bool Ok, string Reason) DebitActCostUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId, string kind)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(worldId);
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(kind);
        var cost = CostForKind(kind);
        if (cost == 0)
            return (true, "ok");
        var remaining = ReadActBudgetUnlocked(db, tx, worldId, entityId);
        if (remaining < cost)
            return (false, "entity.spent");
        long next = checked((long)remaining - (long)cost);
        WriteActBudgetUnlocked(db, tx, worldId, entityId, checked((int)next));
        return (true, "ok");
    }

    // ---- debit-once log: one row per priced resolution, same tx as the move ------------------
    //
    // Keyed (world_id, turn, command_id) — the same idempotency key the submit path and the
    // claim log already use (`correlationId := CommandId`). A second arrival of a recorded
    // `CommandId` is a read-back, never a second spend. Never trimmed: the command log is
    // never trimmed, so post-trim re-derivation still finds the rows it needs to converge.
    // Trimming reports without this log would strand a priced turn with no way to replay it —
    // named here so a future trim change cannot assume the log away.

    void EnsureActDebitSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_world_act_debit (
              world_id TEXT NOT NULL,
              turn INTEGER NOT NULL,
              command_id TEXT NOT NULL,
              entity_id TEXT,
              cost INTEGER NOT NULL,
              ok INTEGER NOT NULL,
              detail TEXT NOT NULL,
              sector_id TEXT,
              PRIMARY KEY (world_id, turn, command_id)
            );
            """);
        EnsureColumn(db, "rpg_world_act_debit", "entity_id", "TEXT");
    }

    sealed record ActDebitRow(int Cost, bool Ok, string Detail, string? SectorId, string? EntityId);

    ActDebitRow? ReadActDebitUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, string commandId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT cost, ok, detail, sector_id, entity_id FROM rpg_world_act_debit
            WHERE world_id = $w AND turn = $t AND command_id = $id;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$t", turn);
        cmd.Parameters.AddWithValue("$id", commandId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new ActDebitRow(
            r.GetInt32(0), r.GetInt32(1) != 0, r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4));
    }

    void InsertActDebitUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, string commandId,
        string? entityId, int cost, bool ok, string detail, string? sectorId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO rpg_world_act_debit
              (world_id, turn, command_id, entity_id, cost, ok, detail, sector_id)
            VALUES ($w, $t, $id, $e, $cost, $ok, $detail, $sector);
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$t", turn);
        cmd.Parameters.AddWithValue("$id", commandId);
        cmd.Parameters.AddWithValue("$e", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cost", cost);
        cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.Parameters.AddWithValue("$sector", (object?)sectorId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    internal IReadOnlyList<(string CommandId, string? EntityId, int Cost)> ListActDebitsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT command_id, entity_id, cost FROM rpg_world_act_debit
            WHERE world_id = $w AND turn = $t AND ok = 1
            ORDER BY command_id;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$t", turn);
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, string?, int)>();
        while (r.Read())
            rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2)));
        return rows;
    }

    // ---- the pass -------------------------------------------------------------------------------

    /// <summary>Whether this kind resolves through the cargo pass (the six §Design 1 kinds).</summary>
    internal static bool IsCargoCommandKind(string? kind) =>
        string.Equals(kind, WorldCommandKinds.LoadCargo, StringComparison.Ordinal)
        || string.Equals(kind, WorldCommandKinds.UnloadCargo, StringComparison.Ordinal)
        || string.Equals(kind, WorldCommandKinds.TransferCargo, StringComparison.Ordinal)
        || string.Equals(kind, WorldCommandKinds.DepositCargo, StringComparison.Ordinal)
        || string.Equals(kind, WorldCommandKinds.WithdrawCargo, StringComparison.Ordinal)
        || string.Equals(kind, WorldCommandKinds.ClaimCache, StringComparison.Ordinal);

    /// <summary>
    /// Resolves every cargo/cache order in stable (commander, command) order — `Reveal`'s own
    /// order (`TurnEngine.cs:194-197`), never insertion order, or two clients racing to submit
    /// would change a turn's outcome. Success appends an `Event` entry with the §Design 4 detail
    /// string; refusal appends `CommandDropped` with the verb's reason verbatim, so the wave-2
    /// fold authors copy against a closed list. Entries carry the `Snapshot` phase (settlement
    /// after every phase ran — the phase sector-claims already settle in). Runs inside the
    /// commit's own transaction: a mid-pass crash rolls back the whole turn.
    ///
    /// <para>Fast path: a log with none of the six kinds (every old log — submit previously
    /// refused them as `kind.unknown`) returns with zero reads and zero writes, which is the
    /// mechanical half of the no-bump proof (zero-priced logs stay identical across the bump;
    /// the re-hash below is a fixed point when no debit fires).</para>
    /// </summary>
    internal void CargoResolveUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn,
        IReadOnlyList<WorldCommand> commands, TurnReport report, string nowUtc)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(worldId);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(nowUtc);

        var cargo = commands.Where(c => IsCargoCommandKind(c.Kind)).ToList();
        if (cargo.Count == 0)
            return;

        EnsureActDebitSchemaUnlocked(db);

        // Server-side derived, never from the wire (D2 recorded): the world's one player row.
        // A missing row is impossible mid-commit; the fallback can never equal a real player id,
        // so the verbs refuse safely (`cargo.not-owned` / `cargo.cross-empire`) rather than
        // resolving against a guessed owner.
        var derivedPlayer = ReadWorldPlayerUnlocked(db, tx, worldId);

        foreach (var command in cargo
                     .OrderBy(c => c.CommanderId, StringComparer.Ordinal)
                     .ThenBy(c => c.CommandId, StringComparer.Ordinal))
        {
            var (ok, detail, sectorId) =
                ResolveCargoCommandUnlocked(db, tx, worldId, turn, command, derivedPlayer, nowUtc);
            if (ok)
                report.AddPostStep(
                    TurnEngine.Phases.Snapshot, TurnReportKinds.Event,
                    command.CommandId, detail, sectorId);
            else
                report.AddPostStep(
                    TurnEngine.Phases.Snapshot, TurnReportKinds.CommandDropped,
                    command.CommandId, detail);
        }
    }

    /// <summary>
    /// One cargo order, resolved: stale-legion drops follow the resolver precedent (entity gone →
    /// `entity.gone`, routed → `entity.routed`, re-validated ownership → `entity.not-yours` —
    /// the Claim/Sustain/Build `Drop` pattern), then the per-verb debit site (§Design 2+4:
    /// whole-act refusal free, attempt that reaches the loop pays once), then the verb.
    /// Replay of a recorded `CommandId` returns the stored detail without re-running the verb
    /// and without re-debiting (debit-once). Never throws on a well-admitted command; defensive
    /// shape checks below mirror admission so a command that reached this pass by another path
    /// (AI fill, old binary) still drops with a named reason instead of throwing out of a commit.
    /// </summary>
    internal (bool Ok, string Detail, string? SectorId) ResolveCargoCommandUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn,
        WorldCommand command, long? derivedPlayerId, string nowUtc)
    {
        var commandId = command.CommandId ?? "";
        var hasKey = commandId.Length != 0;
        if (hasKey)
        {
            EnsureActDebitSchemaUnlocked(db);
            var prior = ReadActDebitUnlocked(db, tx, worldId, turn, commandId);
            if (prior is not null)
                return (prior.Ok, prior.Detail, prior.SectorId);
        }

        (bool Ok, string Detail, string? SectorId) LogAndReturn(
            bool ok, string detail, string? sectorId, int cost, string? entityKey)
        {
            if (hasKey)
                InsertActDebitUnlocked(db, tx, worldId, turn, commandId, entityKey, cost, ok, detail, sectorId);
            return (ok, detail, sectorId);
        }

        if (command.EntityId is not { } entityId)
            return LogAndReturn(false, "entity.missing", null, 0, null);

        var subject = ReadCargoSubjectUnlocked(db, tx, worldId, entityId);
        if (subject is null)
            return LogAndReturn(false, "entity.gone", null, 0, entityId);
        if (subject.Routed)
            return LogAndReturn(false, "entity.routed", null, 0, entityId);
        if (!string.Equals(subject.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
            return LogAndReturn(false, "entity.not-yours", null, 0, entityId);

        // The world row is always present mid-commit; see CargoResolveUnlocked for why the
        // fallback refuses safely rather than guessing.
        var playerId = derivedPlayerId ?? -1;

        (bool Ok, string Detail, string? SectorId) outcome = command.Kind switch
        {
            WorldCommandKinds.LoadCargo => ResolveLoadUnlocked(db, tx, worldId, entityId, playerId, command),
            WorldCommandKinds.UnloadCargo => ResolveUnloadUnlocked(db, tx, worldId, entityId, playerId, command),
            WorldCommandKinds.TransferCargo => ResolveTransferUnlocked(db, tx, worldId, entityId, playerId, command),
            WorldCommandKinds.DepositCargo => ResolveDepositUnlocked(db, tx, worldId, entityId, command),
            WorldCommandKinds.WithdrawCargo => ResolveWithdrawUnlocked(db, tx, worldId, entityId, command),
            WorldCommandKinds.ClaimCache => ResolveClaimUnlocked(db, tx, worldId, entityId, playerId, command, nowUtc),
            _ => (false, "kind.unknown", null),
        };

        int costToLog;
        if (outcome.Ok)
            costToLog = CostForKind(command.Kind);
        else if (string.Equals(outcome.Detail, "entity.spent", StringComparison.Ordinal))
            costToLog = CostForKind(command.Kind);
        else
            costToLog = 0;
        return LogAndReturn(outcome.Ok, outcome.Detail, outcome.SectorId, costToLog, entityId);
    }

    // Back-compat for direct resolver tests written before the turn-keyed debit log landed:
    // reads the open turn from the world header so old call sites keep compiling while the
    // commit path passes the turn explicitly. New tests pass the turn directly.
    internal (bool Ok, string Detail, string? SectorId) ResolveCargoCommandUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        WorldCommand command, long? derivedPlayerId, string nowUtc)
    {
        var turn = ReadOpenTurnUnlocked(db, tx, worldId);
        return ResolveCargoCommandUnlocked(db, tx, worldId, turn, command, derivedPlayerId, nowUtc);
    }

    int ReadOpenTurnUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT current_turn FROM rpg_worlds WHERE world_id = $w;";
        cmd.Parameters.AddWithValue("$w", worldId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
    }

    // ---- per-kind arms (one row each of the §Design 4 table) --------------------------------------

    (bool Ok, string Detail, string? SectorId) ResolveLoadUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        string entityId, long playerId, WorldCommand command)
    {
        if (command.CargoKind != "instance" && command.CargoKind != "stack")
            return (false, "cargo.kind-unknown", null);
        var (found, weightEach) = TryResolveCargoWeight(
            new CargoWeightKey(command.CargoKind, command.InstanceId, command.ContainerId));
        if (!found)
            return (false, "cargo.weight-unknown", null);

        // Whole-act: gate-pass charges once before the move; gate-fail refunds to net zero
        // so a refusal retries free. Verb bodies stay untouched — the refund restores the
        // pre-debit row in the same tx, observably identical to never having debited.
        var before = ReadActBudgetUnlocked(db, tx, worldId, entityId);
        var debit = DebitActCostUnlocked(db, tx, worldId, entityId, command.Kind);
        if (!debit.Ok)
            return (false, debit.Reason, null);
        var (ok, reason) = LoadCargoUnlocked(
            db, tx, worldId, entityId, playerId,
            command.CargoKind, command.InstanceId, command.ContainerId, command.Qty ?? 0, weightEach);
        if (!ok)
            WriteActBudgetUnlocked(db, tx, worldId, entityId, before);
        return ok
            ? (true, $"cargo.loaded:{LastCargoSeqUnlocked(db, tx, worldId, entityId)}", null)
            : (false, reason, null);
    }

    (bool Ok, string Detail, string? SectorId) ResolveUnloadUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        string entityId, long playerId, WorldCommand command)
    {
        if (command.Seq is not { } seq)
            return (false, "cargo.seq-missing", null);

        var before = ReadActBudgetUnlocked(db, tx, worldId, entityId);
        var debit = DebitActCostUnlocked(db, tx, worldId, entityId, command.Kind);
        if (!debit.Ok)
            return (false, debit.Reason, null);
        var (ok, reason) = UnloadCargoUnlocked(db, tx, worldId, entityId, seq, playerId);
        if (!ok)
            WriteActBudgetUnlocked(db, tx, worldId, entityId, before);
        return ok ? (true, $"cargo.unloaded:{seq}", null) : (false, reason, null);
    }

    (bool Ok, string Detail, string? SectorId) ResolveTransferUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        string entityId, long playerId, WorldCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.TargetEntityId))
            return (false, "cargo.target-missing", null);
        if (command.Seq is not { } seq)
            return (false, "cargo.seq-missing", null);

        // Unpriced by lock (Q3): still enters the seam so a future price needs only a table
        // row, never a new call site. Charged-0 leaves the budget row untouched.
        var debit = DebitActCostUnlocked(db, tx, worldId, entityId, command.Kind);
        if (!debit.Ok)
            return (false, debit.Reason, null);
        var (ok, reason, newSeq) = TransferCargoUnlocked(
            db, tx, worldId, entityId, command.TargetEntityId, seq, playerId);
        return ok ? (true, $"cargo.transferred:{newSeq}", null) : (false, reason, null);
    }

    (bool Ok, string Detail, string? SectorId) ResolveDepositUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        string entityId, WorldCommand command)
    {
        // D2 recorded: deposit carries no playerId — presence + faction + capacity are the whole
        // gate, and the resolver-level ownership check above already ran.
        if (command.SectorId is not { } sectorId)
            return (false, "sector.missing", null);
        if (command.Seq is not { } seq)
            return (false, "cargo.seq-missing", null);

        var before = ReadActBudgetUnlocked(db, tx, worldId, entityId);
        var debit = DebitActCostUnlocked(db, tx, worldId, entityId, command.Kind);
        if (!debit.Ok)
            return (false, debit.Reason, sectorId);
        var (ok, reason, newSeq) = DepositUnlocked(db, tx, worldId, entityId, sectorId, seq);
        if (!ok)
            WriteActBudgetUnlocked(db, tx, worldId, entityId, before);
        return ok ? (true, $"cargo.deposited:{newSeq}", sectorId) : (false, reason, sectorId);
    }

    (bool Ok, string Detail, string? SectorId) ResolveWithdrawUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        string entityId, WorldCommand command)
    {
        // D2 recorded, D3 locked: withdraw carries no playerId and no mass — the row's mass is
        // resolved here, server-side, before the verb runs, so a miss writes nothing.
        if (command.SectorId is not { } sectorId)
            return (false, "sector.missing", null);
        if (command.Seq is not { } seq)
            return (false, "cargo.seq-missing", null);

        var stored = ReadSectorStorageRowUnlocked(db, tx, worldId, sectorId, seq);
        if (stored is null)
            return (false, "cargo.not-found", sectorId);
        var (found, weightEach) = TryResolveCargoWeight(
            new CargoWeightKey(stored.Kind, stored.InstanceId, stored.ContainerId));
        if (!found)
            return (false, "cargo.weight-unknown", sectorId);

        var before = ReadActBudgetUnlocked(db, tx, worldId, entityId);
        var debit = DebitActCostUnlocked(db, tx, worldId, entityId, command.Kind);
        if (!debit.Ok)
            return (false, debit.Reason, sectorId);
        var (ok, reason, newSeq) = WithdrawUnlocked(
            db, tx, worldId, sectorId, entityId, seq, weightEach);
        if (!ok)
            WriteActBudgetUnlocked(db, tx, worldId, entityId, before);
        return ok ? (true, $"cargo.withdrawn:{newSeq}", sectorId) : (false, reason, sectorId);
    }

    (bool Ok, string Detail, string? SectorId) ResolveClaimUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        string entityId, long playerId, WorldCommand command, string nowUtc)
    {
        // D2 recorded: claim's playerId is delve-parity shape whose gate is reachability alone —
        // passed through, never branched on.
        if (string.IsNullOrWhiteSpace(command.CacheId))
            return (false, "cache.missing", null);
        var correlation = command.CommandId ?? "";
        if (correlation.Length == 0)
            return (false, "correlation.missing", null);

        EnsureCacheClaimSchemaUnlocked(db);

        // Replay first: the identical (cacheId, worldId, entityId, correlationId) returns the SAME
        // recorded result — and, critically, resolves no mass twice (the claim-log replay
        // discipline `CacheFieldAccess` already proves). No debit on replay: the first arrival
        // already paid once.
        var replayed = ReadCargoClaimUnlocked(db, tx, command.CacheId, worldId, entityId, correlation);
        if (replayed is not null)
            return replayed.Ok
                ? (true, ClaimDetail(replayed), null)
                : (false, replayed.Reason, null);

        // Mass pre-flight, before the verb runs: every row resolved up front, so a single
        // unknown row refuses the whole command with zero writes — never a half-moved claim.
        // Same transaction, same gate held throughout: the verb sees exactly these rows.
        // Free when it refuses: nothing was pried open, nothing is owed.
        var rows = ListCacheItemsUnlocked(db, tx, command.CacheId);
        var weights = new Dictionary<int, long>(rows.Count);
        foreach (var row in rows)
        {
            var (found, weightEach) = TryResolveCargoWeight(
                new CargoWeightKey(row.Kind, row.InstanceId, row.ContainerId));
            if (!found)
                return (false, "cargo.weight-unknown", null);
            weights[row.Seq] = weightEach;
        }

        // Reachability before spend (attempt-pays / refusal-free): a stale or forged cache
        // refuses free here, before the seam. An attempt that reaches the per-row loop below
        // pays once even at `claimed:0+N` — left-behind rows are pay-for-nothing by lock.
        if (!IsCargoClaimReachableUnlocked(db, tx, worldId, entityId, command.CacheId))
            return (false, "cache.unreachable", null);

        var before = ReadActBudgetUnlocked(db, tx, worldId, entityId);
        var debit = DebitActCostUnlocked(db, tx, worldId, entityId, command.Kind);
        if (!debit.Ok)
            return (false, debit.Reason, null);

        var result = ClaimCorpseCacheIntoCargoUnlocked(
            db, tx, worldId, entityId, playerId, command.CacheId, correlation, nowUtc,
            row => weights.TryGetValue(row.Seq, out var w)
                ? w
                : throw new InvalidOperationException(
                    $"Claim pre-flight resolved seq {row.Seq} but the verb asked for it again."));
        if (!result.Ok)
            WriteActBudgetUnlocked(db, tx, worldId, entityId, before);
        return result.Ok
            ? (true, ClaimDetail(result), null)
            : (false, result.Reason, null);
    }

    static string ClaimDetail(CorpseCacheCargoClaimResult result) =>
        $"cache.claimed:{result.ClaimedSeqs.Count}+{result.SkippedSeqs.Count}";

    // ---- resolver-level reads (liveness, never capacity) --------------------------------------------

    /// <summary>Where the acting legion stands in the resolver's eyes: does the row exist, is it
    /// routed, who holds it. One row read — presence, faction, and capacity stay inside the verbs,
    /// exactly where they already live.</summary>
    sealed record CargoSubjectState(string? OwnerFactionId, bool Routed);

    CargoSubjectState? ReadCargoSubjectUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT owner_faction_id, routed FROM rpg_world_entities
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new CargoSubjectState(
                r.IsDBNull(0) ? null : r.GetString(0),
                !r.IsDBNull(1) && r.GetInt32(1) != 0)
            : null;
    }

    /// <summary>The newest cargo seq aboard — the row the load just inserted (seqs are dense per
    /// legion, so the just-inserted row holds the max). Read through the existing list helper,
    /// never a second tally.</summary>
    int LastCargoSeqUnlocked(SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        var rows = ListCargoUnlocked(db, tx, worldId, entityId);
        var seq = -1;
        foreach (var row in rows)
            if (row.Seq > seq)
                seq = row.Seq;
        return seq;
    }
}
