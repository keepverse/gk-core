using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>
/// The per-turn command log (spec-turn-engine.md §Persistence). Submission is idempotent on
/// (world, turn, commander, commandId); the stored original always wins a replay, so a client can
/// retry a request it never saw the answer to without rewriting an order it already committed.
/// </summary>
public sealed partial class RpgStore
{
    void EnsureWorldTurnSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_world_commands (
              world_id TEXT NOT NULL,
              turn INTEGER NOT NULL,
              commander_id TEXT NOT NULL,
              command_id TEXT NOT NULL,
              seq INTEGER NOT NULL,
              kind TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              submitted_utc TEXT NOT NULL,
              PRIMARY KEY (world_id, turn, commander_id, command_id)
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_world_commands_turn
              ON rpg_world_commands(world_id, turn, commander_id, seq);
            CREATE TABLE IF NOT EXISTS rpg_world_turn_commits (
              world_id TEXT NOT NULL,
              turn INTEGER NOT NULL,
              commander_id TEXT NOT NULL,
              committed_utc TEXT NOT NULL,
              PRIMARY KEY (world_id, turn, commander_id)
            );
            CREATE TABLE IF NOT EXISTS rpg_world_turn_log (
              world_id TEXT NOT NULL,
              turn INTEGER NOT NULL,
              state_hash TEXT NOT NULL,
              engine_version INTEGER NOT NULL,
              ruleset_version INTEGER NOT NULL,
              seed TEXT NOT NULL,
              committed_utc TEXT NOT NULL,
              report_json TEXT,
              PRIMARY KEY (world_id, turn)
            );
            """);

        // Why an AI filed an order. Nullable because the player never explains themselves, and a
        // column rather than a field on WorldCommand because that record is the replay unit — an
        // audit string inside it would travel through the engine and the hash for no reason.
        EnsureColumn(db, "rpg_world_commands", "reason", "TEXT");

        // `TurnReport.Phases` (the locked phase order, made observable) was never persisted
        // alongside `Entries`, only re-derivable from them — and a phase that ran with zero entries
        // (`Growth`'s own named no-op, most turns) leaves nothing behind to re-derive it from, so it
        // silently vanished from every already-stored turn's report. Found by actually watching a
        // turn play back, not by a test. A legacy row with no `phases_json` falls back to that lossy
        // reconstruction (`TurnReport.FromEntries`) rather than refusing.
        EnsureColumn(db, "rpg_world_turn_log", "phases_json", "TEXT");

        // `budget-debit` debit-once rows live as long as the command log (never trimmed), so a
        // fresh `Init` over an old file still finds the table even before any priced act lands.
        EnsureActDebitSchemaUnlocked(db);
    }

    /// <summary>Files one order against the world's open turn.</summary>
    public (bool Ok, string Reason, bool Replayed) SubmitWorldCommand(
        string worldId, WorldCommand command, DateTimeOffset? utcNow = null)
    {
        var result = SubmitWorldCommands(worldId, new[] { command }, utcNow)[0];
        return (result.Ok, result.Reason, result.Replayed);
    }

    /// <summary>
    /// Files a batch of orders against the world's **open** turn, loading the world exactly once.
    ///
    /// Two rules the single-command shape got wrong and this one fixes: the caller does not choose
    /// the turn (filing into a resolved or future turn would silently corrupt that turn's replay),
    /// and a batch does not re-read the whole graph per order. Results are per command — one stale
    /// order must not throw away the rest of a commander's turn.
    /// </summary>
    public IReadOnlyList<WorldCommandOutcome> SubmitWorldCommands(
        string worldId, IReadOnlyList<WorldCommand> commands, DateTimeOffset? utcNow = null)
    {
        if (commands.Count == 0) return Array.Empty<WorldCommandOutcome>();

        var world = LoadWorldState(worldId);
        if (world is null)
            return commands
                .Select(c => new WorldCommandOutcome(c.CommandId, false, "world.unknown", false))
                .ToList();

        // party-dungeon delve-scope (2026-09-05): a delve world is never iterated by the map's
        // turn — rooms are moved through by RpgStore.Delve.MoveParty, never TurnEngine.Step.
        // WorldState itself carries no Kind field on purpose (WorldCanonical hashing is untouched),
        // so the check reads the header, refusing BEFORE any write.
        var header = GetWorldHeader(worldId);
        if (header != null && header.Kind != "map")
            return commands
                .Select(c => new WorldCommandOutcome(c.CommandId, false, "world.not-a-map", false))
                .ToList();

        var turn = world.CurrentTurn;
        var now = (utcNow ?? ServerClock.UtcNow).ToString("o");
        var outcomes = new List<WorldCommandOutcome>(commands.Count);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            foreach (var command in commands)
            {
                var (admitted, reason) = WorldCommandAdmission.Admit(world, command);
                if (!admitted)
                {
                    outcomes.Add(new WorldCommandOutcome(command.CommandId, false, reason, false));
                    continue;
                }

                if (CommandExistsUnlocked(db, tx, worldId, turn, command))
                {
                    outcomes.Add(new WorldCommandOutcome(command.CommandId, true, "replay", true));
                    continue;
                }

                InsertCommandUnlocked(db, tx, worldId, turn, command, now);
                outcomes.Add(new WorldCommandOutcome(command.CommandId, true, "ok", false));
            }

            tx.Commit();
        }

        return outcomes;
    }

    static bool CommandExistsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, WorldCommand command)
    {
        using var existing = db.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText = """
            SELECT 1 FROM rpg_world_commands
            WHERE world_id = $w AND turn = $t AND commander_id = $c AND command_id = $id;
            """;
        existing.Parameters.AddWithValue("$w", worldId);
        existing.Parameters.AddWithValue("$t", turn);
        existing.Parameters.AddWithValue("$c", command.CommanderId);
        existing.Parameters.AddWithValue("$id", command.CommandId);
        return existing.ExecuteScalar() != null;
    }

    static void InsertCommandUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, WorldCommand command,
        string now, string? reason = null)
    {
        long seq;
        using (var next = db.CreateCommand())
        {
            next.Transaction = tx;
            next.CommandText = """
                SELECT COALESCE(MAX(seq), -1) + 1 FROM rpg_world_commands
                WHERE world_id = $w AND turn = $t AND commander_id = $c;
                """;
            next.Parameters.AddWithValue("$w", worldId);
            next.Parameters.AddWithValue("$t", turn);
            next.Parameters.AddWithValue("$c", command.CommanderId);
            seq = Convert.ToInt64(next.ExecuteScalar());
        }

        using var ins = db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO rpg_world_commands (world_id, turn, commander_id, command_id, seq,
                kind, payload_json, submitted_utc, reason)
            VALUES ($w, $t, $c, $id, $seq, $kind, $payload, $now, $reason);
            """;
        ins.Parameters.AddWithValue("$w", worldId);
        ins.Parameters.AddWithValue("$t", turn);
        ins.Parameters.AddWithValue("$c", command.CommanderId);
        ins.Parameters.AddWithValue("$id", command.CommandId);
        ins.Parameters.AddWithValue("$seq", seq);
        ins.Parameters.AddWithValue("$kind", command.Kind);
        ins.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new CommandPayload(
            command.EntityId, command.SectorId, command.SlotIndex, command.LanePath, command.Stance,
            command.Amount, command.StructureId, command.WardenId, command.ProjectId,
            command.RelicInstanceIds, command.Seq, command.TargetEntityId, command.CacheId,
            command.CargoKind, command.InstanceId, command.ContainerId, command.Qty)));
        ins.Parameters.AddWithValue("$now", now);
        // Bounded at the boundary like every other free-text field: an audit string is not worth
        // failing a turn over, and an unbounded one is a row nobody budgeted for.
        ins.Parameters.AddWithValue("$reason", reason is null
            ? DBNull.Value
            : reason.Length <= MaxCommandReasonLength ? reason : reason[..MaxCommandReasonLength]);
        ins.ExecuteNonQuery();
    }

    /// <summary>How much of an AI's reasoning is kept. Long enough to be useful, short enough to bound.</summary>
    public const int MaxCommandReasonLength = 200;

    /// <summary>
    /// Every faction with a policy takes its turn (spec-ai-commander.md §The commander loop).
    ///
    /// Each one is handed <see cref="BelievedWorldView"/> — its own fog, on the same terms the
    /// player plays under — files whatever it decides, and commits. Factions are walked in ordinal
    /// order and each gets its own seed stream, so adding one never shifts another's rolls.
    ///
    /// Nothing here is wrapped in a try. A policy is pure integer arithmetic over validated data; if
    /// it throws, the commit's transaction rolls back and the world is untouched, which is a visible
    /// bug rather than a faction that quietly stopped playing.
    /// </summary>
    static void FillAiCommandersUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, WorldState world,
        ulong worldSeed, string now, Func<string, IFactionPolicy>? policies)
    {
        var resolve = policies ?? FactionPolicies.Resolve;
        var committed = ReadCommittersUnlocked(db, tx, worldId, turn);

        foreach (var faction in world.Factions.OrderBy(f => f.FactionId, StringComparer.Ordinal))
        {
            if (faction.PolicyId is not { } policyId) continue;                  // a person plays this one
            if (committed.Contains(faction.FactionId)) continue;                 // already ended its turn

            // Orders already filed speak for a faction as loudly as a commit does. Without this the
            // escape hatch does not work: the *first* commit of a turn fills every AI faction that
            // has not committed yet, so a scenario scripting two of them has its second one filled
            // over by whichever commit happened to land first. Found by dumping a scenario's command
            // log and finding orders in it that nobody had written.
            if (HasCommandsUnlocked(db, tx, worldId, turn, faction.FactionId)) continue;

            // Momentum's input (spec-ai-commander.md §Momentum). Derived from the command log, which
            // IS the save -- commands are never trimmed -- so nothing new is stored to support it,
            // and it cannot reach a replayed hash because replay never re-runs a policy. Turn 0 has
            // no predecessor, which simply reads as "no standing choice" and disables hysteresis.
            var lastOrders = turn > 0
                ? LastOrderedDestinationsUnlocked(db, tx, worldId, turn - 1, faction.FactionId, world)
                : null;
            var view = new BelievedWorldView(world, faction.FactionId, lastOrders);
            var seed = SeededRng.DeriveStream(worldSeed, $"ai:{faction.FactionId}:{turn}").NextULong();
            var orders = resolve(policyId).Decide(view, seed);

            // One order per entity, and no more orders than the world has entities. The spec makes
            // this the AI's bound the way MaxCommandsPerSubmit bounds a client — stated but, until
            // it is checked here, worth nothing: a policy with a runaway loop would fill the command
            // table from inside a write transaction holding the store's global lock.
            if (orders.Count > world.Entities.Count + 1)
                throw new InvalidOperationException(
                    $"Policy '{policyId}' filed {orders.Count} orders for '{faction.FactionId}'; " +
                    "at most one per entity is allowed.");

            var subjects = new HashSet<string>(StringComparer.Ordinal);

            foreach (var order in orders)
            {
                // A policy files as the faction whose eyes it was given, full stop. Admission checks
                // that a commander exists and owns the entity it names, so without this a policy
                // could file a *legal* order on another faction's behalf — orders that faction never
                // chose, under its name, and it would still be waiting at the barrier.
                if (!string.Equals(order.Command.CommanderId, faction.FactionId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Policy '{policyId}' filed for '{order.Command.CommanderId}' " +
                        $"while acting as '{faction.FactionId}'.");

                if (order.Command.EntityId is { } subject && !subjects.Add(subject))
                    throw new InvalidOperationException(
                        $"Policy '{policyId}' gave '{subject}' two orders in turn {turn}.");

                // Admission is the same gate a person's order passes. A policy that fails it is a
                // bug in the policy, and a silent `continue` here would hide it forever: the faction
                // would commit every turn having done nothing, which looks exactly like standing fast.
                var (admitted, why) = WorldCommandAdmission.Admit(world, order.Command);
                if (!admitted)
                    throw new InvalidOperationException(
                        $"Policy '{policyId}' filed an inadmissible order " +
                        $"'{order.Command.CommandId}' ({order.Command.Kind}): {why}.");

                if (CommandExistsUnlocked(db, tx, worldId, turn, order.Command)) continue;

                InsertCommandUnlocked(db, tx, worldId, turn, order.Command, now, order.Reason);
            }

            MarkCommittedUnlocked(db, tx, worldId, turn, faction.FactionId, now);
        }
    }

    /// <summary>Whether this commander has already filed anything for the turn.</summary>
    static bool HasCommandsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, string commanderId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT 1 FROM rpg_world_commands WHERE world_id = $w AND turn = $t AND commander_id = $c LIMIT 1;";
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$t", turn);
        cmd.Parameters.AddWithValue("$c", commanderId);
        return cmd.ExecuteScalar() != null;
    }

    static void MarkCommittedUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, string commanderId, string now)
    {
        using var commit = db.CreateCommand();
        commit.Transaction = tx;
        commit.CommandText = """
            INSERT OR IGNORE INTO rpg_world_turn_commits (world_id, turn, commander_id, committed_utc)
            VALUES ($w, $t, $c, $now);
            """;
        commit.Parameters.AddWithValue("$w", worldId);
        commit.Parameters.AddWithValue("$t", turn);
        commit.Parameters.AddWithValue("$c", commanderId);
        commit.Parameters.AddWithValue("$now", now);
        commit.ExecuteNonQuery();
    }

    /// <summary>
    /// A turn's orders with the reasoning behind them — what the turn report shows so a player can
    /// tell an AI's mistake from a bug. Commands are never trimmed, so neither is this.
    /// </summary>
    /// <summary>
    /// Entity id → the sector a faction's own orders sent it to on <paramref name="turn"/>.
    ///
    /// <para>Only <c>Move</c> carries a destination, and the destination is the <b>last</b> sector of
    /// its lane path. Later orders for one entity win, matching the log's own ordering — a policy
    /// files at most one order per entity, but the log does not enforce that and the read should not
    /// assume what it can simply honour.</para>
    /// </summary>
    static Dictionary<string, string> LastOrderedDestinationsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn, string factionId,
        WorldState world)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        // The entity id is NOT a column -- it lives inside payload_json, like every other
        // command field except the five the table indexes on. Selecting `entity_id` threw
        // "no such column" against every real world, which the Data suite caught on the first run.
        cmd.CommandText = """
            SELECT payload_json
            FROM rpg_world_commands
            WHERE world_id = $w AND turn = $t AND commander_id = $c
            ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$t", turn);
        cmd.Parameters.AddWithValue("$c", factionId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            var payloadJson = r.GetString(0);
            if (string.IsNullOrWhiteSpace(payloadJson)) continue;

            CommandPayload? payload;
            try { payload = JsonSerializer.Deserialize<CommandPayload>(payloadJson); }
            catch (JsonException) { continue; }
            if (payload?.EntityId is not { } entityId) continue;

            var dest = WalkToDestination(world, entityId, payload);
            if (dest is not null) result[entityId] = dest;
        }

        return result;
    }

    /// <summary>
    /// Where a stored <c>Move</c> was actually headed.
    ///
    /// <para>The command records a <b>lane</b> path and a null <c>SectorId</c>, so the destination is
    /// not stored and must be walked: start at the entity's origin — the same
    /// <c>AtSectorId ?? OnLaneTowardSectorId</c> the policy's own <c>Route</c> uses as its origin —
    /// and step across each lane to whichever end is not the one just left.</para>
    ///
    /// <para>Returns null for anything that cannot be resolved (not a move, empty path, a lane that
    /// no longer exists, an entity that is gone). A null simply reads as "no standing choice" and
    /// disables hysteresis for that entity, which is the right failure: momentum is a damping term,
    /// and a damping term that guesses is worse than one that abstains.</para>
    /// </summary>
    static string? WalkToDestination(WorldState world, string entityId, CommandPayload payload)
    {
        var lanePath = payload.LanePath;
        if (lanePath is null || lanePath.Count == 0) return null;

        var entity = world.Entities.FirstOrDefault(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal));
        var at = entity?.AtSectorId ?? entity?.OnLaneTowardSectorId;
        if (at is null) return null;

        foreach (var laneId in lanePath)
        {
            var lane = world.Lanes.FirstOrDefault(l => string.Equals(l.LaneId, laneId, StringComparison.Ordinal));
            if (lane is null) return null;
            if (string.Equals(lane.FromSectorId, at, StringComparison.Ordinal)) at = lane.ToSectorId;
            else if (string.Equals(lane.ToSectorId, at, StringComparison.Ordinal)) at = lane.FromSectorId;
            else return null;   // the path does not actually start where the entity stands
        }

        return at;
    }

    public IReadOnlyList<LoggedWorldCommand> ListLoggedWorldCommands(string worldId, int turn)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT commander_id, command_id, kind, payload_json, reason
                FROM rpg_world_commands
                WHERE world_id = $w AND turn = $t
                ORDER BY commander_id, seq;
                """;
            cmd.Parameters.AddWithValue("$w", worldId);
            cmd.Parameters.AddWithValue("$t", turn);

            var list = new List<LoggedWorldCommand>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new LoggedWorldCommand(
                    ReadCommandRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)),
                    r.IsDBNull(4) ? null : r.GetString(4)));

            return list;
        }
    }

    /// <summary>
    /// Every order filed for a turn, in stable (commander, submission) order — the engine's input,
    /// and the reason a replay reproduces a turn exactly.
    /// </summary>
    public IReadOnlyList<WorldCommand> ListWorldCommands(string worldId, int turn)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT commander_id, command_id, kind, payload_json
                FROM rpg_world_commands
                WHERE world_id = $w AND turn = $t
                ORDER BY commander_id, seq;
                """;
            cmd.Parameters.AddWithValue("$w", worldId);
            cmd.Parameters.AddWithValue("$t", turn);

            var list = new List<WorldCommand>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadCommandRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            }

            return list;
        }
    }

    /// <summary>
    /// Every optional field a command can carry. Adding one to <see cref="WorldCommand"/> and
    /// forgetting it here loses it in the round trip and the order comes back malformed — which is
    /// exactly how `stance` was found missing.
    /// </summary>
    sealed record CommandPayload(
        string? EntityId, string? SectorId, int? SlotIndex, IReadOnlyList<string>? LanePath,
        string? Stance = null, long? Amount = null, string? StructureId = null, string? WardenId = null,
        string? ProjectId = null, IReadOnlyList<string>? RelicInstanceIds = null,
        int? Seq = null, string? TargetEntityId = null, string? CacheId = null,
        string? CargoKind = null, string? InstanceId = null, string? ContainerId = null,
        long? Qty = null);

    /// <summary>Reports are kept for the most recent turns; older ones are re-derived on demand.</summary>
    public const int ReportHotTail = 50;

    /// <summary>
    /// `species-progression` SP1.2 (map C1, `layer-source-selector`) — extracted from
    /// `CommitWorldTurn`'s `HubInputsFor` delegate so the rule (which progression layers a
    /// district-assault member carries) is directly testable without driving a full turn-commit
    /// simulation, and asks <see cref="FusionRpg.Core.Stats.Aptitudes.ProgressionLayerSelector"/>
    /// instead of re-deriving the answer inline (the exact copied-rule shape map C1 names).
    ///
    /// <para>A member with no <c>InstanceId</c> is a non-player force or a guard (its own field doc
    /// says so) and has nothing to look up. A member WITH one is always a unique specimen from this
    /// provider's own vantage point (world-turn district members are never a bare "empire general"
    /// source), so it composes commander (if its OWNER empire is the human empire) + its OWN specimen
    /// allocation ONLY -- never its empire's species allocation, which is the fallback map C1 found
    /// leaking here and nowhere else (the lawn and the web squad already got this right).</para>
    /// </summary>
    internal BattleHubInputs? WorldTurnHubInputsForUnlocked(
        SqliteConnection db, WorldEntityMember member, long playerId)
    {
        if (string.IsNullOrWhiteSpace(member.InstanceId)) return null;

        // C1 fix: the empire is the specimen's OWNER (save-identity G5's equivalent, pre-migration --
        // rpg_unique_actors.empire_id if stamped, else the human empire), never the species' side.
        // That is what makes a zombie-side human-owned unique agree with the sheet (map C1's own named
        // behaviour change) instead of reading as Zomboss's by mistake.
        var empire = SpecimenOwnerEmpireUnlocked(db, member.InstanceId!)?.Empire ?? HumanEmpireOf(playerId);
        var layers = FusionRpg.Core.Stats.Aptitudes.ProgressionLayerSelector.Select(
            FusionRpg.Core.Creatures.CreatureProgressionSource.UniqueSpecimen(
                member.InstanceId!, occurrenceId: "world-turn"),
            empire, HumanEmpireOf(playerId));

        // ai-empire-species EP4.18 (R23, R4 mirrored): the commander pool is the OWNER EMPIRE's, read
        // through the ONE empire-keyed pool read (EP4.17) — no `empire == Dave` branch and no second
        // pool reader. Theta comes from the host (`ServerPowerIndexProvider.ActorIndexFor`, EP4.16, wired
        // at the composition root); a store nobody wired answers 0, which leaves a non-human empire's
        // computed default empty — byte-identical to the pre-R23 Empty.
        //
        // Best-effort on the aptitude tuning hub, the same contract this store's other readers give an
        // unconfigured hub: without it the computed default cannot be priced, so a human owner keeps the
        // explicit read it always had and any other empire resolves Empty rather than taking the whole
        // turn down.
        var ownerRef = new Core.Saves.EmpireRef(new Core.Saves.SaveId(playerId), empire);
        // The pool is the OWNER EMPIRE's, through the ONE empire-keyed read (EP4.17) at this store's
        // configured host Theta (EP4.16/EP4.18 — `CommanderPoolForUnlocked`, whose own best-effort
        // contract covers an unconfigured tuning hub: a human owner keeps the explicit pool it always
        // had, any other empire resolves Empty rather than taking the whole turn down).
        var commander = layers.CarriesCommander
            ? CommanderPoolForUnlocked(db, ownerRef).Allocation
            : FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;

        // map C1: a unique specimen carries ONLY its own allocation (2a) -- never its empire's species
        // allocation (2b), the fallback this provider used to leak. `layers.Owner` is always
        // `ProgressionOwner.Specimen` for this call site (the source above is always UniqueSpecimen),
        // but the switch stays general to match every other selector call site (SP1.3-SP1.5).
        var specimen = layers.Owner switch
        {
            // EP1.14 (spec-default-build.md) -- the siege member read: a levelled specimen the
            // player never built now composes its species' default distribution instead of Empty.
            FusionRpg.Core.Stats.Aptitudes.ProgressionOwner.Specimen s =>
                EffectiveUniqueAllocationUnlocked(db, s.InstanceId,
                    FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Tuning).Allocation,
            FusionRpg.Core.Stats.Aptitudes.ProgressionOwner.Species g =>
                EffectiveSpeciesAllocationUnlocked(db, playerId, g.SpeciesId,
                    FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Tuning, layers.Empire),
            _ => FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty,
        };

        // species-progression `species-layer-delivery` step 6.2 (SP6.7): 1a + 1b of the specimen's
        // OWN species, keyed to its OWNER empire -- the SAME `empire` this method already resolved
        // for the commander/specimen split above, never re-derived. Never 2b: this method has no
        // caller for it, matching the acceptance's own "never 2b" rule for a Specimen answer. The
        // species id comes from GetCreatureProfile, the SAME lookup GetSpecimenLedgerRoll already
        // uses for the identical (owner, speciesId) shape.
        var speciesId = GetCreatureProfile(member.InstanceId!)?.SpeciesId;
        var speciesLayers = string.IsNullOrWhiteSpace(speciesId)
            ? null
            : SpeciesLayersForSpecimen(
                new FusionRpg.Core.Saves.EmpireRef(new FusionRpg.Core.Saves.SaveId(playerId), empire), speciesId);

        return new BattleHubInputs
        {
            Aptitude = commander + specimen,
            SpeciesLayers = speciesLayers is { Count: > 0 } ? speciesLayers : null,
        };
    }

    /// <summary>The locked form of <see cref="WorldTurnHubInputsForUnlocked"/>, for a caller (SP1.5's
    /// own four-path parity proof, or any future caller outside an already-open transaction) that
    /// holds no connection of its own.</summary>
    public BattleHubInputs? WorldTurnHubInputsFor(WorldEntityMember member, long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return WorldTurnHubInputsForUnlocked(db, member, playerId);
        }
    }

    /// <summary>
    /// Marks one commander committed, and — when the barrier releases — resolves the turn in a
    /// single transaction: step the engine, replace the world graph, append the turn log, advance
    /// the turn counter.
    ///
    /// <paramref name="expectedTurn"/> is the turn the caller means to end, and it is required
    /// rather than optional. Once an AI commander commits automatically (`ai-commander`), *any*
    /// commit can be the one that releases the barrier — so a retried request would read the new
    /// current turn, commit that instead, and silently resolve a second turn the player never
    /// played. Naming the turn makes a retry a refusal.
    ///
    /// The world is loaded **inside** the lock for the same reason: a pre-lock read could be
    /// resolved out from under this call, leaving it committing against a world that no longer
    /// exists.
    /// </summary>
    public WorldTurnCommitResult CommitWorldTurn(
        string worldId, string commanderId, int expectedTurn, DateTimeOffset? utcNow = null,
        Func<string, IFactionPolicy>? policies = null)
    {
        var now = (utcNow ?? ServerClock.UtcNow).ToString("o");

        lock (_gate)
        {
            var world = LoadWorldState(worldId);
            if (world is null) return new WorldTurnCommitResult(false, "world.unknown", false, null);
            if (world.Factions.All(f => !string.Equals(f.FactionId, commanderId, StringComparison.Ordinal)))
                return new WorldTurnCommitResult(false, "commander.unknown", false, null);

            var header = GetWorldHeader(worldId)!;
            // party-dungeon delve-scope: refused before MarkCommittedUnlocked's first write.
            if (header.Kind != "map") return new WorldTurnCommitResult(false, "world.not-a-map", false, null);

            var turn = world.CurrentTurn;

            // Checked after "who are you", so a stranger cannot learn which turn is open, and
            // refused in both directions: the question is "is this the turn you were looking at",
            // not "is this in the past".
            if (expectedTurn != turn)
                return new WorldTurnCommitResult(false, "turn.stale", false, null);

            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            MarkCommittedUnlocked(db, tx, worldId, turn, commanderId, now);

            // Every commander that is not a person now takes its turn, before the barrier is read.
            // This is the only place it can happen: the barrier is here, so filling anywhere else
            // would leave the caller told "waiting" for a turn that in fact resolved — and would
            // leave every non-HTTP caller unable to advance at all.
            FillAiCommandersUnlocked(db, tx, worldId, turn, world, header.Seed, now, policies);

            var committed = ReadCommittersUnlocked(db, tx, worldId, turn);
            var commanders = world.Factions.Select(f => f.FactionId).ToList();
            if (!new WaitForAllCommitted().ShouldFire(commanders, committed))
            {
                tx.Commit();
                return new WorldTurnCommitResult(true, "waiting", false, null);
            }

            // Everyone is in: resolve. The seed is per world; each turn derives its own stream so
            // one turn's rolls never shift another's.
            var commands = ListWorldCommandsUnlocked(db, tx, worldId, turn);
            // loam-relics-and-wonders `wonder-build-flow` §Design 4: the Data-side relic
            // reachability gate — every named relic proved owned, unassigned and reachable in
            // THIS transaction, moments before resolution. Unreachable ids are emptied here so
            // BuildResolver refuses them as `relic.not-reachable` during the Step below.
            commands = ValidateWonderRelicCommandsUnlocked(db, tx, worldId, commands);
            // commander-roster EP3.8: the Data-side half of `attach-commander` — the role and the
            // specimen's base are store facts, so they are checked (and the species/level stamped)
            // here, moments before Core's own Snapshot re-validation.
            commands = ValidateCommanderAttachCommandsUnlocked(db, tx, worldId, commands);
            // D4 (solid-remediation T3.3): a siege composes a player-owned specimen from its real
            // aptitude allocation instead of flat, level-derived stats. The resolver is Core-only and
            // must never see a store — `guard-dal.ps1` holds that line — so Core declares the need as a
            // delegate and this Data-layer call site, which has the store, injects it. Dependency
            // inversion, not a layering exception.
            //
            // Read INSIDE this transaction, matching the RpgStore.GateCounterSeed precedent: the
            // allocation used to compose a fight and the state the turn commits are then the same
            // snapshot, never two values that could move between them.
            //
            // A member with no InstanceId is a non-player force or a guard (its own field doc says so)
            // and has nothing to look up, so it composes exactly as it did before this seam.
            var battles = new DistrictAssaultResolver
            {
                HubInputsFor = member => WorldTurnHubInputsForUnlocked(db, member, header.PlayerId),
                // T74/A33: the ladder half of the same inversion. A legion member holding a granted
                // action must price it at its own rung, and this Data-layer call site is what has the
                // store. Read inside the same transaction as HubInputsFor just above, for the same
                // reason: the state that priced the fight and the state this turn commits are then one
                // snapshot. A blank id is "no holder" (the provider's own guard).
                UnlockStateFor = instanceId => UnlockStateOfUnlocked(db, instanceId, tx),
                UnlockTuning = FusionRpg.Core.Actions.Unlock.UnlockTuningPolicy.Tuning,
                // commander-roster EP3.11 (spec-legion-commander.md test 6b): a Commander member whose
                // specimen is not in `Roster` this turn -- seated on the lawn, on a delve, or away with
                // another legion -- cannot fight here, and the REST of the legion fights on. The phase
                // is a store fact, so it is answered here rather than in Core, the same inversion the
                // two providers above already use; Core owns the rule (`CommanderAway`, applied in
                // `BuildAnimateSetups`) and the report line (`BattleReporting.Fight`).
                //
                // A missing row reads as away rather than as fielded: an unresolved specimen has no
                // phase to be in `Roster`, and a commander nobody can resolve must not silently join a
                // fight on flat level-derived stats. Read in the same transaction as the two providers
                // above, so the phase that decided the fight and the phase this turn commits are one
                // snapshot.
                MemberAway = member => member.InstanceId is { Length: > 0 } id
                    && !string.Equals(ReadUniqueActorUnlocked(db, id, tx)?.Phase, UniqueActorPhases.Roster,
                        StringComparison.Ordinal),
            };
            var result = TurnEngine.Step(world, commands, header.Seed, battles);

            // commander-roster EP3.12 (spec-legion-commander.md "Casualty — no commander special"):
            // a commander member reduced to zero in a siege is handled as ANY unique is. It runs BEFORE
            // the diff, because a member this pass detaches must be gone from the world the diff writes
            // (and from the hash below) — the stored state and the stored hash both describe the
            // committed turn, never the mid-commit one.
            var postCasualtyWorld = ApplyCommanderCasualtiesUnlocked(db, tx, world, result.World, now);

            // base-defense world-graph-diff 3.2/3.3: turn commit writes only what changed, not the
            // whole graph — `world` (pre-step) and `result.World` (post-step) are exactly `previous`
            // and `next`. World *creation* (CreateWorld) is unaffected and still uses
            // WriteWorldGraphUnlocked on an empty graph, which is already the cheapest possible case.
            DiffWorldGraphUnlocked(db, tx, world, postCasualtyWorld);

            // loam-relics-and-wonders `wonder-build-flow` §Design 7: the relic spend — fires only
            // for an accepted `"build.started:"` report line, in this same tx, never a second one.
            SpendWonderRelicsUnlocked(db, tx, worldId, commands, result.Report);

            // empire-inventory-surfaces `cargo-commands` §Design 5: the cargo pass — resolves the
            // six cargo/cache kinds post-Step, in this same tx, after the diff (verbs read live
            // post-turn positions/owners) and before the log insert (outcome entries land in the
            // stored report) and before the decay tick below (claim-then-decay: a claimed row is
            // gone before decay sees it; skipped rows remain and decay normally).
            // world-action-economy `budget-debit` §Design 2+5: the same pass spends the act costs
            // from the refilled budget (debit-after-refill) and records debit-once rows keyed
            // (worldId, turn, commandId) in this same tx.
            CargoResolveUnlocked(db, tx, worldId, turn, commands, result.Report, now);

            // deployment-hierarchy module 6 (`cache-retrieval-mission`,
            // `spec-cache-retrieval-mission.md`): due voided-cache retrieval missions resolve in
            // this same tx, after the cargo pass (the same claim-then-decay ordering: a retrieved
            // row is gone before the decay tick below sees it) and before the log insert (outcome
            // entries land in the stored report). No-op when nothing is filed.
            ResolveRetrievalMissionsUnlocked(db, tx, worldId, result.World.CurrentTurn, header.Seed, now, result.Report);

            // world-action-economy `budget-debit` §Design 5 corrected: `MovementRemaining` IS
            // hashed (`WorldCanonical.cs:61`), and the hash inside `result` was taken at `Step`
            // return (pre-debit). The pass above spent next-turn budgets in the DB (same tx);
            // apply the same spends to the in-memory post-Step world and re-hash — the stored
            // hash always describes the committed state, never the mid-commit state. Zero priced
            // kinds: no debit fired, the re-hash is a fixed point. In-memory, never a reload:
            // the reload would lose post-Step Intel the diff never persists, while the Step
            // world already carries it.
            var postDebitWorld = postCasualtyWorld;
            var debits = ListActDebitsUnlocked(db, tx, worldId, turn);
            if (debits.Count != 0)
            {
                var spendByEntity = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var d in debits)
                {
                    if (d.EntityId is null || d.Cost == 0)
                        continue;
                    spendByEntity.TryGetValue(d.EntityId, out var acc);
                    spendByEntity[d.EntityId] = checked(acc + (long)d.Cost);
                }
                if (spendByEntity.Count != 0)
                {
                    postDebitWorld = postDebitWorld with
                    {
                        Entities = postDebitWorld.Entities.Select(e =>
                            spendByEntity.TryGetValue(e.EntityId, out var spend)
                                ? e with { MovementRemaining = checked((int)checked((long)e.MovementRemaining - spend)) }
                                : e).ToList(),
                    };
                }
            }
            var postHash = StateHasher.Hash(postDebitWorld);

            using (var log = db.CreateCommand())
            {
                log.Transaction = tx;
                log.CommandText = """
                    INSERT OR REPLACE INTO rpg_world_turn_log
                        (world_id, turn, state_hash, engine_version, ruleset_version, seed, committed_utc, report_json, phases_json)
                    VALUES ($w, $t, $hash, $ev, $rv, $seed, $now, $report, $phases);
                    """;
                log.Parameters.AddWithValue("$w", worldId);
                log.Parameters.AddWithValue("$t", turn);
                log.Parameters.AddWithValue("$hash", postHash);
                log.Parameters.AddWithValue("$ev", TurnEngine.EngineVersion);
                log.Parameters.AddWithValue("$rv", TurnEngine.RulesetVersion);
                log.Parameters.AddWithValue("$seed", header.Seed.ToString());
                log.Parameters.AddWithValue("$now", now);
                log.Parameters.AddWithValue("$report", JsonSerializer.Serialize(result.Report.Entries));
                log.Parameters.AddWithValue("$phases", JsonSerializer.Serialize(result.Report.Phases));
                log.ExecuteNonQuery();
            }

            using (var advance = db.CreateCommand())
            {
                advance.Transaction = tx;
                advance.CommandText = """
                    UPDATE rpg_worlds
                    SET current_turn = $next, last_advanced_utc = $now, revision = revision + 1
                    WHERE world_id = $w;
                    """;
                advance.Parameters.AddWithValue("$w", worldId);
                advance.Parameters.AddWithValue("$next", result.World.CurrentTurn);
                advance.Parameters.AddWithValue("$now", now);
                advance.ExecuteNonQuery();
            }

            // deployment-hierarchy module 4 (`cache-decay-void`, spec-cache-decay-void.md
            // §Design 3): the decay clock ticks on the world's turn, in this same commit, reusing
            // `header.Seed` (the value `TurnEngine.Step` just used) and the newly-committed turn.
            TickCorpseCacheDecayForPlayerUnlocked(db, tx, header.PlayerId, result.World.CurrentTurn, header.Seed, now);

            tx.Commit();
            TrimWorldTurnReportsUnlocked(db, worldId, ReportHotTail);
            return new WorldTurnCommitResult(true, "advanced", true, postHash);
        }
    }

    /// <summary>
    /// commander-roster EP3.12 (`spec-legion-commander.md` "Casualty — no commander special"): a
    /// commander member reduced to zero in a siege is handled as **any unique** is — no
    /// commander-specific stat path, no second rule. **`injury-tiers` (`deployment-hierarchy`) is the
    /// program that decides how a unique is wounded or killed**; until it ships, this is the named
    /// interim rule: the fallen commander is **detached** from its legion and its specimen is set
    /// `Recovering`, the non-lethal default the delve's own downed-unique path already writes
    /// (`BeginUniqueRecoveryUnlocked`). This writes the PHASE only — never a
    /// `rpg_unique_actor_recovery` count, which is wound bookkeeping `injury-tiers` owns, so a siege
    /// casualty is `Recovering` with no recovery row until that program lands.
    ///
    /// <para><b>Why the caller passes two worlds.</b> A death has two shapes and only the pair sees
    /// both: a member whose new wounds reach its full `Hp` is REMOVED from the survivor list by
    /// `DistrictAssaultResolver.BuildSideOutcome` (the ordinary path), while a member that entered
    /// already at zero effective HP is kept and simply never fielded. So a `Commander` member present
    /// before the step and absent after — or still present at zero — is a casualty, and nothing else
    /// is. A `Fighter` is untouched here (the survivor list already handles it), and a `Bearer`
    /// carries cargo, not a specimen, so neither has a phase to move.</para>
    /// </summary>
    internal WorldState ApplyCommanderCasualtiesUnlocked(
        SqliteConnection db, SqliteTransaction tx, WorldState before, WorldState after, string now)
    {
        var casualties = new List<string>();
        var detach = new HashSet<(string EntityId, string InstanceId)>();

        foreach (var entityBefore in before.Entities)
        {
            var entityAfter = after.Entities.FirstOrDefault(e =>
                string.Equals(e.EntityId, entityBefore.EntityId, StringComparison.Ordinal));

            foreach (var member in entityBefore.Members)
            {
                if (member.Role != WorldEntityMemberRole.Commander) continue;
                if (member.InstanceId is not { Length: > 0 } instanceId) continue;

                var stillThere = entityAfter?.Members.FirstOrDefault(m =>
                    string.Equals(m.InstanceId, instanceId, StringComparison.Ordinal));
                // Gone from its legion (it died in the fight) or left standing at zero effective HP.
                if (stillThere is not null && Math.Max(0L, stillThere.Hp - stillThere.Wounds) > 0) continue;

                casualties.Add(instanceId);
                if (stillThere is not null) detach.Add((entityBefore.EntityId, instanceId));
            }
        }

        if (casualties.Count == 0) return after;

        foreach (var instanceId in casualties)
        {
            var row = ReadUniqueActorUnlocked(db, instanceId, tx);
            // A retired specimen stays retired, and a deploying one is mid-flight: neither is a live
            // roster member this pass may move — the same pair `BeginUniqueRecoveryUnlocked` refuses.
            if (row is null
                || string.Equals(row.Phase, UniqueActorPhases.Retired, StringComparison.Ordinal)
                || string.Equals(row.Phase, UniqueActorPhases.Deploying, StringComparison.Ordinal)) continue;

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE rpg_unique_actors SET
                  phase = $phase, match_key = NULL, last_ptr = NULL, deploy_correlation_id = NULL,
                  revision = revision + 1, updated_utc = $now
                WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Recovering);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.ExecuteNonQuery();
        }

        if (detach.Count == 0) return after;

        return after with
        {
            Entities = after.Entities.Select(e =>
                e.Members.Any(m => m.InstanceId is { } id && detach.Contains((e.EntityId, id)))
                    ? e with
                    {
                        Members = e.Members
                            .Where(m => m.InstanceId is not { } id || !detach.Contains((e.EntityId, id)))
                            .ToList(),
                    }
                    : e).ToList(),
        };
    }

    public WorldTurnLogRow? GetWorldTurnLog(string worldId, int turn)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT turn, state_hash, engine_version, ruleset_version, seed, committed_utc, report_json, phases_json
                FROM rpg_world_turn_log WHERE world_id = $w AND turn = $t;
                """;
            cmd.Parameters.AddWithValue("$w", worldId);
            cmd.Parameters.AddWithValue("$t", turn);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new WorldTurnLogRow(
                r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                ulong.Parse(r.GetString(4)), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7));
        }
    }

    /// <summary>
    /// A turn's report: served from the hot tail when it is still stored, and otherwise **re-derived**
    /// by replaying the world from turn zero with its recorded command log. The engine is
    /// deterministic, so the re-derived report is the same one that was written — which is why the
    /// log does not have to keep every report forever.
    ///
    /// Re-derivation refuses across a version change rather than fabricating a report the current
    /// engine would not have produced.
    /// </summary>
    public TurnReport? GetWorldTurnReport(string worldId, int turn)
    {
        var log = GetWorldTurnLog(worldId, turn);
        if (log is null) return null;

        if (log.ReportJson is { } json)
        {
            var entries = JsonSerializer.Deserialize<List<TurnReportEntry>>(json) ?? new List<TurnReportEntry>();

            // A row committed before `phases_json` existed has no phase list to trust — fall back to
            // the lossy entries-only reconstruction rather than refusing a report that is otherwise
            // still perfectly good.
            if (log.PhasesJson is { } phasesJson)
            {
                var phases = JsonSerializer.Deserialize<List<string>>(phasesJson) ?? new List<string>();
                return TurnReport.FromStored(phases, entries);
            }

            return TurnReport.FromEntries(entries);
        }

        if (log.EngineVersion != TurnEngine.EngineVersion || log.RulesetVersion != TurnEngine.RulesetVersion)
            return null;

        var header = GetWorldHeader(worldId);
        if (header is null) return null;
        // party-dungeon delve-scope: a delve's TemplateId is a layout id, not a map size tier —
        // WorldTemplateCatalog.Build would throw on it. Refuse on purpose rather than fail loudly
        // by accident (replay is never meaningful for a delve: TurnEngine.Step never runs on one).
        if (header.Kind != "map") return null;

        var world = WorldTemplateCatalog.Build(header.TemplateId, header.Seed, worldId);
        TurnReport? replayed = null;
        for (var t = 0; t <= turn; t++)
        {
            var result = TurnEngine.Step(world, ListWorldCommands(worldId, t), header.Seed, DistrictAssaultResolver.Instance);
            world = result.World;
            replayed = result.Report;
        }

        return replayed;
    }

    /// <summary>Drops report bodies older than the hot tail. Hashes and versions always stay.</summary>
    public void TrimWorldTurnReports(string worldId, int keepLast)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            TrimWorldTurnReportsUnlocked(db, worldId, keepLast);
        }
    }

    static void TrimWorldTurnReportsUnlocked(SqliteConnection db, string worldId, int keepLast)
    {
        // `budget-debit` constraint, named not assumed: trimming past a turn with priced acts
        // WITHOUT its command log is forbidden — post-trim re-derivation replays Step plus the
        // post-Step pass from the never-trimmed command log (debit-once per CommandId, so the
        // replay converges to the same post-debit hash). Commands are never trimmed and neither
        // are `rpg_world_act_debit` rows, so this UPDATE (report bodies only) is always safe.
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_turn_log SET report_json = NULL, phases_json = NULL
            WHERE world_id = $w AND report_json IS NOT NULL AND turn <= (
              SELECT COALESCE(MAX(turn), -1) - $keep FROM rpg_world_turn_log WHERE world_id = $w
            );
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$keep", keepLast);
        cmd.ExecuteNonQuery();
    }

    public WorldHeaderRow? GetWorldHeader(string worldId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadWorldHeaderUnlocked(db, worldId);
        }
    }

    /// <summary>
    /// One stored row back into a command. Shared by both listers: the payload's shape is the thing
    /// most likely to drift, and it should drift in exactly one place.
    /// </summary>
    static WorldCommand ReadCommandRow(string commanderId, string commandId, string kind, string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<CommandPayload>(payloadJson)
                      ?? new CommandPayload(null, null, null, Array.Empty<string>(), null);

        return new WorldCommand
        {
            CommanderId = commanderId,
            CommandId = commandId,
            Kind = kind,
            EntityId = payload.EntityId,
            SectorId = payload.SectorId,
            SlotIndex = payload.SlotIndex,
            Stance = payload.Stance,
            LanePath = payload.LanePath ?? Array.Empty<string>(),
            Amount = payload.Amount,
            StructureId = payload.StructureId,
            WardenId = payload.WardenId,
            ProjectId = payload.ProjectId,
            // A row written before RelicInstanceIds existed has no list to trust — empty, never
            // null: an absent list reads as "spends no relic", exactly the behaviour those orders
            // already had.
            RelicInstanceIds = payload.RelicInstanceIds ?? Array.Empty<string>(),
            // empire-inventory-surfaces `cargo-commands` §Design 2: the same absent→null
            // discipline — every field optional, so a pre-4A.1 row (no such keys at all)
            // deserializes with all seven null, exactly the orders those rows already were.
            Seq = payload.Seq,
            TargetEntityId = payload.TargetEntityId,
            CacheId = payload.CacheId,
            CargoKind = payload.CargoKind,
            InstanceId = payload.InstanceId,
            ContainerId = payload.ContainerId,
            Qty = payload.Qty
        };
    }

    static List<string> ReadCommittersUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT commander_id FROM rpg_world_turn_commits WHERE world_id = $w AND turn = $t;";
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$t", turn);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    static List<WorldCommand> ListWorldCommandsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, int turn)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT commander_id, command_id, kind, payload_json
            FROM rpg_world_commands
            WHERE world_id = $w AND turn = $t
            ORDER BY commander_id, seq;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$t", turn);

        var list = new List<WorldCommand>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadCommandRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));

        return list;
    }
}

/// <summary>An order as the log holds it, with the reasoning an AI attached to it.</summary>
public sealed record LoggedWorldCommand(WorldCommand Command, string? Reason);

/// <summary>Outcome of a commit: did it land, and did it release the turn?</summary>
public sealed record WorldTurnCommitResult(bool Ok, string Reason, bool Advanced, string? StateHash);

/// <summary>A turn's durable record. `ReportJson`/`PhasesJson` are null once the body has been
/// trimmed — always both together, never one without the other (`TrimWorldTurnReportsUnlocked`).
/// `PhasesJson` is also null on its own for a row committed before it existed; that legacy case
/// falls back to `TurnReport.FromEntries`'s lossy reconstruction rather than refusing.</summary>
public sealed record WorldTurnLogRow(
    int Turn, string StateHash, int EngineVersion, int RulesetVersion,
    ulong Seed, string CommittedUtc, string? ReportJson, string? PhasesJson);

/// <summary>Per-command result of a submission — a batch never fails as a whole.</summary>
public sealed record WorldCommandOutcome(string CommandId, bool Ok, string Reason, bool Replayed);
