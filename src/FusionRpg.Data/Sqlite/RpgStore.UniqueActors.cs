using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>EP1.15 (spec-default-build.md, cache trigger T3) — <c>ObserveUniqueActorEvents</c>'s own
/// result, widened past T6.1's original bare player-id list without breaking its shape: the affected
/// players (T6.1) and, new, the specimens this same batch leveled (T3) — the server caller broadcasts
/// <c>AptitudesUpdated(unique, instanceId)</c> for exactly the latter.</summary>
public sealed record UniqueActorEventsResult(
    IReadOnlyList<long> AffectedPlayerIds,
    IReadOnlyList<string> LeveledSpecimenIds);

public sealed partial class RpgStore
{
    public UniqueActorDto CreateUniqueActor(long playerId, string side, int typeId)
    {
        var s = NormalizeUniqueSide(side);
        if (typeId < 0) throw new ArgumentOutOfRangeException(nameof(typeId));
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null)
                throw new InvalidOperationException("player not found");
            var now = ServerClock.UtcNowDateTime.ToString("o");
            var id = Guid.NewGuid().ToString("N");
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO rpg_unique_actors(
                      instance_id, player_id, empire_id, side, type_id, phase, level, xp,
                      match_key, last_ptr, deploy_correlation_id, revision, created_utc, updated_utc)
                    VALUES($id, $pid, $emp, $side, $type, $phase, 1, 0, NULL, NULL, NULL, 0, $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$pid", playerId);
                // save-identity SE4.14/SE4.25: this path predates the empire_id column and never
                // stamped it — invisible until SE4.25 wired the first real ownership-predicate reader
                // (ItemEquipEndpoints) against a specimen this method minted, and every one of them
                // failed `OwnsSpecimenUnlocked`'s "NULL never matches" rule. Same default as
                // MintCreatureUnlocked: the save's human empire.
                cmd.Parameters.AddWithValue("$emp", (object?)HumanEmpireOfOrNull(db, playerId)?.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$side", s);
                cmd.Parameters.AddWithValue("$type", typeId);
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
            return ReadUniqueActorUnlocked(db, id)!;
        }
    }

    /// <summary>
    /// Debug/audit upsert: stable <paramref name="instanceId"/> for the current player's playable
    /// derived-sheet specimen. Sets level (progression surface) without inventing derived values.
    ///
    /// <para><b>Every row it writes is marked <c>debug_seeded</c> (debug-origin D1).</b> A debug API may
    /// trigger a real operation, but its result must never be mistaken for gameplay: without the marker
    /// this specimen sat in the player's own <see cref="ListUniqueActors(EmpireRef)"/> roster as
    /// <c>derived-audit</c>, which is a debug fixture wearing a gameplay costume. The marker is set on
    /// BOTH branches — insert and the ensure-update — so a re-run of the debug seed also re-stamps a row
    /// an older build created, which is the one legacy case a data-driven marker can reach without
    /// guessing at an id it did not write. The capability is untouched: this row is still a real
    /// UniqueActor, still resolvable by id (<see cref="GetUniqueActor"/>), still sheet-composable and
    /// still the subject of the derived coverage audit — it is only absent from the ROSTER read.</para>
    /// </summary>
    public UniqueActorDto EnsureUniqueActorForAudit(
        long playerId, string instanceId, string side, int typeId, long level)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("instanceId required", nameof(instanceId));
        if (typeId < 0) throw new ArgumentOutOfRangeException(nameof(typeId));
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        var s = NormalizeUniqueSide(side);
        var id = instanceId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null)
                throw new InvalidOperationException("player not found");
            var existing = ReadUniqueActorUnlocked(db, id);
            var now = ServerClock.UtcNowDateTime.ToString("o");
            if (existing is not null)
            {
                if (existing.PlayerId != playerId)
                    throw new InvalidOperationException("unique actor belongs to another player");
                // "Ensure" means a KNOWN state, so this branch resets the same fields the INSERT
                // below sets to their known values -- phase and the three bind columns included.
                // Before 2026-09-17 it left them alone, which was invisible: nothing read the audit
                // actor's phase. `RpgStore.Items.SaveAssignment` now refuses a non-`Roster` specimen
                // (the deploy-time-snapshot rule), and `DerivedAuditActor.BindMultiChannelEquip`
                // assigns gear to THIS actor under a fixed instance id -- so an audit actor left
                // `ActiveBound` or `Retired` by an earlier debug deploy would make the seed throw on
                // its next run, and stay broken. Resetting is correct for a seed and is scoped to the
                // audit path: the ordinary deploy state machine does not come through here.
                using var upd = db.CreateCommand();
                upd.CommandText = """
                    UPDATE rpg_unique_actors SET
                      side = $side, type_id = $type, level = $lvl, xp = 0,
                      phase = $phase, match_key = NULL, last_ptr = NULL, deploy_correlation_id = NULL,
                      debug_seeded = 1,
                      revision = revision + 1, updated_utc = $now
                    WHERE instance_id = $id;
                    """;
                upd.Parameters.AddWithValue("$side", s);
                upd.Parameters.AddWithValue("$type", typeId);
                upd.Parameters.AddWithValue("$lvl", level);
                upd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
                upd.Parameters.AddWithValue("$now", now);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                return ReadUniqueActorUnlocked(db, id)!;
            }

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO rpg_unique_actors(
                      instance_id, player_id, empire_id, side, type_id, phase, level, xp,
                      match_key, last_ptr, deploy_correlation_id, debug_seeded, revision, created_utc, updated_utc)
                    VALUES($id, $pid, $emp, $side, $type, $phase, $lvl, 0, NULL, NULL, NULL, 1, 0, $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$pid", playerId);
                // save-identity SE4.14/SE4.25: same missing stamp as CreateUniqueActor, same fix.
                cmd.Parameters.AddWithValue("$emp", (object?)HumanEmpireOfOrNull(db, playerId)?.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$side", s);
                cmd.Parameters.AddWithValue("$type", typeId);
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
                cmd.Parameters.AddWithValue("$lvl", level);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
            return ReadUniqueActorUnlocked(db, id)!;
        }
    }

    public UniqueActorDto? GetUniqueActor(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadUniqueActorUnlocked(db, instanceId.Trim());
        }
    }

    /// <summary>The current save's human empire's own roster — see
    /// <see cref="ListUniqueActors(EmpireRef)"/>, the classified-by-purpose read this
    /// defaults to (save-identity SE4.26, "an empire's roster").</summary>
    public UniqueActorListDto ListUniqueActors(long playerId) =>
        ListUniqueActors(new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId)));

    /// <summary>
    /// save-identity SE4.26 ("Specimen reads are classified by purpose") — an EMPIRE's own roster: the
    /// UI, the deploy roster, fusion inputs, patron, expeditions and respec all ask this question, and
    /// the answer must never include another empire's specimen just because it shares the save's row.
    /// The match's RUNTIME question (every side on the board) is a different read —
    /// `AtomPushService.OwnersForSave`, never this one.
    ///
    /// <para><b>debug-origin D1: a DEBUG-seeded specimen is not in the roster.</b> Filtered by the
    /// <c>debug_seeded</c> marker <see cref="EnsureUniqueActorForAudit"/> stamps, not by a hardcoded id,
    /// so a fixture cannot reappear under a renamed constant. The exclusion is here and ONLY here: the
    /// roster is the player-facing surface, and every by-id read (<see cref="GetUniqueActor"/>,
    /// <c>/api/actors/{id}/sheet</c>, the derived coverage audit) still resolves the row — the debug
    /// capability is narrowed, never deleted.</para>
    /// </summary>
    public UniqueActorListDto ListUniqueActors(EmpireRef owner)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var items = new List<UniqueActorDto>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT instance_id, player_id, side, type_id, phase, level, xp,
                       match_key, last_ptr, deploy_correlation_id, revision, created_utc, updated_utc,
                       empire_id
                FROM rpg_unique_actors
                WHERE player_id = $pid AND empire_id = $emp AND COALESCE(debug_seeded, 0) = 0
                ORDER BY created_utc ASC;
                """;
            cmd.Parameters.AddWithValue("$pid", owner.Save.Value);
            cmd.Parameters.AddWithValue("$emp", owner.Empire.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                items.Add(MapUniqueActor(r));
            return new UniqueActorListDto { PlayerId = owner.Save.Value, Items = items };
        }
    }

    /// <summary>Roster → Deploying. Same correlationId while Deploying is idempotent (ok, queued=false).</summary>
    public (bool Ok, string Reason, UniqueActorDto? Actor, bool Queued) TryBeginUniqueDeploy(
        string instanceId, string correlationId, string? matchKey = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(correlationId))
            return (false, "bad_args", null, false);
        var id = instanceId.Trim();
        var corr = correlationId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var row = ReadUniqueActorUnlocked(db, id);
            if (row is null) return (false, "not_found", null, false);

            if (string.Equals(row.Phase, UniqueActorPhases.Deploying, StringComparison.Ordinal) &&
                string.Equals(row.DeployCorrelationId, corr, StringComparison.Ordinal))
                return (true, "", row, false);

            // Cross-mode soft-lock (spec-expeditions.md): a specimen on an active expedition
            // cannot be PvZ-deployed; the mirror check lives in DispatchExpedition.
            if (HasActiveExpeditionMembershipUnlocked(db, id))
                return (false, "expedition.locked", row, false);

            // creature-lawn-deploy T1.1: the active Patron is unconsumable in fusion
            // (RpgStore.Fusion.cs:354) for the same reason it must never ALSO get free lawn combat
            // value on top of its aura — the two economies would otherwise stack. Reuses the exact
            // same IsPatronUnlocked check, same instance-keyed shape.
            //
            // commander-roster EP3.6 (spec-lawn-commander-seat.md "The canary branch"): a specimen
            // holding THIS RUN's commander seat is refused BEFORE the phase check, so the reason names
            // the rule rather than a phase. The restriction belongs to the place and the run, never to
            // the role: a role-holder that is not leading this run deploys like any unique.
            if (IsSeatedCommanderUnlocked(db, id))
                return (false, "commander.cannot-deploy", row, false);

            if (IsPatronUnlocked(db, row.PlayerId, id))
                return (false, "patron.cannot-deploy", row, false);

            // commander-roster EP3.9 (spec-legion-commander.md "The parent rule (R-C1)"): a child starts
            // from HOME or from a STATIONED legion, never from one that is marching — the one parent
            // question every child admission asks, read from the world graph rather than from a cached
            // flag. `Roster` alone cannot answer it: an attached commander is still Roster while its
            // legion marches.
            if (!ParentOfUnlocked(db, id).AdmitsChild)
                return (false, "not-at-base", row, false);

            if (!string.Equals(row.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal))
                return (false, "phase." + row.Phase.ToLowerInvariant(), row, false);

            // Contract gate — creatures only. A unique actor without a creature profile predates this
            // module entirely and deploys exactly as it always did.
            var creatureProfile = ReadCreatureProfileUnlocked(db, id);
            if (creatureProfile != null)
            {
                // save-identity SE4.23: contracts are the HUMAN summoner's binding slots, loyalty and
                // tribute — an AI empire's own specimen (Zomboss's mint) is gated by its own deploy
                // policy, never this economy (D6: without this, every AI mint auto-bound on the shared
                // row, and the 13th refused `contract.unbound`).
                if (IsHumanEmpireSpecimenUnlocked(db, row.PlayerId, id))
                {
                    var contract = ContractViewUnlocked(db, row.PlayerId, id);
                    if (!contract.Bound) return (false, "contract.unbound", row, false);
                    if (!contract.Deployable) return (false, "contract.insubordinate", row, false);
                }

                // creature-lawn-deploy T1.4: owner-confirmed 2026-09-06 — a creature's own species side/typeId
                // pass through UNCHANGED for deploy (no override needed): PvZ's own engine makes any
                // spawned zombie-type entity hostile to the plant side by construction, and only the
                // game's native hypnotize operation flips that. `PlantAvatar` species need nothing extra
                // (they are plant-side already, or "most creatures deploy as plant-side avatars" per
                // creature-system-map.md line 7 — this repo's own real anchor corpus is overwhelmingly
                // plant-side content, matching that framing). `HypnoAlly` species are the harder case:
                // making a spawned zombie fight FOR its owner needs that native hypnotize call, and this
                // codebase ALREADY investigated it once (content-stack program,
                // tasks/content-stack-todo.md: "no UnMindControl/ClearMindControl-shaped method... mind
                // control is documented elsewhere as a side-swap, not a flag") and explicitly refused to
                // guess at unverified gameplay-critical Unity state rather than ship an untested fix.
                // Matching that same discipline here: HypnoAlly deploy is a NAMED refusal, not a silent
                // gap or an unverified guess, until that native-hypnotize problem is solved for real.
                if (CreatureSpeciesCatalog.IsKnown(creatureProfile.SpeciesId) &&
                    CreatureSpeciesCatalog.Get(creatureProfile.SpeciesId).DeployMode == CreatureDeployMode.HypnoAlly)
                    return (false, "deploy.hypno-ally-not-implemented", row, false);
            }

            // creature-lawn-deploy T1.2: reconcile trait bindings on every deploy that actually proceeds
            // past every refusal check above — never at mint, so a specimen promoted between two
            // deploys (RpgStore.Fusion.cs's PromotionUnlocked, same instanceId, appended traits) always
            // gets its current traits bound, not a stale mint-time snapshot. A no-op for a unique
            // actor with no creature profile (ReconcileCreatureTraitBindingsUnlocked returns immediately).
            ReconcileCreatureTraitBindingsUnlocked(db, id);
            // creature-lawn-deploy T1.5: same reasoning, for the specimen's own species magnitudes.
            ReconcileCreatureMagnitudeBindingsUnlocked(db, id);

            var now = ServerClock.UtcNowDateTime.ToString("o");
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE rpg_unique_actors SET
                      phase = $phase,
                      deploy_correlation_id = $corr,
                      match_key = $mk,
                      last_ptr = NULL,
                      revision = revision + 1,
                      updated_utc = $now
                    WHERE instance_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Deploying);
                cmd.Parameters.AddWithValue("$corr", corr);
                cmd.Parameters.AddWithValue("$mk", (object?)NullIfEmpty(matchKey) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            return (true, "", ReadUniqueActorUnlocked(db, id), true);
        }
    }

    /// <summary>Deploying → ActiveBound on spawn ack (by correlationId).</summary>
    public (bool Ok, string Reason, UniqueActorDto? Actor) TryAckUniqueSpawn(
        string correlationId, string ptr, string? matchKey = null, long? activeMatchMs = null)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || string.IsNullOrWhiteSpace(ptr))
            return (false, "bad_args", null);
        if (activeMatchMs is < 0)
            return (false, "bad_active_match_ms", null);
        var corr = correlationId.Trim();
        var p = ptr.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var row = FindUniqueByCorrelationUnlocked(db, corr);
            if (row is null) return (false, "not_found", null);
            if (!string.Equals(row.Phase, UniqueActorPhases.Deploying, StringComparison.Ordinal))
                return (false, "phase." + row.Phase.ToLowerInvariant(), row);

            var now = ServerClock.UtcNowDateTime.ToString("o");
            var mk = NullIfEmpty(matchKey) ?? row.MatchKey;
            if (!string.IsNullOrWhiteSpace(mk))
            {
                using var collision = db.CreateCommand();
                collision.Transaction = tx;
                collision.CommandText = """
                    SELECT instance_id FROM rpg_unique_lawn_sessions
                    WHERE match_key = $mk AND instance_id <> $id
                      AND ((correlation_id IS NOT NULL AND correlation_id = $corr)
                           OR (ptr IS NOT NULL AND ptr = $ptr))
                    LIMIT 1;
                    """;
                collision.Parameters.AddWithValue("$mk", mk!);
                collision.Parameters.AddWithValue("$id", row.InstanceId);
                collision.Parameters.AddWithValue("$corr", corr);
                collision.Parameters.AddWithValue("$ptr", p);
                if (collision.ExecuteScalar() is not null and not DBNull)
                    return (false, "binding.collision", row);
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE rpg_unique_actors SET
                      phase = $phase,
                      last_ptr = $ptr,
                      match_key = $mk,
                      revision = revision + 1,
                      updated_utc = $now
                    WHERE instance_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.ActiveBound);
                cmd.Parameters.AddWithValue("$ptr", p);
                cmd.Parameters.AddWithValue("$mk", (object?)mk ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", row.InstanceId);
                cmd.ExecuteNonQuery();
            }
            if (!string.IsNullOrWhiteSpace(mk))
            {
                using var session = db.CreateCommand();
                session.Transaction = tx;
                session.CommandText = """
                    INSERT INTO rpg_unique_lawn_sessions(instance_id, player_id, match_key, bound_utc, correlation_id, ptr, bound_active_ms, seat)
                    VALUES($id, $p, $mk, $t, $corr, $ptr, $activeMs, $seat)
                    ON CONFLICT(instance_id) DO UPDATE SET
                      player_id=$p, match_key=$mk, bound_utc=$t, correlation_id=$corr, ptr=$ptr,
                      bound_active_ms=$activeMs, seat=$seat;
                    """;
                // commander-roster EP3.4: an ordinary bound actor is seated on the BOARD. A commander
                // seat row is written by the seat wire (EP3.5); the column exists here so the two are
                // distinguishable, and every pre-EP3.4 row reads 'board'.
                session.Parameters.AddWithValue("$id", row.InstanceId);
                session.Parameters.AddWithValue("$p", row.PlayerId);
                session.Parameters.AddWithValue("$mk", mk!);
                session.Parameters.AddWithValue("$t", now);
                session.Parameters.AddWithValue("$corr", corr);
                session.Parameters.AddWithValue("$ptr", p);
                session.Parameters.AddWithValue("$activeMs", (object?)activeMatchMs ?? DBNull.Value);
                session.Parameters.AddWithValue("$seat", UniqueLawnSeats.Board);
                session.ExecuteNonQuery();
            }
            tx.Commit();
            return (true, "", ReadUniqueActorUnlocked(db, row.InstanceId));
        }
    }

    /// <summary>Deploying → Roster (spawn failed).</summary>
    public (bool Ok, string Reason, UniqueActorDto? Actor) TryFailUniqueDeploy(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return (false, "bad_args", null);
        var id = instanceId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var row = ReadUniqueActorUnlocked(db, id);
            if (row is null) return (false, "not_found", null);
            if (!string.Equals(row.Phase, UniqueActorPhases.Deploying, StringComparison.Ordinal))
                return (false, "phase." + row.Phase.ToLowerInvariant(), row);
            ClearBindToRosterUnlocked(db, id);
            return (true, "", ReadUniqueActorUnlocked(db, id));
        }
    }

    public (bool Ok, string Reason, UniqueActorDto? Actor) TryRetireUniqueActor(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return (false, "bad_args", null);
        var id = instanceId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            // The pre-retire row carries the death place: a bound specimen's match_key. Read
            // BEFORE the retire clears it (RetireUniqueActorUnlocked sets match_key = NULL).
            var before = ReadUniqueActorUnlocked(db, id);
            var (ok, reason) = RetireUniqueActorUnlocked(db, tx, id);
            if (ok)
            {
                // deployment-hierarchy module 3, death path (spec-corpse-cache.md §Design 2): the
                // specimen's rolled gear moves into a lawn cache in THIS transaction, never a
                // second commit. A retire with no match_key (Roster retire, never deployed) still
                // moves — into a deterministically-keyed fallback cache, never left equipped on a
                // dead specimen. Runs on the idempotent already-Retired path too (self-healing for
                // rows retired before this module existed); a no-op when nothing is movable, since
                // the move creates no empty cache row.
                var mk = before?.MatchKey;
                var placeRef = string.IsNullOrWhiteSpace(mk) ? "unplaced:" + id : mk!;
                MoveAssignedGearToCorpseCacheUnlocked(db, tx, id, "lawn", placeRef, "death");
            }
            tx.Commit();
            return (ok, reason, ReadUniqueActorUnlocked(db, id));
        }
    }

    /// <summary>The core of <see cref="TryRetireUniqueActor"/>, reusable from a caller that already
    /// holds an open connection (party-dungeon D2.23 — extraction settlement needs this on the SAME
    /// connection/transaction its own writes are in, exactly like <see cref="AwardUniqueActorXpUnlocked"/>
    /// already is for the expedition reward apply). Idempotent on an already-`Retired` row.</summary>
    (bool Ok, string Reason) RetireUniqueActorUnlocked(SqliteConnection db, SqliteTransaction? tx, string instanceId)
    {
        var row = ReadUniqueActorUnlocked(db, instanceId);
        if (row is null) return (false, "not_found");
        if (string.Equals(row.Phase, UniqueActorPhases.Retired, StringComparison.Ordinal))
            return (true, "");
        if (string.Equals(row.Phase, UniqueActorPhases.Deploying, StringComparison.Ordinal) ||
            string.Equals(row.Phase, UniqueActorPhases.Recovering, StringComparison.Ordinal))
            return (false, "phase." + row.Phase.ToLowerInvariant());

        var now = ServerClock.UtcNowDateTime.ToString("o");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE rpg_unique_actors SET
              phase = $phase,
              match_key = NULL,
              last_ptr = NULL,
              deploy_correlation_id = NULL,
              revision = revision + 1,
              updated_utc = $now
            WHERE instance_id = $id;
            """;
        cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Retired);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.ExecuteNonQuery();
        return (true, "");
    }

    /// <summary>
    /// party-dungeon D2.23 (spec-delve-attrition.md §7) — the FIRST real, persisted `Recovering`
    /// (unlike W4's lawn-death pass-through, `RecoverToRosterUnlocked`, which never actually writes
    /// this phase). Same-connection, called from `CloseDelve`'s own open transaction. Refuses a
    /// `Retired`/`Deploying` row for the identical reason <see cref="RetireUniqueActorUnlocked"/>
    /// refuses those two — neither state is a live roster member a delve wound can touch.
    /// </summary>
    (bool Ok, string Reason) BeginUniqueRecoveryUnlocked(
        SqliteConnection db, long playerId, string instanceId, int recoveryDelvesLeft, long woundedDelveId, int thetaRun)
    {
        if (recoveryDelvesLeft <= 0)
            throw new ArgumentOutOfRangeException(nameof(recoveryDelvesLeft), recoveryDelvesLeft, "a recovery count is never zero or negative");

        var row = ReadUniqueActorUnlocked(db, instanceId);
        if (row is null) return (false, "not_found");
        if (string.Equals(row.Phase, UniqueActorPhases.Retired, StringComparison.Ordinal) ||
            string.Equals(row.Phase, UniqueActorPhases.Deploying, StringComparison.Ordinal))
            return (false, "phase." + row.Phase.ToLowerInvariant());

        var now = ServerClock.UtcNowDateTime.ToString("o");
        using (var cmd = db.CreateCommand())
        {
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

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rpg_unique_actor_recovery(instance_id, player_id, recovery_delves_left, wounded_delve_id, theta_run)
                VALUES($id, $pid, $left, $delve, $theta)
                ON CONFLICT(instance_id) DO UPDATE SET
                  recovery_delves_left = $left, wounded_delve_id = $delve, theta_run = $theta;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.Parameters.AddWithValue("$pid", playerId);
            cmd.Parameters.AddWithValue("$left", recoveryDelvesLeft);
            cmd.Parameters.AddWithValue("$delve", woundedDelveId);
            cmd.Parameters.AddWithValue("$theta", thetaRun);
            cmd.ExecuteNonQuery();
        }

        return (true, "");
    }

    /// <summary>
    /// R6 — virtual time, no clock: every `CloseDelve` (any delve, Extracted or Wiped) decrements
    /// EVERY `Recovering` row this player owns by one delve, in the same transaction. A row that
    /// reaches zero flips straight back to `Roster` and its recovery row is deleted in the same
    /// write — "0 flips the creature to Roster in the same write" (spec §7), never a separate sweep.
    /// </summary>
    void DecrementAllRecoveringForPlayerUnlocked(SqliteConnection db, long playerId, string now)
    {
        var ids = new List<(string InstanceId, int Left)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT instance_id, recovery_delves_left FROM rpg_unique_actor_recovery WHERE player_id = $pid;";
            cmd.Parameters.AddWithValue("$pid", playerId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add((r.GetString(0), r.GetInt32(1)));
        }

        foreach (var (id, left) in ids)
        {
            var next = left - 1;
            if (next > 0)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE rpg_unique_actor_recovery SET recovery_delves_left = $left WHERE instance_id = $id;";
                cmd.Parameters.AddWithValue("$left", next);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                continue;
            }

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM rpg_unique_actor_recovery WHERE instance_id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE rpg_unique_actors SET phase = $phase, revision = revision + 1, updated_utc = $now
                    WHERE instance_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>The one row of `rpg_unique_actor_recovery` a caller (the recovery-ritual endpoint, a
    /// roster read) needs — null when the actor is not `Recovering` (or was never wounded).</summary>
    public (int RecoveryDelvesLeft, long WoundedDelveId, int ThetaRun)? GetUniqueActorRecovery(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadUniqueActorRecoveryUnlocked(db, instanceId.Trim());
        }
    }

    static (int RecoveryDelvesLeft, long WoundedDelveId, int ThetaRun)? ReadUniqueActorRecoveryUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT recovery_delves_left, wounded_delve_id, theta_run FROM rpg_unique_actor_recovery WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt64(1), r.GetInt32(2)) : null;
    }

    /// <summary>
    /// party-dungeon D2.23 (spec-delve-attrition.md §7) — "the priced escape": spends
    /// <c>SoulSinkPolicy.Price(risk.recoveryRitualSouls.{rung}, theta_run, tuning)</c> souls, priced
    /// at the WOUNDING delve's own rung and `theta_run` (never the current delve, if any — the row
    /// remembers both since the wounding delve may since have been archived), clears the recovery
    /// row, and writes `Roster` — one transaction, correlation-idempotent exactly like
    /// <see cref="RpgStore.Contracts.PerformRitual"/> (a retry of the same correlation is a replay,
    /// never a second charge). <paramref name="tuning"/> is an explicit parameter, matching
    /// <see cref="CloseDelve"/>'s own shape in this same module — never a static hub read here.
    /// </summary>
    public (bool Ok, string Reason, UniqueActorDto? Actor) TryPerformRecoveryRitual(
        long playerId, string instanceId, string correlationId,
        FusionRpg.Core.Dungeon.Tuning.DungeonTuning tuning, DateTimeOffset? utcNow = null)
    {
        var id = (instanceId ?? "").Trim();
        var corr = (correlationId ?? "").Trim();
        if (id.Length == 0) return (false, "specimen.missing", null);
        if (corr.Length == 0) return (false, "correlation.missing", null);
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            var row = ReadUniqueActorUnlocked(db, id);
            // save-identity SE4.24: the one ownership predicate — a Zomboss specimen of this save must
            // never be recoverable by the human.
            if (row is null || !OwnsSpecimenUnlocked(db, new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId)), row.InstanceId))
                return (false, "not_found", null);

            // Checked BEFORE the phase guard, on purpose: a SUCCESSFUL ritual moves the actor out of
            // Recovering, so a replay of the same correlation arrives with a row that would otherwise
            // fail "must be Recovering" -- the opposite of PerformRitual's own contract-ritual replay,
            // whose state (bound, still ready to re-ritual) never disqualifies itself this way.
            if (HasSoulLedgerEntryUnlocked(db, playerId, SoulEarnPolicy.Reasons.DelveRecoveryRitual, corr))
                return (true, "replay", row);

            if (!string.Equals(row.Phase, UniqueActorPhases.Recovering, StringComparison.Ordinal))
                return (false, "phase." + row.Phase.ToLowerInvariant(), row);

            var recovery = ReadUniqueActorRecoveryUnlocked(db, id);
            if (recovery is null) return (false, "recovery.missing", row); // Recovering with no row is a data defect, not a valid state to price

            var woundedRungId = ReadDelveRungIdUnlocked(db, recovery.Value.WoundedDelveId);
            if (woundedRungId is null || !tuning.RiskRecoveryRitualSouls.TryGetValue(woundedRungId, out var basePriceSouls))
                return (false, "rung.unknown", row);

            using var tx = db.BeginTransaction();
            var price = SoulSinkPolicy.Price(basePriceSouls, recovery.Value.ThetaRun, Core.Power.PowerTuningHub.Tuning);
            if (ReadSoulBalanceUnlocked(db, playerId).Balance < price)
                return (false, "souls.insufficient", row);

            var stamp = now.UtcDateTime.ToString("o");
            if (!AppendSoulLedgerUnlocked(db, playerId, 0, -price, SoulEarnPolicy.Reasons.DelveRecoveryRitual,
                    "unique_actor", id, corr, stamp))
            {
                tx.Commit(); // the correlation already bought this ritual -- a replay, not a second purchase
                return (true, "replay", ReadUniqueActorUnlocked(db, id));
            }

            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM rpg_unique_actor_recovery WHERE instance_id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE rpg_unique_actors SET phase = $phase, revision = revision + 1, updated_utc = $now
                    WHERE instance_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
                cmd.Parameters.AddWithValue("$now", stamp);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return (true, "", ReadUniqueActorUnlocked(db, id));
        }
    }

    /// <summary>Peeks `rpg_soul_ledger`'s own `UNIQUE(player_id, reason, dedupe_key)` without
    /// inserting — the read half of the check <see cref="AppendSoulLedgerUnlocked"/>'s `INSERT OR
    /// IGNORE` makes atomically for a caller that only finds out about the collision from inside its
    /// own state-mutating write.</summary>
    static bool HasSoulLedgerEntryUnlocked(SqliteConnection db, long playerId, string reason, string dedupeKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM rpg_soul_ledger WHERE player_id = $p AND reason = $r AND dedupe_key = $dk LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$dk", dedupeKey);
        return cmd.ExecuteScalar() is not null and not DBNull;
    }

    static string? ReadDelveRungIdUnlocked(SqliteConnection db, long delveId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT rung_id FROM rpg_delves WHERE delve_id = $id;";
        cmd.Parameters.AddWithValue("$id", delveId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Reads the cross-delve-persisted subset of a member's pools (spec §1, S2-7 —
    /// `attrition.persistAcrossDelves[]`, `["hunger"]` today). Empty when the actor has never closed
    /// a delve before — the caller (party assembly) fills every other id from `resource.max`.</summary>
    public IReadOnlyDictionary<string, long> GetUniqueActorPersistedPools(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return new Dictionary<string, long>();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadPersistedPoolsUnlocked(db, instanceId.Trim());
        }
    }

    static IReadOnlyDictionary<string, long> ReadPersistedPoolsUnlocked(SqliteConnection db, string instanceId)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT resource_id, stored FROM rpg_unique_actor_pools WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetString(0)] = r.GetInt64(1);
        return result;
    }

    /// <summary>Writes exactly the ids named by <paramref name="subsetPools"/> — the caller (`CloseDelve`)
    /// has already filtered to `attrition.persistAcrossDelves[]`; this method trusts that filter rather
    /// than re-deriving it, matching this whole module's "read model owned elsewhere" shape. One row
    /// per resource id (spec's own normalized shape), never a JSON blob.</summary>
    static void WritePersistedPoolsUnlocked(SqliteConnection db, string instanceId, IReadOnlyDictionary<string, long> subsetPools)
    {
        foreach (var (resourceId, stored) in subsetPools)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rpg_unique_actor_pools(instance_id, resource_id, stored) VALUES($id, $rid, $stored)
                ON CONFLICT(instance_id, resource_id) DO UPDATE SET stored = $stored;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.Parameters.AddWithValue("$rid", resourceId);
            cmd.Parameters.AddWithValue("$stored", stored);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Observe capture events: ack → ActiveBound; die/end → Recovering → Roster.
    /// Fail-closed; never throws.
    /// </summary>
    /// <returns>
    /// <see cref="UniqueActorEventsResult.AffectedPlayerIds"/> — T6.1 (2026-09-06, `mods-absorption`):
    /// the distinct player ids whose OWN <see cref="UniqueActorPhases.ActiveBound"/> roster changed
    /// (grew or shrank) while processing this batch — empty when nothing transitioned. Callers use
    /// this to re-push <c>AtomPushService.OwnersForSave</c>'s union for exactly the affected players,
    /// the same real gap T6.1's own audit entry named: nothing re-triggered the Hello-time union mid
    /// session, so an item equipped or unequipped after connecting never reached the runner.
    /// <see cref="UniqueActorEventsResult.LeveledSpecimenIds"/> — EP1.15 (cache trigger T3, new): the
    /// specimens a lawn-kill or lawn-duration award leveled while processing this batch.
    /// </returns>
    public UniqueActorEventsResult ObserveUniqueActorEvents(IEnumerable<(string Kind, string? MatchKey, string PayloadJson)> events)
        => ObserveUniqueActorEvents(events.Select(e => (e.Kind, e.MatchKey, e.PayloadJson, (string?)null)));

    public UniqueActorEventsResult ObserveUniqueActorEvents(IEnumerable<(string Kind, string? MatchKey, string PayloadJson, string? EventTime)> events)
    {
        var affected = new List<long>();
        // EP1.15 (spec-default-build.md, cache trigger T3): the specimens THIS batch itself leveled --
        // nothing about their allocation changed, yet the resolved default did, so the caller broadcasts
        // AptitudesUpdated(unique, instanceId) for exactly these, the same trap §2.16 names.
        var leveled = new List<string>();
        try
        {
            foreach (var e in events)
                foreach (var pid in ObserveUniqueActorEvent(e.Kind, e.MatchKey, e.PayloadJson ?? "{}", e.EventTime, leveled))
                    if (!affected.Contains(pid))
                        affected.Add(pid);
        }
        catch
        {
            /* fail-closed */
        }
        return new UniqueActorEventsResult(affected, leveled.Distinct(StringComparer.Ordinal).ToList());
    }

    static readonly IReadOnlyList<long> NoPlayers = Array.Empty<long>();

    /// <returns>Every player id whose ActiveBound roster changed as a result of this one event —
    /// almost always zero or one, but a shared `match_key` recovering more than one specimen at once
    /// is handled without assuming they all belong to the same player.</returns>
    IReadOnlyList<long> ObserveUniqueActorEvent(string kind, string? matchKey, string payloadJson, string? eventTime, List<string> leveledSpecimenIds)
    {
        if (string.IsNullOrWhiteSpace(kind)) return NoPlayers;
        if (string.Equals(kind, "pvz.spawn.extra.ack", StringComparison.OrdinalIgnoreCase))
        {
            var corr = TryString(payloadJson, "correlationId");
            var ptr = TryString(payloadJson, "ptr");
            var activeMatchMs = TryLong(payloadJson, "activeMatchMs");
            if (!string.IsNullOrWhiteSpace(corr) && !string.IsNullOrWhiteSpace(ptr))
            {
                var ack = TryAckUniqueSpawn(corr!, ptr!, matchKey, activeMatchMs);
                if (ack.Ok && ack.Actor is not null) return new[] { ack.Actor.PlayerId };
            }
            return NoPlayers;
        }

        if (string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "MatchStarted", StringComparison.OrdinalIgnoreCase))
        {
            // commander-roster EP3.5 (spec-lawn-commander-seat.md "Who is seated, and when"): the run's
            // frozen leader is seated HERE, from the MatchStarted fact the injector stamps. A refusal is
            // recorded on the run and never gates it — the run proceeds, the frozen aura still applies,
            // and no duration XP is paid for a seat that never happened. The frozen snapshot is the
            // contract, so a default-commander change mid-run moves nothing.
            var leading = FusionRpg.Core.Activity.PvzActivityKinds.LeadingCommanderId(payloadJson);
            if (!string.IsNullOrWhiteSpace(leading) && !string.IsNullOrWhiteSpace(matchKey))
            {
                var seat = SeatLeadingCommander(leading!, matchKey!);
                if (!seat.Seated)
                    Console.WriteLine(
                        $"[commander] seat refused for run {matchKey}: {seat.Reason} ({leading})");
                return seat.PlayerId is { } seatedPlayer ? new[] { seatedPlayer } : NoPlayers;
            }
            return NoPlayers;
        }

        if (string.Equals(kind, "plant.die", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "zombie.die", StringComparison.OrdinalIgnoreCase))
        {
            var ptr = TryString(payloadJson, "ptr");
            if (string.IsNullOrWhiteSpace(ptr)) return NoPlayers;
            var activeMatchMs = TryLong(payloadJson, "activeMatchMs");
            var targetSide = string.Equals(kind, "plant.die", StringComparison.OrdinalIgnoreCase)
                ? "plant" : "zombie";
            return TryRecoverActiveByPtr(
                ptr!, matchKey, TryString(payloadJson, "lifecycleOccurrence"), eventTime,
                TryString(payloadJson, "killerPtr"), targetSide, activeMatchMs, leveledSpecimenIds);
        }

        if (string.Equals(kind, "board.end", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "match.result", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(matchKey)
                ? TryRecoverActiveByMatchKey(matchKey!, eventTime, TryLong(payloadJson, "activeMatchMs"), leveledSpecimenIds)
                : NoPlayers;
        }

        return NoPlayers;
    }

    IReadOnlyList<long> TryRecoverActiveByPtr(
        string ptr, string? matchKey, string? occurrenceId, string? eventTime,
        string? killerPtr, string targetSide, long? activeMatchMs, List<string> leveledSpecimenIds)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT instance_id, player_id, match_key FROM rpg_unique_actors
                WHERE phase = $phase AND last_ptr = $ptr;
                """;
            cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.ActiveBound);
            cmd.Parameters.AddWithValue("$ptr", ptr);
            var rows = new List<(string Id, long PlayerId, string? MatchKey)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
            // The dead ptr identifies the terminal specimen, not its killer. A kill award is only
            // eligible when the capture supplies a causal killerPtr that resolves to an opposing,
            // still-bound specimen in this same match.
            if (!string.IsNullOrWhiteSpace(killerPtr) && !string.IsNullOrWhiteSpace(matchKey))
            {
                using var killer = db.CreateCommand();
                killer.Transaction = tx;
                killer.CommandText = """
                    SELECT instance_id, player_id, side, match_key FROM rpg_unique_actors
                    WHERE phase = $phase AND last_ptr = $ptr AND match_key = $mk;
                    """;
                killer.Parameters.AddWithValue("$phase", UniqueActorPhases.ActiveBound);
                killer.Parameters.AddWithValue("$ptr", killerPtr!);
                killer.Parameters.AddWithValue("$mk", matchKey!);
                var killers = new List<(string Id, long PlayerId, string Side)>();
                using (var kr = killer.ExecuteReader())
                {
                    while (kr.Read())
                    {
                        var side = kr.GetString(2);
                        if (!string.Equals(side, targetSide, StringComparison.Ordinal))
                            killers.Add((kr.GetString(0), kr.GetInt64(1), side));
                    }
                }
                foreach (var (id, _, _) in killers)
                    if (AwardUniqueLawnKillUnlocked(db, id, matchKey, occurrenceId, tx))
                        leveledSpecimenIds.Add(id);
            }
            foreach (var (id, _, actorMatchKey) in rows)
            {
                if (AwardUniqueLawnDurationUnlocked(db, id, actorMatchKey ?? matchKey, activeMatchMs, tx))
                    leveledSpecimenIds.Add(id);
                // Task 4C.1 lawn-death producer (explicit lawn→Retire trigger, not the
                // injury-tiers build): a bound specimen named by plant.die/zombie.die died for
                // real — Retire in THIS transaction via the same unlocked seam
                // TryRetireUniqueActor uses (no parallel retire path, no new signal: the
                // phase transition itself is the corpse-cache trigger), moving rolled gear
                // into the lawn cache. Survivors still walk home through
                // TryRecoverActiveByMatchKey (board.end/match.result) with gear worn.
                // rpg_player_item_assignment is never read here (commander rule untouched).
                var placeRef = actorMatchKey ?? matchKey;
                if (string.IsNullOrWhiteSpace(placeRef)) placeRef = "unplaced:" + id;

                // solid-remediation SR-17, CLOSED 2026-09-17 by owner ruling. ASK the engine which
                // outcome applies; never inline one. What stood here before today was the delve's own
                // `case Retire:` branch copied verbatim, so the lawn settled EVERY death at the
                // harshest outcome -- and the register claimed the lawn had no settlement call site at
                // all. Both were wrong.
                //
                // THE RULING (owner, verbatim): "Lawn permanent Death? Use damage scale and low chance
                // permanent death, that mode is casual, dont kill all player unique demon, so injury
                // also low too." So the tuned `LawnPermadeathLadder` replaces `AlwaysPermadeath`, and
                // `gk-core/data/tuning/lawn-attrition.v2.json` drops to 40permille permadeath at a full bar.
                //
                // damageTakenMilli = 1000 is EXACT here, not a placeholder standing in for the
                // accumulator SR-17 used to be blocked on. A specimen named by plant.die/zombie.die
                // lost its whole bar by definition, so a lawn DEATH always evaluates at the full-bar
                // end of the curve. The accumulator is only needed for the INJURY half -- a survivor's
                // damage taken across a run -- which is still unbuilt and is not what the ruling asked
                // for.
                //
                // The roll is DERIVED, never drawn from ambient randomness: `ObserveUniqueActorEvents`
                // is re-ingestable, and a fresh draw would let a replay of the same die event flip a
                // specimen between permanently dead and alive. Seeding off (instanceId, matchKey,
                // occurrenceId) makes the outcome a pure function of the event, so re-ingest is
                // idempotent -- the same property `AwardUniqueLawnKillUnlocked`'s receipt gives the XP
                // award beside it.
                var lawnLadder = new FusionRpg.Core.Battle.Attrition.LawnPermadeathLadder(
                    FusionRpg.Core.Battle.Attrition.LawnAttritionTuningHub.Tuning,
                    damageTakenMilli: 1000,
                    rolledMilli: FusionRpg.Core.Battle.SeededRng
                        .DeriveStream(0UL, $"lawn-permadeath:{id}:{matchKey}:{occurrenceId}")
                        .NextPerMille());

                var settlement = FusionRpg.Core.Battle.Attrition.MemberSettlementRules.Decide(
                    downedOnce: true,
                    ladder: lawnLadder,
                    // Unreachable on the lawn: a non-permanent lawn death recovers straight to Roster
                    // (below) rather than spending delves in Recovering, which is a delve concept.
                    // Kept positive because Decide validates it on the Recover branch.
                    downedRecoveryDelves: 1,
                    afflicted: false,
                    extracted: false,
                    bossKilled: false,
                    routeAtLeastHalfCleared: false);

                if (settlement.Outcome == FusionRpg.Core.Battle.Attrition.SettlementOutcome.Retire)
                {
                    // The rare branch now: permanent death, gear to the lawn cache.
                    RetireUniqueActorUnlocked(db, tx, id);
                    MoveAssignedGearToCorpseCacheUnlocked(db, tx, id, "lawn", placeRef, "death");
                }
                else
                {
                    // The common branch, and what "casual" means: the specimen died on the board but
                    // is not lost. It walks home through the SAME recover path a board.end survivor
                    // uses, keeping everything worn -- so no corpse cache row is created and no gear
                    // moves. `MoveAssignedGearToCorpseCacheUnlocked` is deliberately NOT called here:
                    // a specimen that comes back keeps its loadout.
                    RecoverToRosterUnlocked(db, id, tx);
                }
                // Preserve RecoverToRosterUnlocked's unconditional lawn-session cleanup:
                // the duration award above only deletes on its success path.
                using (var clear = db.CreateCommand())
                {
                    clear.Transaction = tx;
                    clear.CommandText = "DELETE FROM rpg_unique_lawn_sessions WHERE instance_id=$id;";
                    clear.Parameters.AddWithValue("$id", id);
                    clear.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return rows.Select(x => x.PlayerId).Distinct().ToList();
        }
    }

    /// <summary>Applies the dedicated specimen lawn-kill factor once per lifecycle occurrence. The
    /// receipt is keyed by specimen, match, occurrence, and reason so retries and pointer reuse are
    /// harmless. Missing occurrence identity is refused rather than falling back to a pointer.
    /// <b>Returns</b> whether this call itself leveled the specimen (EP1.15, cache trigger T3) — the
    /// caller broadcasts <c>AptitudesUpdated(unique, instanceId)</c> only then, since a level-up is
    /// exactly the case where nothing about the allocation changed yet the resolved default did.</summary>
    bool AwardUniqueLawnKillUnlocked(SqliteConnection db, string instanceId, string? matchKey, string? occurrenceId,
        SqliteTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(matchKey) || string.IsNullOrWhiteSpace(occurrenceId)) return false;
        long delta;
        try { delta = RpgXpAwards.SpecimenLawnKill; }
        catch (InvalidOperationException) { return false; }
        if (delta <= 0) return false;

        var now = ServerClock.UtcNowDateTime.ToString("o");
        using var receipt = db.CreateCommand();
        receipt.Transaction = tx;
        receipt.CommandText = """
            INSERT OR IGNORE INTO rpg_unique_lawn_xp_receipts
              (instance_id, match_key, occurrence_id, reason, xp, created_utc)
            VALUES($id, $mk, $occ, $reason, $xp, $now);
            """;
        receipt.Parameters.AddWithValue("$id", instanceId);
        receipt.Parameters.AddWithValue("$mk", matchKey!);
        receipt.Parameters.AddWithValue("$occ", occurrenceId!);
        receipt.Parameters.AddWithValue("$reason", RpgXpReasons.SpecimenLawnKill);
        receipt.Parameters.AddWithValue("$xp", delta);
        receipt.Parameters.AddWithValue("$now", now);
        if (receipt.ExecuteNonQuery() <= 0)
        {
            using var check = db.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT xp FROM rpg_unique_lawn_xp_receipts WHERE instance_id=$id AND match_key=$mk AND occurrence_id=$occ AND reason=$reason;";
            check.Parameters.AddWithValue("$id", instanceId);
            check.Parameters.AddWithValue("$mk", matchKey!);
            check.Parameters.AddWithValue("$occ", occurrenceId!);
            check.Parameters.AddWithValue("$reason", RpgXpReasons.SpecimenLawnKill);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) != delta)
                throw new InvalidOperationException("unique lawn XP receipt collision");
            return false;
        }

        var (ok, _, actor, levelsGained) = AwardUniqueActorXpUnlocked(db, instanceId, delta, tx);
        if (ok && actor is not null && levelsGained > 0)
            TryRollActionUnlocks(db, instanceId, actor.TypeId, levelsGained, tx);
        return ok && levelsGained > 0;
    }

    /// <summary><b>Returns</b> whether this call itself leveled the specimen (EP1.15, cache trigger
    /// T3) — same contract as <see cref="AwardUniqueLawnKillUnlocked"/>.</summary>
    bool AwardUniqueLawnDurationUnlocked(SqliteConnection db, string instanceId, string? matchKey, long? activeMatchMs,
        SqliteTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(matchKey)) return false;
        long intervalMs;
        long intervalXp;
        try
        {
            intervalMs = RpgXpAwards.SpecimenBoundIntervalMs;
            intervalXp = RpgXpAwards.SpecimenBoundIntervalXp;
        }
        catch (InvalidOperationException) { return false; }
        if (intervalMs <= 0 || intervalXp <= 0) return false;

        using var read = db.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT bound_active_ms FROM rpg_unique_lawn_sessions WHERE instance_id=$id AND match_key=$mk;";
        read.Parameters.AddWithValue("$id", instanceId);
        read.Parameters.AddWithValue("$mk", matchKey!);
        if (read.ExecuteScalar() is not { } boundValue || boundValue is DBNull || activeMatchMs is not { } endedMs)
            return false;
        var startedMs = Convert.ToInt64(boundValue);
        if (endedMs < startedMs) return false;
        var elapsedMs = endedMs - startedMs;
        if (elapsedMs <= 0) return false;
        long delta;
        checked { delta = (elapsedMs / intervalMs) * intervalXp; }

        var now = ServerClock.UtcNowDateTime.ToString("o");
        using var receipt = db.CreateCommand();
        receipt.Transaction = tx;
        receipt.CommandText = """
            INSERT OR IGNORE INTO rpg_unique_lawn_xp_receipts
              (instance_id, match_key, occurrence_id, reason, xp, created_utc)
            VALUES($id, $mk, 'duration', $reason, $xp, $now);
            """;
        receipt.Parameters.AddWithValue("$id", instanceId);
        receipt.Parameters.AddWithValue("$mk", matchKey!);
        receipt.Parameters.AddWithValue("$reason", RpgXpReasons.SpecimenLawnDuration);
        receipt.Parameters.AddWithValue("$xp", delta);
        receipt.Parameters.AddWithValue("$now", now);
        if (receipt.ExecuteNonQuery() <= 0)
        {
            using var check = db.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT xp FROM rpg_unique_lawn_xp_receipts WHERE instance_id=$id AND match_key=$mk AND occurrence_id='duration' AND reason=$reason;";
            check.Parameters.AddWithValue("$id", instanceId);
            check.Parameters.AddWithValue("$mk", matchKey!);
            check.Parameters.AddWithValue("$reason", RpgXpReasons.SpecimenLawnDuration);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) != delta)
                throw new InvalidOperationException("unique lawn XP duration receipt collision");
            return false;
        }

        var (ok, _, actor, levelsGained) = AwardUniqueActorXpUnlocked(db, instanceId, delta, tx);
        if (ok && actor is not null && levelsGained > 0)
            TryRollActionUnlocks(db, instanceId, actor.TypeId, levelsGained, tx);
        using var clear = db.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = "DELETE FROM rpg_unique_lawn_sessions WHERE instance_id=$id;";
        clear.Parameters.AddWithValue("$id", instanceId);
        clear.ExecuteNonQuery();
        return ok && levelsGained > 0;
    }

    IReadOnlyList<long> TryRecoverActiveByMatchKey(string matchKey, string? eventTime, long? activeMatchMs, List<string> leveledSpecimenIds)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT instance_id, player_id FROM rpg_unique_actors
                WHERE phase = $phase AND match_key = $mk;
                """;
            cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.ActiveBound);
            cmd.Parameters.AddWithValue("$mk", matchKey);
            var rows = new List<(string Id, long PlayerId)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetInt64(1)));
            }
            foreach (var (id, _) in rows)
            {
                if (AwardUniqueLawnDurationUnlocked(db, id, matchKey, activeMatchMs, tx))
                    leveledSpecimenIds.Add(id);
                RecoverToRosterUnlocked(db, id, tx);
            }
            tx.Commit();
            return rows.Select(x => x.PlayerId).Distinct().ToList();
        }
    }

    void RecoverToRosterUnlocked(SqliteConnection db, string instanceId, SqliteTransaction? tx = null)
    {
        // ActiveBound → Recovering → Roster in one write (persist_ok immediate for W4).
        var now = ServerClock.UtcNowDateTime.ToString("o");
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE rpg_unique_actors SET
              phase = $phase,
              match_key = NULL,
              last_ptr = NULL,
              deploy_correlation_id = NULL,
              revision = revision + 2,
              updated_utc = $now
            WHERE instance_id = $id AND phase = $active;
            """;
        cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.Parameters.AddWithValue("$active", UniqueActorPhases.ActiveBound);
        cmd.ExecuteNonQuery();
        using var session = db.CreateCommand();
        session.Transaction = tx;
        session.CommandText = "DELETE FROM rpg_unique_lawn_sessions WHERE instance_id=$id;";
        session.Parameters.AddWithValue("$id", instanceId);
        session.ExecuteNonQuery();
    }

    void ClearBindToRosterUnlocked(SqliteConnection db, string instanceId)
    {
        var now = ServerClock.UtcNowDateTime.ToString("o");
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_unique_actors SET
              phase = $phase,
              match_key = NULL,
              last_ptr = NULL,
              deploy_correlation_id = NULL,
              revision = revision + 1,
              updated_utc = $now
            WHERE instance_id = $id;
            """;
        cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.ExecuteNonQuery();
    }

    UniqueActorDto? FindUniqueByCorrelationUnlocked(SqliteConnection db, string corr)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT instance_id, player_id, side, type_id, phase, level, xp,
                   match_key, last_ptr, deploy_correlation_id, revision, created_utc, updated_utc,
                   empire_id
            FROM rpg_unique_actors WHERE deploy_correlation_id = $corr LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$corr", corr);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapUniqueActor(r) : null;
    }

    UniqueActorDto? ReadUniqueActorUnlocked(SqliteConnection db, string instanceId, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT instance_id, player_id, side, type_id, phase, level, xp,
                   match_key, last_ptr, deploy_correlation_id, revision, created_utc, updated_utc,
                   empire_id
            FROM rpg_unique_actors WHERE instance_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", instanceId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapUniqueActor(r) : null;
    }

    static UniqueActorDto MapUniqueActor(SqliteDataReader r)
    {
        // save-identity SE4.31: every SELECT feeding this mapper appends the owner column `empire_id`
        // LAST, so the ordinals below stay the contract they were. It is read by NAME for the same
        // reason: a SELECT that forgets the column throws here (GetOrdinal) rather than quietly
        // reporting a specimen with no owner.
        var empire = r.GetOrdinal("empire_id");
        return new UniqueActorDto
        {
            InstanceId = r.GetString(0),
            PlayerId = r.GetInt64(1),
            Side = r.GetString(2),
            TypeId = r.GetInt32(3),
            Phase = r.GetString(4),
            Level = r.GetInt64(5),
            Xp = ReadXp(r, 6),
            MatchKey = r.IsDBNull(7) ? null : r.GetString(7),
            LastPtr = r.IsDBNull(8) ? null : r.GetString(8),
            DeployCorrelationId = r.IsDBNull(9) ? null : r.GetString(9),
            Revision = r.GetInt64(10),
            CreatedAt = r.GetString(11),
            UpdatedAt = r.GetString(12),
            EmpireId = r.IsDBNull(empire) ? null : r.GetString(empire)
        };
    }

    static string NormalizeUniqueSide(string? side)
    {
        var s = (side ?? "plant").Trim().ToLowerInvariant();
        return s is "plant" or "zombie" ? s : "plant";
    }

    static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string PayloadToJson(object? payload)
    {
        if (payload is null) return "{}";
        if (payload is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "{}";
            try
            {
                using var _ = JsonDocument.Parse(s);
                return s;
            }
            catch
            {
                return JsonSerializer.Serialize(new { value = s });
            }
        }
        if (payload is JsonElement el)
            return el.GetRawText();
        try { return JsonSerializer.Serialize(payload, Json); }
        catch { return "{}"; }
    }

    /// <summary>Deploying rows older than <paramref name="timeout"/> → Roster (W5-D).</summary>
    public IReadOnlyList<(string InstanceId, string? CorrelationId)> FailExpiredUniqueDeploys(
        TimeSpan timeout, DateTimeOffset? utcNow = null)
    {
        if (timeout <= TimeSpan.Zero) return Array.Empty<(string, string?)>();
        var now = utcNow ?? ServerClock.UtcNow;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var expired = new List<(string Id, string? Corr)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT instance_id, deploy_correlation_id, updated_utc FROM rpg_unique_actors
                    WHERE phase = $phase;
                    """;
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Deploying);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    var corr = r.IsDBNull(1) ? null : r.GetString(1);
                    var updated = r.GetString(2);
                    if (!DateTimeOffset.TryParse(updated, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                        continue;
                    if (now - ts >= timeout)
                        expired.Add((id, corr));
                }
            }

            foreach (var (id, _) in expired)
                ClearBindToRosterUnlocked(db, id);
            return expired;
        }
    }

    /// <summary>
    /// ActiveBound with missing/stale match_key (no open run) → Roster (W5-E).
    /// Observe lag OK; no Hot coupling. Call on boot / Init.
    /// </summary>
    public int SweepStaleActiveBoundUniqueActors()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return SweepStaleActiveBoundUnlocked(db);
        }
    }

    int SweepStaleActiveBoundUnlocked(SqliteConnection db)
    {
        var stale = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT u.instance_id, u.match_key
                FROM rpg_unique_actors u
                WHERE u.phase = $phase;
                """;
            cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.ActiveBound);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var mk = r.IsDBNull(1) ? null : r.GetString(1);
                if (string.IsNullOrWhiteSpace(mk) || !HasOpenRunForMatchKeyUnlocked(db, mk!))
                    stale.Add(id);
            }
        }

        foreach (var id in stale)
            RecoverToRosterUnlocked(db, id);
        return stale.Count;
    }

    static bool HasOpenRunForMatchKeyUnlocked(SqliteConnection db, string matchKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM runs
            WHERE match_key = $mk AND (ended_utc IS NULL OR ended_utc = '')
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$mk", matchKey);
        return cmd.ExecuteScalar() is not null and not DBNull;
    }

    public bool HasAnyActiveBoundUniqueActors()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return HasAnyActiveBoundUnlocked(db);
        }
    }

    static bool HasAnyActiveBoundUnlocked(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM rpg_unique_actors WHERE phase = $phase LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.ActiveBound);
        return cmd.ExecuteScalar() is not null and not DBNull;
    }

    public string GetUniqueStatModsJson(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return "{}";
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return GetUniqueStatModsJsonUnlocked(db, instanceId.Trim());
        }
    }

    public UniqueEquipmentListDto? GetUniqueEquipment(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var id = instanceId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var actor = ReadUniqueActorUnlocked(db, id);
            if (actor is null) return null;
            return new UniqueEquipmentListDto
            {
                InstanceId = id,
                Phase = actor.Phase,
                Items = ListUniqueEquipmentUnlocked(db, id),
                ModsJson = GetUniqueStatModsJsonUnlocked(db, id)
            };
        }
    }

    public void UpsertUniqueStatModsJson(string instanceId, string modsJson)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("instanceId");
        var json = string.IsNullOrWhiteSpace(modsJson) ? "{}" : modsJson.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            UpsertUniqueStatModsJsonUnlocked(db, instanceId.Trim(), json);
        }
    }

    static void UpsertUniqueStatModsJsonUnlocked(SqliteConnection db, string instanceId, string json)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_unique_stat_mods(instance_id, mods_json) VALUES($id, $j)
            ON CONFLICT(instance_id) DO UPDATE SET mods_json = excluded.mods_json;
            """;
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    public List<UniqueEquipmentSlotDto> ListUniqueEquipment(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return new();
        var id = instanceId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ListUniqueEquipmentUnlocked(db, id);
        }
    }

    /// <summary>
    /// ⭐ <b>The SSOT is <c>rpg_item_assignment</c>, not <c>rpg_unique_equipment</c></b> —
    /// `decision-d1-durable-ownership.md` §10 <b>M2</b>, landed 2026-09-06 alongside
    /// <see cref="MigrateUniqueEquipmentToAssignments"/> (M1). D1's own wording for this step:
    /// *"Repoint … to read assignments instead of `rpg_unique_equipment`. **Output shape unchanged**
    /// — same `mods_json`, same `instance:pending`, same binder, same FE."*
    ///
    /// <para><b>Legacy labels on the wire, canonical roles in the table.</b> The row carries an
    /// <see cref="FusionRpg.Core.Items.ItemRole"/> id; this projects it back through
    /// <see cref="FusionRpg.Core.Items.LegacyEquipSlots"/> so
    /// <c>GET /api/unique/actors/{id}/equipment</c> still answers <c>weapon|armor|trinket</c> and
    /// <c>RelicsLayer.tsx</c> needs no change. Widening the wire to all fifteen roles is D1's
    /// <b>M3</b>, a separate step that moves the REST payload and the FE literal together.</para>
    ///
    /// <para><b>The sort is preserved deliberately, not incidentally.</b> The old query ended
    /// <c>ORDER BY slot ASC</c> over the legacy labels (armor, trinket, weapon); ordering by
    /// <c>role</c> instead would answer armament-primary, core-guard, jewel-minor-a — i.e. weapon,
    /// armor, trinket — a different array on the same data. Sorted here by the projected legacy
    /// label with <see cref="string.CompareOrdinal(string?,string?)"/>, which is what SQLite's
    /// default BINARY collation on a TEXT column already did.</para>
    ///
    /// <para><b>Every <c>ref_kind</c> is surfaced, not just <c>stock</c>.</b> An assignment is what
    /// the specimen wears; filtering the view to the kind this wire happens to write would make the
    /// endpoint lie the day a rolled item lands in one of the three roles. No rolled assignment can
    /// exist today (no concrete item container is minted yet — module 17's own seed→concrete
    /// deferral), so this is a correctness property rather than a behaviour change.</para>
    /// </summary>
    static List<UniqueEquipmentSlotDto> ListUniqueEquipmentUnlocked(SqliteConnection db, string instanceId)
    {
        var items = new List<UniqueEquipmentSlotDto>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT role, ref_id FROM rpg_item_assignment WHERE specimen_id = $id;";
            cmd.Parameters.AddWithValue("$id", instanceId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!FusionRpg.Core.Items.ItemRoles.TryParse(r.GetString(0), out var role)) continue;
                if (!FusionRpg.Core.Items.LegacyEquipSlots.TryToLegacy(role, out var slot)) continue;
                items.Add(new UniqueEquipmentSlotDto
                {
                    Slot = slot,
                    ItemId = r.IsDBNull(1) ? "" : r.GetString(1)
                });
            }
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Slot, b.Slot));
        return items;
    }

    /// <summary>Source marker on every assignment this legacy wire writes. <c>stock</c> is I13
    /// §4.4's kind for "identified by a catalog id, not by a rolled instance" — exactly what a
    /// relic or stub item is, and exactly what D1 §10 M1 specifies for the migrated rows.</summary>
    const string LegacyEquipRefKind = FusionRpg.Core.Items.EquipRefKinds.Stock;

    /// <summary>
    /// ⛔ <b>The symmetric half of <c>equip.role-held-by-relic</c>, added 2026-09-06 (defect R1).</b>
    ///
    /// <para>Since D1 §10 M1/M2 two flows write <c>rpg_item_assignment</c>: this legacy relic wire
    /// (<c>stock</c>) and <c>POST /api/items/equip</c> (<c>rolled</c>). The item route already refused
    /// a role a relic holds by name — <b>and this one refused nothing</b>. Measured, not assumed: with
    /// a real blade in <c>armament-primary</c>, <c>PUT .../equipment/weapon</c> answered <b>200</b> and
    /// the upsert replaced the row, so the player's item came off with no refusal and no notice.</para>
    ///
    /// <para>Enforced <b>here</b>, at the single write point, rather than in
    /// <c>UniqueActorService</c>: it is inside the same <c>_gate</c> the write takes, so there is no
    /// read-then-write window, and it covers <see cref="ClearUniqueEquipmentSlot"/> for free — which
    /// matters, because the clear path <c>DELETE</c>s the very same row and would otherwise unequip an
    /// item outright.</para>
    ///
    /// <para>Only <c>rolled</c> is refused. A <c>stock</c> occupant is this wire's own row and
    /// replacing it is the wire's normal job (swapping one relic for another).</para>
    /// </summary>
    static void RefuseIfRoleHeldByAnItem(
        IReadOnlyList<FusionRpg.Core.Items.EquipAssignment> standing,
        string instanceId, FusionRpg.Core.Items.ItemRole role, string slot)
    {
        foreach (var a in standing)
        {
            if (a.Role != role) continue;
            if (!string.Equals(a.RefKind, FusionRpg.Core.Items.EquipRefKinds.Rolled, StringComparison.Ordinal))
                continue;
            throw new UniqueEquipmentSlotClaimed(
                $"slot.claimed_by_item: '{slot}' ({FusionRpg.Core.Items.ItemRoles.Id(role)}) on specimen " +
                $"'{instanceId}' holds item '{a.RefId}', which was equipped through the item flow — " +
                "take it off with POST /api/items/unequip first");
        }
    }

    static void WriteLegacyEquipAssignmentUnlocked(
        SqliteConnection db, string instanceId, FusionRpg.Core.Items.ItemRole role, string itemId) =>
        ExecParams(db, """
            INSERT INTO rpg_item_assignment (specimen_id, role, ref_kind, ref_id, assigned_utc)
            VALUES ($sid, $role, $rk, $rid, $utc)
            ON CONFLICT(specimen_id, role) DO UPDATE SET
              ref_kind = excluded.ref_kind, ref_id = excluded.ref_id, assigned_utc = excluded.assigned_utc;
            """,
            ("$sid", instanceId), ("$role", FusionRpg.Core.Items.ItemRoles.Id(role)),
            ("$rk", LegacyEquipRefKind), ("$rid", itemId),
            ("$utc", ServerClock.UtcNowDateTime.ToString("O")));

    static void ClearLegacyEquipAssignmentUnlocked(
        SqliteConnection db, string instanceId, FusionRpg.Core.Items.ItemRole role) =>
        ExecParams(db, "DELETE FROM rpg_item_assignment WHERE specimen_id = $sid AND role = $role;",
            ("$sid", instanceId), ("$role", FusionRpg.Core.Items.ItemRoles.Id(role)));

    /// <summary>
    /// ⭐ <b>D1 §10 <c>M1</c> — the relic row migration, closed 2026-09-06.</b> Copies every
    /// <c>rpg_unique_equipment(instance_id, slot, item_id)</c> row into
    /// <c>rpg_item_assignment(specimen_id, role, ref_kind='stock', ref_id=item_id)</c>, mapping the
    /// slot through I2's alias map (<see cref="FusionRpg.Core.Items.LegacyEquipSlots"/>).
    ///
    /// <para><b>Why this was stuck, and why it was never actually blocked.</b> `item-todo.md`
    /// module 4 (P1.4) deferred this to module 17 (`uniques`) on the grounds that relics had
    /// "no home to migrate into"; module 17 (P5.1) then deferred it back, correctly, as module 4's.
    /// Both notes were self-consistent and the item sat unbuilt. The premise of the first was
    /// wrong: module 17's <c>item_unique</c> is a nine-column <b>classification flag</b> keyed 1:1
    /// on an <c>effect_container</c> — no name, rarity, slot, description or effect column — so it
    /// never could have received an equipped-slot row. The home these rows needed is
    /// <c>rpg_item_assignment</c>, which is module 4's own table and shipped 2026-09-04.</para>
    ///
    /// <para><b>One-way and idempotent</b>, exactly as D1 words it. A specimen+role that already
    /// carries an assignment is left alone — never overwritten — so a second call writes nothing,
    /// and a row equipped through the post-M2 path can never be clobbered by a stale legacy row.
    /// The old table keeps its rows and stops being written; dropping it is D1's <b>M4</b>, gated on
    /// a <c>ref_kind='rolled'</c> grant path that does not exist yet, and D1 calls that drop
    /// *"the only irreversible act; do it last."*</para>
    ///
    /// <para>Returns the number of rows actually copied.</para>
    /// </summary>
    public int MigrateUniqueEquipmentToAssignments()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return MigrateUniqueEquipmentToAssignmentsUnlocked(db);
        }
    }

    internal static int MigrateUniqueEquipmentToAssignmentsUnlocked(SqliteConnection db)
    {
        var legacy = new List<(string InstanceId, string Slot, string ItemId)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT instance_id, slot, item_id FROM rpg_unique_equipment;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                legacy.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        if (legacy.Count == 0) return 0;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT specimen_id, role FROM rpg_item_assignment;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) taken.Add($"{r.GetString(0)}\0{r.GetString(1)}");
        }

        var now = ServerClock.UtcNowDateTime.ToString("O");
        var copied = 0;
        foreach (var (instanceId, slot, itemId) in legacy)
        {
            if (string.IsNullOrWhiteSpace(itemId)) continue;
            if (!FusionRpg.Core.Items.LegacyEquipSlots.TryFromLegacy(slot, out var role)) continue;
            var roleId = FusionRpg.Core.Items.ItemRoles.Id(role);
            if (!taken.Add($"{instanceId}\0{roleId}")) continue; // already assigned — never overwrite
            ExecParams(db, """
                INSERT INTO rpg_item_assignment (specimen_id, role, ref_kind, ref_id, assigned_utc)
                VALUES ($sid, $role, $rk, $rid, $utc);
                """,
                ("$sid", instanceId), ("$role", roleId),
                ("$rk", LegacyEquipRefKind), ("$rid", itemId), ("$utc", now));
            copied++;
        }
        return copied;
    }

    /// <summary>Upsert slot. Empty itemId deletes the slot row. Non-empty must be a known stub.
    ///
    /// <para><b>Writes <c>rpg_item_assignment</c></b> (D1 §10 M2) — the legacy slot label is validated
    /// exactly as before, then mapped to its canonical role. The request, the response and every
    /// downstream effect (<c>mods_json</c>, the <c>unique-equip</c> atom bindings) are unchanged;
    /// only the table underneath moved. See <see cref="ListUniqueEquipmentUnlocked"/>.</para></summary>
    public UniqueEquipmentListDto UpsertUniqueEquipment(string instanceId, string slot, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("instanceId");
        var id = instanceId.Trim();
        var s = FusionRpg.Core.Match.UniqueEquipmentCatalog.NormalizeSlot(slot);
        if (!FusionRpg.Core.Items.LegacyEquipSlots.TryFromLegacy(s, out var role))
            throw new ArgumentException("slot required", nameof(slot));
        var item = (itemId ?? "").Trim();
        if (!string.IsNullOrEmpty(item))
        {
            if (!FusionRpg.Core.Match.UniqueEquipmentCatalog.IsKnownItem(item))
                throw new ArgumentException("unknown_item", nameof(itemId));
            if (!FusionRpg.Core.Match.UniqueEquipmentCatalog.SlotMatchesItem(s, item))
                throw new ArgumentException("slot_mismatch", nameof(itemId));
        }
        lock (_gate)
        {
            // Module 4's own read, reused rather than re-derived — and taken before the write
            // connection opens so the refusal cannot half-apply. `_gate` is reentrant, and an unknown
            // specimen simply has no assignments, so `not_found` below still answers first for one.
            RefuseIfRoleHeldByAnItem(ListAssignments(id), id, role, s);

            using var db = OpenUnlocked();
            if (ReadUniqueActorUnlocked(db, id) is null)
                throw new InvalidOperationException("not_found");

            if (string.IsNullOrEmpty(item))
                ClearLegacyEquipAssignmentUnlocked(db, id, role);
            else
                WriteLegacyEquipAssignmentUnlocked(db, id, role, item);

            RebuildUniqueModsFromEquipmentUnlocked(db, id);
            ReconcileUniqueEquipmentAtomBindingsUnlocked(db, id);
            var actor = ReadUniqueActorUnlocked(db, id)!;
            return new UniqueEquipmentListDto
            {
                InstanceId = id,
                Phase = actor.Phase,
                Items = ListUniqueEquipmentUnlocked(db, id),
                ModsJson = GetUniqueStatModsJsonUnlocked(db, id)
            };
        }
    }

    public UniqueEquipmentListDto ClearUniqueEquipmentSlot(string instanceId, string slot) =>
        UpsertUniqueEquipment(instanceId, slot, "");

    /// <summary>Source tag on every binding this reconciliation owns — lets it find (and never touch)
    /// bindings any other system placed on the same <see cref="OwnerKind.UniqueActor"/> owner.</summary>
    const string UniqueEquipAtomSource = "unique-equip";

    /// <summary>creature-lawn-deploy T1.2's own source tag — a distinct name from
    /// <see cref="UniqueEquipAtomSource"/> even though both bind through the same
    /// <see cref="OwnerKind.UniqueActor"/> owner, so the two reconcilers can never withdraw or
    /// mis-match each other's bindings on the same specimen.</summary>
    const string CreatureTraitAtomSource = "creature-trait";

    /// <summary>Same flat, unscaled pin <see cref="UniqueEquipAtomPinTheta"/> uses, named separately
    /// on purpose: a trait's own gameplay effect (e.g. `trait.critical-hunter`'s crit-chance bonus) is
    /// pre-authored content, not a magnitude meant to scale with the specimen's own level/rarity within
    /// THIS binding — mirrors the equipment precedent's own reasoning (AGENTS.md "one power ladder": no
    /// private per-binding curve). If species-magnitude delivery (T1.5) later needs its own pin, it
    /// gets to pick independently — this constant does not assume they must match.</summary>
    const int CreatureTraitAtomPinTheta = UniqueEquipAtomPinTheta;

    /// <summary>
    /// creature-lawn-deploy T1.2: reconcile a creature specimen's own rolled `TraitIds` against
    /// `effect_binding`, through the SAME <see cref="OwnerKind.UniqueActor"/> owner equipment already
    /// uses (a creature specimen is a `rpg_unique_actors` row like any other). Run on EVERY deploy, never
    /// once at mint — `RpgStore.Fusion.cs`'s `PromotionUnlocked` can add traits to an EXISTING
    /// `instanceId` after mint (fusion star-merge), and a mint-time-only binding would silently miss
    /// that later trait forever (strengthen-pass Correction 2, spec-lawn-deploy-core.md).
    ///
    /// <para>Idempotent by construction, mirroring
    /// <see cref="ReconcileUniqueEquipmentAtomBindingsUnlocked"/>'s own double-grant invariant: a trait
    /// already correctly bound is left untouched; a bound trait no longer present in `TraitIds` is
    /// withdrawn; a new trait not yet bound is produced and bound. Unlike equipment, there is no
    /// "slot swap" case — one trait id always maps to exactly one container id
    /// (<see cref="CreatureTraitCatalog.GrantTemplateId"/>), so a trait's own binding either exists
    /// correctly or does not exist at all.</para>
    ///
    /// <para>An unknown trait id (should not happen — `TraitIds` is populated from a species'
    /// already-`CreatureSpeciesCatalog.Validate`-checked `TraitPool` at mint/promotion time) or a missing
    /// seeded container fails closed: skipped, nothing granted, never a throw — matching the equipment
    /// reconciler's own "seeded container missing" behavior exactly.</para>
    /// </summary>
    void ReconcileCreatureTraitBindingsUnlocked(SqliteConnection db, string instanceId)
    {
        var actor = ReadUniqueActorUnlocked(db, instanceId);
        if (actor is null) return;
        var player = GetPlayerUnlocked(db, actor.PlayerId);
        if (player is null) return;
        var profile = ReadCreatureProfileUnlocked(db, instanceId);
        if (profile is null) return; // not a creature specimen — nothing for this reconciler to do

        var owner = new OwnerScope(OwnerKind.UniqueActor, instanceId);

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal); // traitId -> containerId
        foreach (var traitId in profile.TraitIds.Distinct(StringComparer.Ordinal))
        {
            if (!CreatureTraitCatalog.IsKnown(traitId)) continue; // fail closed, not a throw
            wanted[traitId] = CreatureTraitCatalog.Get(traitId).GrantTemplateId;
        }

        var existing = ListBindings(owner)
            .Where(b => string.Equals(b.Source, CreatureTraitAtomSource, StringComparison.Ordinal))
            .ToDictionary(b => b.Slot ?? "", StringComparer.Ordinal);

        foreach (var (traitId, binding) in existing)
            if (!wanted.ContainsKey(traitId))
                Withdraw(binding.BindingId);

        var catalogRevision = GetCatalogRevision();
        foreach (var (traitId, containerId) in wanted)
        {
            if (existing.ContainsKey(traitId)) continue; // already bound — double-grant invariant

            var container = GetContainer(containerId);
            if (container is null) continue; // seeded trait container missing; fail closed

            var rollSeed = WorldSeed.DeriveRollSeed(
                player.WorldSeed, CreatureTraitAtomSource, $"{instanceId}:{traitId}");
            ProduceAndBind(container, DomainMembers, rollSeed, CreatureTraitAtomPinTheta,
                FusionRpg.Core.Power.PowerTuningHub.Tuning, owner, traitId, priority: 0,
                source: CreatureTraitAtomSource, out _, out _,
                origin: InstanceOrigin.Grant, catalogRevision: catalogRevision);
        }
    }

    /// <summary>creature-lawn-deploy T1.5's own source tag — distinct from
    /// <see cref="CreatureTraitAtomSource"/>/<see cref="UniqueEquipAtomSource"/> so all three reconcilers
    /// can share the same <see cref="OwnerKind.UniqueActor"/> owner without ever withdrawing or
    /// mis-matching each other's bindings on the same specimen.</summary>
    const string CreatureMagnitudeAtomSource = "creature-magnitude";

    /// <summary>Species-magnitude content is pre-computed (`SpeciesExpander`/`AptitudeReadFunctions`
    /// already folded the specimen's species-level `PTheta` into each channel's own flat value at
    /// generation time — see `ConcreteSpecies.Magnitudes`), so this binding never needs its own
    /// PowerLadder-scaled arithmetic; a flat, unscaled pin picked independently of
    /// <see cref="CreatureTraitAtomPinTheta"/> per that constant's own comment.</summary>
    const int CreatureMagnitudeAtomPinTheta = UniqueEquipAtomPinTheta;

    /// <summary>The one container id convention for a species' whole magnitude package — one container
    /// per species (holding one atom per channel internally), never one container per channel: a
    /// specimen's magnitude set is all-or-nothing (species is fixed for the specimen's whole
    /// lifetime), unlike traits, which add/remove independently across promotions.
    ///
    /// <para>Filed under <see cref="ContainerKind.Trait"/> (id prefix <c>"trait"</c>, enforced by
    /// <c>ContainerRowValidator</c>) deliberately, not <see cref="ContainerKind.SpeciesPassive"/>:
    /// that kind's own <c>"species-passive."</c> prefix already names a DIFFERENT, existing mechanism
    /// (species-progression's layer-1b ledger, `RpgStore.SpeciesMods.cs`'s per-empire ROLLED pick
    /// source, `PickSourceAtoms`/`GetSpecimenLedgerRoll`) — reusing it here would collide on the same speciesId
    /// and conflate two unrelated shapes (rolled vs. this binding's fixed, pre-computed value). `Trait`
    /// is the correct MECHANICAL fit (flat, non-rolled, single-value, `stat.derived`, bound via the same
    /// `OwnerKind.UniqueActor` T1.2 already proved) even though the content isn't a trait; a genuinely
    /// new `ContainerKind` for this is real, separate, cross-cutting surface (`PrefixOf` plus every
    /// exhaustive switch over the enum) intentionally left out of this task's own scope. The id itself
    /// is `trait.species-magnitude-{speciesId}` (a single kebab-case token after the `trait.` prefix —
    /// `ContainerRowValidator` refuses a further embedded dot) so it stays unambiguous from a real
    /// `CreatureTraitCatalog` member like `trait.critical-hunter`.</para>
    /// </summary>
    internal static string SpeciesMagnitudeContainerId(string speciesId) => $"trait.species-magnitude-{speciesId}";

    /// <summary>
    /// creature-lawn-deploy T1.5: reconcile a creature specimen's own SPECIES magnitudes (base stats,
    /// distinct from `TraitIds`) against `effect_binding`, through the SAME
    /// <see cref="OwnerKind.UniqueActor"/> owner T1.2's trait reconciler already uses. Run on every
    /// deploy for the same reason traits do — not because a specimen's own `SpeciesId` can ever change
    /// post-mint (it cannot), but so a species' committed magnitude content can be revised and picked
    /// up on the specimen's NEXT deploy via <see cref="GetCatalogRevision"/>, exactly like every other
    /// atom-backed binding in this file, rather than this one binding alone needing a special
    /// "immutable, bind once" carve-out.
    ///
    /// <para>Unlike the trait reconciler's per-trait wanted set, a specimen's magnitude package is a
    /// SINGLETON: either its species has a magnitude container to bind, or it does not yet (empty
    /// <see cref="CreatureSpeciesDef.Magnitudes"/> — a species with no magnitude data imported, or no
    /// seeded <see cref="SpeciesMagnitudeContainerId"/> content yet, fails closed exactly like an
    /// unknown trait id: no binding, never a throw, matching this module's own "honest incompleteness"
    /// rule for content that has not been generated yet.</para>
    /// </summary>
    void ReconcileCreatureMagnitudeBindingsUnlocked(SqliteConnection db, string instanceId)
    {
        var actor = ReadUniqueActorUnlocked(db, instanceId);
        if (actor is null) return;
        var player = GetPlayerUnlocked(db, actor.PlayerId);
        if (player is null) return;
        var profile = ReadCreatureProfileUnlocked(db, instanceId);
        if (profile is null) return; // not a creature specimen — nothing for this reconciler to do

        var owner = new OwnerScope(OwnerKind.UniqueActor, instanceId);

        string? wantedContainerId = null;
        if (CreatureSpeciesCatalog.IsKnown(profile.SpeciesId))
        {
            var species = CreatureSpeciesCatalog.Get(profile.SpeciesId);
            if (species.Magnitudes.Count > 0)
                wantedContainerId = SpeciesMagnitudeContainerId(profile.SpeciesId);
        }

        var existing = ListBindings(owner)
            .Where(b => string.Equals(b.Source, CreatureMagnitudeAtomSource, StringComparison.Ordinal))
            .ToList();

        foreach (var binding in existing)
            if (!string.Equals(binding.Slot, wantedContainerId, StringComparison.Ordinal))
                Withdraw(binding.BindingId);

        if (wantedContainerId is null) return;
        if (existing.Any(b => string.Equals(b.Slot, wantedContainerId, StringComparison.Ordinal)))
            return; // already bound — double-grant invariant

        var container = GetContainer(wantedContainerId);
        if (container is null) return; // seeded species-magnitude container missing; fail closed

        var rollSeed = WorldSeed.DeriveRollSeed(
            player.WorldSeed, CreatureMagnitudeAtomSource, $"{instanceId}:{profile.SpeciesId}");
        ProduceAndBind(container, DomainMembers, rollSeed, CreatureMagnitudeAtomPinTheta,
            FusionRpg.Core.Power.PowerTuningHub.Tuning, owner, wantedContainerId, priority: 0,
            source: CreatureMagnitudeAtomSource, out _, out _,
            origin: InstanceOrigin.Grant, catalogRevision: GetCatalogRevision());
    }

    /// <summary>
    /// contentScale's own pin (`ContentScale.cs`, `spec-content-scale.md` §2.1): Θc=20 is the anchor
    /// where `contentScale == 1.000` exactly, the same pin `AtomEndToEndTests.cs`'s own production-
    /// shape fixture uses. Stub equipment items are flat, unscaled effects — not loot that should get
    /// a level-derived curve invented for it as a side effect of this owner-scope fix (AGENTS.md "one
    /// power ladder": a private `f(level)` here is exactly the mistake that rule exists to prevent).
    /// </summary>
    const int UniqueEquipAtomPinTheta = 20;

    /// <summary>
    /// `mods-absorption` (T6.1, `tasks/seed-to-concrete-todo.md`, decision 1 —
    /// `tasks/seed-to-concrete-open-decisions.md`): reconcile this actor's atom-backed equipment slots
    /// against `effect_binding`, through <see cref="OwnerKind.UniqueActor"/> (owner-approved
    /// 2026-09-02 specifically because <see cref="OwnerKind.Entity"/> is session-scoped and would
    /// silently drop equipped-item bonuses on the next session boundary).
    ///
    /// <para><b>Idempotent by construction</b> — the "double-grant invariant": a slot whose existing
    /// binding's instance already points at the wanted item's own container is left untouched, never
    /// withdrawn and re-produced, so calling this twice with an unchanged loadout writes nothing new.
    /// A slot that changed item (or was cleared) has its stale binding withdrawn first, then whatever
    /// is now wanted is produced+bound.</para>
    ///
    /// <para>Only items <see cref="FusionRpg.Core.Match.UniqueEquipmentCatalog.TryGetAtomBackedContainerId"/>
    /// maps take this path. As of the 2026-09-06 migration every shipped <c>EffectId</c> maps —
    /// including <c>fx.entity_atk</c>, which binds through the deliberately empty
    /// <c>item.fx-entity-atk</c> container (a preserved no-op, not a magnitude) — so the legacy
    /// <c>mods_json</c> grant path below stays live in code for a future item with no atom yet, but
    /// nothing shipped today reaches it. The two paths never grant the same slot twice because the
    /// catalog map is the single source of which path an item takes.</para>
    /// </summary>
    void ReconcileUniqueEquipmentAtomBindingsUnlocked(SqliteConnection db, string instanceId)
    {
        var actor = ReadUniqueActorUnlocked(db, instanceId);
        if (actor is null) return;
        var player = GetPlayerUnlocked(db, actor.PlayerId);
        if (player is null) return;

        var owner = new OwnerScope(OwnerKind.UniqueActor, instanceId);
        var slots = ListUniqueEquipmentUnlocked(db, instanceId);

        var wanted = new Dictionary<string, (string ContainerId, string ItemId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in slots)
        {
            if (string.IsNullOrWhiteSpace(s.ItemId)) continue;
            if (FusionRpg.Core.Match.UniqueEquipmentCatalog.TryGetAtomBackedContainerId(s.ItemId, out var containerId))
                wanted[s.Slot] = (containerId, s.ItemId);
        }

        var existing = ListBindings(owner)
            .Where(b => string.Equals(b.Source, UniqueEquipAtomSource, StringComparison.Ordinal))
            .ToDictionary(b => b.Slot ?? "", StringComparer.OrdinalIgnoreCase);

        bool MatchesWanted(BindingRow binding, (string ContainerId, string ItemId) want) =>
            GetInstance(binding.InstanceId) is { } inst &&
            string.Equals(inst.ContainerId, want.ContainerId, StringComparison.Ordinal);

        foreach (var (slot, binding) in existing)
        {
            var stillWanted = wanted.TryGetValue(slot, out var want) && MatchesWanted(binding, want);
            if (!stillWanted)
                Withdraw(binding.BindingId);
        }

        var catalogRevision = GetCatalogRevision();
        foreach (var (slot, want) in wanted)
        {
            if (existing.TryGetValue(slot, out var current) && MatchesWanted(current, want))
                continue; // already correctly bound — the double-grant invariant

            var container = GetContainer(want.ContainerId);
            if (container is null) continue; // seeded container missing; fail closed, nothing granted

            var rollSeed = WorldSeed.DeriveRollSeed(
                player.WorldSeed, UniqueEquipAtomSource, $"{instanceId}:{slot}:{want.ItemId}");
            ProduceAndBind(container, DomainMembers, rollSeed, UniqueEquipAtomPinTheta,
                FusionRpg.Core.Power.PowerTuningHub.Tuning, owner, slot, priority: 0,
                source: UniqueEquipAtomSource, out _, out _,
                origin: InstanceOrigin.Grant, catalogRevision: catalogRevision);
        }
    }

    /// <summary>
    /// `mods-absorption` (spec-mods-absorption.md) — the actual per-actor cutover for existing save
    /// data. Re-runs the same pair every equip/unequip already runs
    /// (<see cref="RebuildUniqueModsFromEquipmentUnlocked"/>, then
    /// <see cref="ReconcileUniqueEquipmentAtomBindingsUnlocked"/>) for every unique actor that has
    /// ever equipped something, so a row saved before this module shipped — carrying an atom-backed
    /// item's grant in <c>mods_json</c> from the pre-cutover <c>BuildModsJson</c> — gets rewritten to
    /// the post-cutover shape. Idempotent: an actor whose <c>mods_json</c> is already clean and whose
    /// bindings already match its loadout writes nothing new (the double-grant invariant, same as the
    /// per-equip path).
    ///
    /// <para><b>Per-actor atomic, not one giant transaction</b> (⛔ DECIDED 2026-09-03,
    /// spec-mods-absorption.md §"no read-through window"): each actor's own <c>lock(_gate)</c>
    /// critical section is the atomic unit, matching every other write in this file — the same shape
    /// <see cref="UpsertUniqueEquipment"/> already uses for one actor. A crash mid-sweep leaves
    /// already-cut-over actors cut over and the rest exactly as they were before this call; there is
    /// no window where one actor is half on each path. Returns the count of actors touched.</para>
    /// </summary>
    public int CutoverUniqueEquipmentModsAbsorption()
    {
        List<string> ids;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            // Repointed to the post-M2 SSOT alongside every other reader (D1 §10 M2). The legacy
            // table is migrated into this one at Init, so "every specimen that ever equipped
            // something" is the same set either way -- and after M2 it is the only correct one,
            // because a specimen that equipped for the first time post-cutover has no legacy row.
            cmd.CommandText = "SELECT DISTINCT specimen_id FROM rpg_item_assignment;";
            using var r = cmd.ExecuteReader();
            ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
        }

        var touched = 0;
        foreach (var id in ids)
        {
            lock (_gate)
            {
                using var db = OpenUnlocked();
                if (ReadUniqueActorUnlocked(db, id) is null) continue;
                RebuildUniqueModsFromEquipmentUnlocked(db, id);
                ReconcileUniqueEquipmentAtomBindingsUnlocked(db, id);
            }
            touched++;
        }
        return touched;
    }

    public string RebuildUniqueModsFromEquipment(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("instanceId");
        var id = instanceId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (ReadUniqueActorUnlocked(db, id) is null)
                throw new InvalidOperationException("not_found");
            return RebuildUniqueModsFromEquipmentUnlocked(db, id);
        }
    }

    string RebuildUniqueModsFromEquipmentUnlocked(SqliteConnection db, string instanceId)
    {
        var slots = ListUniqueEquipmentUnlocked(db, instanceId);
        var pairs = slots
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemId))
            .Select(x => (x.Slot, x.ItemId));
        var existing = GetUniqueStatModsJsonUnlocked(db, instanceId);
        var json = FusionRpg.Core.Match.UniqueEquipmentCatalog.BuildModsJson(existing, pairs);
        UpsertUniqueStatModsJsonUnlocked(db, instanceId, json);
        return json;
    }

    static string GetUniqueStatModsJsonUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT mods_json FROM rpg_unique_stat_mods WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return "{}";
        var s = Convert.ToString(v);
        return string.IsNullOrWhiteSpace(s) ? "{}" : s!;
    }

    /// <summary>
    /// W8-B: award specimen XP (not type RpgProgression).
    ///
    /// <para>The level ladder is <see cref="RpgXpCurve"/>'s shared arithmetic cost curve under the
    /// <see cref="RpgActorKinds.Specimen"/> kind, NOT the flat 100-per-level stub this carried until
    /// 2026-09-05. That stub was a private <c>f(level)</c> feeding the same quadratic <c>P(Theta)</c>
    /// every other level feeds, which made specimen power quadratic IN EFFORT where
    /// ssot-power-scale.md §10.5 requires linear — at level 1,000 a specimen level bought the same
    /// power as a player level for a small fraction of the effort. `first` is tuned to 100 so the
    /// cost at level 1 is byte-identical to the old stub; only the late-game divergence changes.</para>
    /// </summary>
    public (bool Ok, string Reason, UniqueActorDto? Actor, int LevelsGained) AwardUniqueActorXp(
        string instanceId, long delta, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return (false, "bad_args", null, 0);
        if (delta <= 0) return (false, "bad_delta", null, 0);
        var id = instanceId.Trim();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var (ok, why, actor, levelsGained) = AwardUniqueActorXpUnlocked(db, id, delta, tx);
            _ = reason; // audit reason reserved; no type progression write
            // A21 (spec-action-instance-and-grant.md §4): the XP write and unlock roll share this
            // transaction, never a post-commit hook.
            if (ok && levelsGained > 0 && actor is not null)
                TryRollActionUnlocks(db, id, actor.TypeId, levelsGained, tx);
            tx.Commit();
            // EP1.15 (spec-default-build.md, cache trigger T3): surfaced so the debug award route
            // (the RPG Server Debug fixture that calls this) can broadcast AptitudesUpdated(unique,
            // instanceId) when this call itself leveled the specimen.
            return (ok, why, actor, levelsGained);
        }
    }

    /// <summary>XP award inside an open transaction — used by the expedition reward apply.</summary>
    internal (bool Ok, string Reason, UniqueActorDto? Actor, int LevelsGained) AwardUniqueActorXpUnlocked(
        SqliteConnection db, string instanceId, long delta, SqliteTransaction? tx = null)
    {
        var row = ReadUniqueActorUnlocked(db, instanceId, tx);
        if (row is null) return (false, "not_found", null, 0);
        if (string.Equals(row.Phase, UniqueActorPhases.Retired, StringComparison.Ordinal))
            return (false, "phase.retired", row, 0);

        long xp;
        checked { xp = row.Xp + delta; }
        var level = row.Level < 1 ? 1 : row.Level;
        // XpToNext is >= 1 by construction (RpgXpCurve clamps), so this always terminates.
        for (var need = RpgXpCurve.XpToNext(RpgActorKinds.Specimen, level); xp >= need;
             need = RpgXpCurve.XpToNext(RpgActorKinds.Specimen, level))
        {
            xp -= need;
            level++;
        }

        var now = ServerClock.UtcNowDateTime.ToString("o");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE rpg_unique_actors SET
              xp = $xp,
              level = $lvl,
              revision = revision + 1,
              updated_utc = $now
            WHERE instance_id = $id;
            """;
        cmd.Parameters.AddWithValue("$xp", xp);
        cmd.Parameters.AddWithValue("$lvl", level);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.ExecuteNonQuery();
        // A21 (spec-action-instance-and-grant.md §4): trailing, additive field -- the same
        // "zero call-site rewrite needed" shape CompiledAction.Category/ActionCostRow.AllowLethal
        // already established. row.Level is the BEFORE value (read above, before this UPDATE).
        return (true, "", ReadUniqueActorUnlocked(db, instanceId, tx), (int)(level - row.Level));
    }

    /// <summary>Standard FNV-1a 64-bit (public-domain hash, not app logic) -- mirrors
    /// `ActionCorpusComposer`'s own private copy: a specimen has no separate "world seed" field, and
    /// its own stable `instance_id` is exactly the right per-specimen discriminator for a
    /// content-seeded, never-per-player-in-the-loot-sense roll (a specimen's OWN unlock rolls simply
    /// must be deterministic and never collide with another specimen's).</summary>
    static ulong Fnv1a64(string text)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in text)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>
    /// A21 (spec-action-instance-and-grant.md §4): one roll attempt for one level gained, wired with
    /// real, `RpgStore`-backed delegates -- `ActionUnlockGrantService` itself stays pure/DB-free.
    /// Called once per `LevelsGained` by both production callers of `AwardUniqueActorXpUnlocked`
    /// (`AwardUniqueActorXp` below, the expedition reward apply in `RpgStore.Expeditions.cs`), right
    /// after the XP write each already made, under the SAME SQLite transaction and `lock (_gate)`
    /// critical section -- no interleaving writer can observe the level and roll as separable facts,
    /// and a grant failure rolls the XP write back with the unlock state.
    /// </summary>
    void TryRollActionUnlocks(SqliteConnection db, string instanceId, int typeId, int levelsGained,
        SqliteTransaction? tx = null)
    {
        if (levelsGained <= 0) return;
        // No configured host has opted into the unlock ladder yet (every test project except the
        // ones that explicitly call UnlockTuningPolicy.Configure) -- skip, not an error. Matches
        // ActionFamilyMapPolicy's own "byte-identical unless configured" default one level up.
        if (UnlockTuningPolicy.Tuning is not { } tuning) return;

        // CreatureSpeciesCatalog.Configure(...) may not have run in every host that reaches here (it
        // exposes no IsConfigured check) -- treated the same as "no species" (ActionEligibility.
        // Candidates' own null contract), not a reason to fail the whole roll: a specimen with no
        // resolvable species simply sees only General-scope candidates, same as today.
        string? speciesKey = null;
        try { speciesKey = CreatureSpeciesCatalog.All.FirstOrDefault(s => s.GameTypeId == typeId)?.SpeciesId; }
        catch (InvalidOperationException) { /* not configured in this host -- no species */ }

        // `action-grant-owner-kind-durability`, FIXED 2026-09-07: found during T59.8's own end-to-end
        // investigation that the unlock RATCHET's own state (EarnCount/Held) is durable
        // (OwnerKind.UniqueActor), but the grant itself was written under OwnerKind.Entity to match
        // the then-real read path (WebMatchService.EquippedActionIdsFor, T22, 2026-08-28, predating
        // UniqueActor's 2026-09-01 introduction) -- a real, session-scoped durability gap, since
        // `ClearSessionScopedBindings()` (real caller: `Program.cs:654`) deletes every `entity:`
        // binding on a session boundary. `EquippedActionIdsFor`'s own grant read is fixed to
        // UniqueActor the same pass; both sides must always agree, so this write moves with it.
        var unlockStateOwner = new OwnerScope(OwnerKind.UniqueActor, instanceId);
        var grantOwner = new OwnerScope(OwnerKind.UniqueActor, instanceId);
        // RS-CF2 / ADG-F5 (measured 2026-09-22): this catalog is narrowed to the rows
        // `UpsertGrantUnlocked` will actually accept, through the SAME predicate the grant itself is
        // judged by (`ActionValidator.ValidateGrantable`). Before this, the roll was handed every
        // *eligible* row -- eligibility answers "who may hold this action" (general / family /
        // species), never "may this be granted" -- so `act.attack` (`kind = basic`, never granted by
        // construction) sat in the pool. A seeded pick that landed on it threw from the grant delegate
        // below, inside the XP award's own transaction, and
        // `POST /api/unique/actors/{instanceId}/xp` answered 500 for those specimens and 200 for the
        // rest -- the E2E flake the row names. A row a grant refuses is not a drawable candidate; the
        // throw below stays as the construction check that the chooser and the writer still agree.
        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => GetUnlockStateUnlocked(db, unlockStateOwner, tx),
            saveUnlockState: (_, state) => SaveUnlockStateUnlocked(db, unlockStateOwner, state, tx),
            catalog: () => ListActionIdsUnlocked(db, tx)
                .Select(id => GetActionUnlocked(db, id, tx))
                .Where(a => a is not null && ActionValidator.ValidateGrantable(a).IsOk)
                .Select(a => a!)
                .ToList(),
            familyOf: ActionFamilyMapPolicy.Map,
            grant: (_, actionId) =>
            {
                var rejection = UpsertGrantUnlocked(
                    db,
                    new ActionGrantRow(grantOwner.Kind, grantOwner.Key, actionId, Source: "unlock-ladder"),
                    grantId: $"unlock:{instanceId}:{actionId}", tx: tx);
                if (!rejection.IsOk)
                    throw new InvalidOperationException($"action unlock grant refused: {rejection.Reason}");
            });

        var specimenSeed = Fnv1a64(instanceId);
        for (var i = 0; i < levelsGained; i++)
            service.TryRollOnce(instanceId, speciesKey, specimenSeed, tuning);
    }
}

/// <summary>
/// Raised by <see cref="RpgStore.UpsertUniqueEquipment"/> (and therefore
/// <see cref="RpgStore.ClearUniqueEquipmentSlot"/>) when the legacy relic wire is asked to write a
/// role that a real <c>rolled</c> item assignment already holds — defect R1, 2026-09-06.
///
/// <para>Its own type, on <c>WorkbenchApplyRefused</c>'s pattern, rather than another
/// <c>ArgumentException</c>/<c>InvalidOperationException</c>: <c>UniqueActorService.PutEquipment</c>
/// already tells those two apart by <c>ParamName</c> and by a <c>StartsWith</c> on the message, and a
/// third refusal squeezed into that scheme would be reported as <c>not_found</c> or as one of the two
/// 400-shaped validation reasons. This one is a genuine conflict and answers 409.</para>
/// </summary>
public sealed class UniqueEquipmentSlotClaimed : Exception
{
    public UniqueEquipmentSlotClaimed(string reason) : base(reason) => Reason = reason;

    public string Reason { get; }
}
