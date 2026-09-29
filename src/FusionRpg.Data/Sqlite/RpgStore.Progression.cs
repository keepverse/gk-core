using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One changed progression row, as the host broadcasts it after commit.</summary>
/// <param name="LevelUps">`empire-level` EP4.3 — the empire level crossings this write produced, if it
/// was the empire credit. Null for every other kind and for the overwhelming majority of empire writes
/// (a credit that crossed no level). It rides the dirty rather than a second queue so that a rolled-back
/// transaction cannot broadcast it: the batch this dirty travels in is nulled on the rollback path,
/// never drained.</param>
/// <param name="Empire">`ai-empire-species` EP4.14 (R1) — which empire of the save this write credited,
/// so the caller can name it on the wire (trigger T1's additive `empire` field, trigger T5's). Every
/// pre-R1 write was the human empire; the value is carried rather than re-derived because only the write
/// knows which empire earned (a Zomboss species level-up and a human one are the same `Kind`).</param>
public readonly record struct RpgProgressionDirty(
    long PlayerId, string Kind, int TypeId, long Revision,
    IReadOnlyList<RpgStore.EmpireLevelUpEvent>? LevelUps = null,
    string? Empire = null);

public readonly record struct EventInsertNotify(
    IReadOnlyList<long> ActivityPlayers,
    IReadOnlyList<RpgProgressionDirty> Progression,
    IReadOnlyList<long> ClosedRunIds);

public sealed partial class RpgStore
{
    static readonly LevelChangePipeline ProgressionPipeline = new();

    /// <summary>
    /// SE4.20 — the Tier A tables are keyed `(save_id, empire_id)`. Every writer today writes the human
    /// empire's row, so the store API still takes a save id and resolves that empire here; SE4.21 lifts
    /// the parameter to an `EmpireRef`. An unseeded save throws rather than writing a guessed empire.
    /// </summary>
    static string HumanEmpireIdUnlocked(SqliteConnection db, long saveId) =>
        HumanEmpireOfOrNull(db, saveId)?.Value ?? throw new SaveEmpiresNotSeeded(saveId);

    List<RpgProgressionDirty> ApplyRpgProgressionFromActivityUnlocked(
        SqliteConnection db, SaveId save, long? runId, string t, string factKind,
        string payload, string dedupeKey, long factId, bool pvzGame = true,
        string? sourceKind = null, string? sourceId = null)
    {
        // SE4.21 (G4): every award names the save it belongs to, and a fact carrying a run that belongs
        // to a different save is a wiring defect, not a guess. A run that resolves to no save at all
        // awards nothing (reported by the caller's own empty result), never the current save.
        if (runId is { } run && run != 0)
        {
            var runSave = SaveOfRunUnlocked(db, run);
            if (runSave is null) return new List<RpgProgressionDirty>();
            if (runSave.Value != save)
                throw new InvalidOperationException(
                    $"activity for run {run} belongs to save {runSave.Value.Value}, not save {save.Value}");
        }

        var playerId = save.Value;
        // Which empire earns stays `EP ai-empire-species`'s rule; today's behaviour is the human empire.
        var owner = new EmpireRef(save, new EmpireId(HumanEmpireIdUnlocked(db, playerId)));
        var dirty = new List<RpgProgressionDirty>();
        var result = TryString(payload, "result");
        var typeId = TryInt(payload, "type");
        // Run-scope the ledger dedupe: the raw fact dedupe ("run", reused ptrs) collides across
        // matches — a second match's defeat XP was silently eaten by INSERT OR IGNORE. Fact-level
        // dedupe still gates true replays; this key only separates distinct facts.
        var runScopedDedupe = (runId ?? 0) + ":" + dedupeKey;
        foreach (var award in RpgXpAwardMap.FromActivity(factKind, result, typeId, payload, sourceKind, sourceId))
        {
            if (award.Kind == RpgActorKinds.Species && !IsEmpireGeneralSource(sourceKind, sourceId, factKind, typeId))
                continue;
            // Web-mode runs never level PvZ almanac type actors (audit 2026-08-21) —
            // player-kind XP still flows (one economy); creature specimen XP is expedition-owned.
            // species-build T1.2: a species row is NOT a PvZ almanac type (spec-species-xp.md §2's
            // ⛔ callout) — this rule exists to protect `plant`/`zombie` kind rows specifically, so it
            // must not widen to skip Species too, or standalone/web-mode species levelling breaks.
            if (!pvzGame && award.Kind is not (RpgActorKinds.Player or RpgActorKinds.Species))
                continue;
            var ledgerPayload = award.Reason == RpgXpReasons.Kill
                ? MergePowerScalePayload(payload, award.PowerScale)
                : payload;
            // ai-empire-species EP4.14 (R1): a species row earns for the empire of the SIDE it was
            // fielded on - a zombie species credits Zomboss's empire of this run's save, a plant
            // species the human's - through the one side->empire mapping every other attribution
            // already uses. Every non-species award (player, plant/zombie almanac) keeps the human
            // owner it always had. A save that does not carry the earning empire credits nothing for
            // this award rather than minting a row for an empire it does not have.
            var awardOwner = award.Kind == RpgActorKinds.Species
                ? SpeciesOwnerForSideUnlocked(db, owner, FactSideOf(factKind))
                : owner;
            if (awardOwner is null) continue;
            // empire-level EP4.3: a species write can credit its empire as a SIDE EFFECT of this award,
            // so the collector travels with the call and its dirties join this caller's own — the same
            // list and the same post-commit batch, exactly as the run-completion and Zomboss-clock terms
            // below already do for their own nested writes.
            var sideEffects = new List<RpgProgressionDirty>();
            var d = TryApplyXpUnlocked(
                db, awardOwner.Value, award.Kind, award.TypeId, runId ?? 0, t,
                award.Delta, award.Reason, runScopedDedupe, factId, ledgerPayload, award.ScopeKey, sideEffects);
            if (d is { } item)
            {
                dirty.Add(item);
                _progressionNotifyBatch?.Add(item);
            }
            foreach (var nested in sideEffects)
            {
                dirty.Add(nested);
                _progressionNotifyBatch?.Add(nested);
            }
        }

        // species-build T1.3: the run-completion term (the DOMINANT term, spec-species-xp.md §3)
        // fires once per resolved match for every EMPIRE-GENERAL species that had at least one
        // source-validated PlantPlaced/ZombieSpawned fact in THIS run — derived entirely from facts already recorded above by
        // earlier calls to this same method, never a new injector capture. Not `!pvzGame`-gated for
        // the same reason the per-placement award above isn't (a species row is not a PvZ almanac
        // type). `runId` of 0/null has no real run scope to query, so it's skipped rather than
        // silently pooling unrelated placements under a shared "run 0" bucket.
        if (factKind == PvzActivityKinds.MatchEnded && runId is { } rid && rid != 0
            && SpeciesProgressionTuningHub.IsConfigured && CreatureSpeciesCatalog.IsConfigured)
        {
            foreach (var d in ApplyRunCompletionSpeciesAwardsUnlocked(db, owner, rid, t, factId))
            {
                dirty.Add(d);
                _progressionNotifyBatch?.Add(d);
            }
        }

        // zomboss-commander-clock SP7.2 -- fires once per resolved LAWN run only (`pvzGame`; a
        // web/standalone-mode run never touches Zomboss's own commander clock, per the acceptance's
        // own "pvzGame == false gives nothing"). `runId` of 0/null has no real run to dedupe against,
        // same reasoning as the species run-completion term just above.
        if (factKind == PvzActivityKinds.MatchEnded && pvzGame && runId is { } zombossRunId && zombossRunId != 0)
        {
            foreach (var d in ApplyZombossCommanderClockUnlocked(db, save, zombossRunId, t, result, factId))
            {
                dirty.Add(d);
                _progressionNotifyBatch?.Add(d);
            }
        }

        return dirty;
    }

    /// <summary>
    /// `zomboss-commander-clock` SP7.2 — see the call site's comment for when this fires. A human
    /// `defeat` gives Zomboss's commander the run-victory award (Zomboss "won" the run); a human
    /// `victory` gives it the smaller run-defeat/consolation award. No result, an unknown result,
    /// or a result that normalizes to neither gives nothing.
    ///
    /// <para><b>Identity (R3):</b> always <c>EmpireRef(save, EmpireId.Zomboss)</c> — the run's OWN
    /// save's Zomboss empire, never a human row, never a literal id, so two saves advance two
    /// different levels. A save with no seeded Zomboss empire (a legacy save from before SE4.22, or
    /// one that genuinely never seeded it) awards nothing here and creates no row on the fly —
    /// <c>rpg_save_empires</c> is checked, never assumed, matching <c>AppendSpeciesModUnlocked</c>'s
    /// own check against the same table.</para>
    ///
    /// <para>When the configured award resolves to 0 — an old <c>progression.v1.json</c>-shaped
    /// tuning object still active, or a balance file that explicitly zeroes it — this gives nothing,
    /// silently. <b>This corrects <c>XpAwardsTuning.ZombossRunVictoryXp</c>'s own SP7.1 doc comment</b>,
    /// which named <c>PointBudget.SkillPointsFor</c>'s THROWING precedent as the intended "first use"
    /// refusal: checked against evidence here, that was the wrong sibling to cite. This store's own
    /// specimen-award writers (<c>AwardUniqueLawnKillUnlocked</c>, <c>AwardUniqueLawnDurationUnlocked</c>)
    /// already establish the real precedent for THIS tuning class — <c>if (delta &lt;= 0) return;</c>,
    /// a silent skip, never a throw — and this follows that one instead. `RpgXpAwards.Configure`
    /// never having run at all is a different failure and is not this method's concern: the shared
    /// per-award loop above already dereferences `RpgXpAwards.Defeat` for every `MatchEnded` fact
    /// before this method is ever reached, so by the time control arrives here `RpgXpAwards` is
    /// already known-configured or the whole call has already thrown.</para>
    /// </summary>
    List<RpgProgressionDirty> ApplyZombossCommanderClockUnlocked(
        SqliteConnection db, SaveId save, long runId, string t, string? resultRaw, long? factId)
    {
        var result = PvzActivityKinds.NormalizeMatchResult(resultRaw);
        var (delta, reason) = result switch
        {
            "defeat" => (RpgXpAwards.ZombossRunVictoryXp, RpgXpReasons.ZombossRunVictory),
            "victory" => (RpgXpAwards.ZombossRunDefeatXp, RpgXpReasons.ZombossRunDefeat),
            _ => (0L, string.Empty)
        };
        if (delta <= 0) return new List<RpgProgressionDirty>();

        using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM rpg_save_empires WHERE save_id=$s AND empire_id=$e;";
            check.Parameters.AddWithValue("$s", save.Value);
            check.Parameters.AddWithValue("$e", EmpireId.Zomboss.Value);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                return new List<RpgProgressionDirty>();
        }

        var zomboss = new EmpireRef(save, EmpireId.Zomboss);
        // Run-scoped dedupe (zomboss-run:{runId}) so a replayed MatchEnded fact never double-pays it —
        // the same replay protection the per-award loop above gives every other award via `INSERT OR
        // IGNORE` on the ledger's dedupe key.
        var d = TryApplyXpUnlocked(
            db, zomboss, RpgActorKinds.Player, 0, runId, t,
            delta, reason, $"zomboss-run:{runId}", factId, payloadJson: null);
        return d is { } item ? new List<RpgProgressionDirty> { item } : new List<RpgProgressionDirty>();
    }

    /// <summary>`species-build` T1.3 — see the call site's comment above for the design. One award
    /// per DISTINCT species fielded in <paramref name="runId"/>, deduped so a replayed `MatchEnded`
    /// fact never double-pays it (T1.2/T1.3's own idempotence acceptance criterion).</summary>
    List<RpgProgressionDirty> ApplyRunCompletionSpeciesAwardsUnlocked(
        SqliteConnection db, EmpireRef owner, long runId, string t, long? factId)
    {
        // Both species-XP paths (per-placement above, run completion here) award to the same owner.
        var playerId = owner.Save.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fielded = new List<(CreatureSpeciesDef Species, string Side)>();
        var index = new LawnElementIndex(CreatureSpeciesCatalog.All);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT kind, payload_json, source_kind, source_id FROM pvz_activity_facts
                WHERE player_id=$p AND run_id=$r AND kind IN ($pp, $zs);
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$r", runId);
            cmd.Parameters.AddWithValue("$pp", PvzActivityKinds.PlantPlaced);
            cmd.Parameters.AddWithValue("$zs", PvzActivityKinds.ZombieSpawned);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = r.GetString(0);
                var payloadJson = r.IsDBNull(1) ? "{}" : r.GetString(1);
                var sourceKind = r.IsDBNull(2) ? null : r.GetString(2);
                var sourceId = r.IsDBNull(3) ? null : r.GetString(3);
                if (!IsEmpireGeneralSource(sourceKind, sourceId, kind, TryInt(payloadJson, "type"))) continue;
                if (TryInt(payloadJson, "type") is not { } tid) continue;
                var side = kind == PvzActivityKinds.PlantPlaced ? "plant" : "zombie";
                if (!index.TryGet(side, tid, out var species)) continue;
                if (seen.Add(species.SpeciesId))
                    fielded.Add((species, side));
            }
        }

        var results = new List<RpgProgressionDirty>();
        foreach (var (species, side) in fielded)
        {
            // EP4.14 (R1): the run-completion half of the same rule the per-placement path applies —
            // each fielded species credits ITS OWN side's empire of the run's save.
            var awardOwner = SpeciesOwnerForSideUnlocked(db, owner, side);
            if (awardOwner is null) continue;
            var dedupe = $"run-complete:{runId}:{species.SpeciesId}";
            // empire-level EP4.3: same side-effect collector as the per-placement path above — this is
            // the second of the two species XP paths, and the credit must cover both.
            var sideEffects = new List<RpgProgressionDirty>();
            var d = TryApplyXpUnlocked(
                db, awardOwner.Value, RpgActorKinds.Species, species.CreatureTypeId, runId, t,
                SpeciesProgressionTuningHub.Tuning.RunCompletionAward, RpgXpReasons.SpeciesRunComplete,
                dedupe, factId, payloadJson: null, scopeKey: species.SpeciesId, sideEffects: sideEffects);
            if (d is { } item) results.Add(item);
            results.AddRange(sideEffects);
        }
        return results;
    }

    /// <summary>
    /// `ai-empire-species` EP4.14 (R1) — the empire a species row of this save earns for, given the side
    /// it was fielded on. The human empire keeps the owner the caller already resolved; any other empire
    /// must be one this save actually carries (`rpg_save_empires`), or there is no honest owner and the
    /// award credits nothing — never a row minted for an empire the save does not have, the same rule
    /// <see cref="ApplyZombossCommanderClockUnlocked"/> applies to the commander clock.
    ///
    /// <para>Returns <c>null</c> for "no owner", which the two species paths turn into a skip. That is a
    /// real, reported outcome (the caller's dirty list simply lacks the write), not an error: a legacy
    /// save from before `save-identity` SE4.22 carries no Zomboss empire, and guessing the human's row
    /// for it is exactly the leak R1 closes.</para>
    /// </summary>
    EmpireRef? SpeciesOwnerForSideUnlocked(SqliteConnection db, EmpireRef humanOwner, string side)
    {
        var empire = KillAttribution.EmpireOf(side);
        if (empire == humanOwner.Empire) return humanOwner;
        foreach (var row in EmpiresOfUnlocked(db, humanOwner.Save.Value))
            if (row.Empire == empire) return new EmpireRef(humanOwner.Save, empire);
        return null;
    }

    /// <summary>`ai-empire-species` EP4.14 — the side a species-placement fact was fielded on. The
    /// vocabulary is closed (<c>PlantPlaced</c>/<c>ZombieSpawned</c>), and that pair is also the only
    /// one `RpgXpAwardMap` projects a species award from.</summary>
    static string FactSideOf(string factKind) =>
        factKind == PvzActivityKinds.ZombieSpawned ? "zombie" : "plant";

    static bool IsEmpireGeneralSource(string? sourceKind, string? sourceId, string? factKind = null, int? typeId = null)
    {
        if (!string.Equals(sourceKind, CreatureProgressionSource.EmpireGeneralKind, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(sourceId)) return false;
        try
        {
            if (CreatureProgressionSource.Parse(sourceKind!, sourceId) is not CreatureProgressionSource.EmpireGeneralSource general)
                return false;
            if (factKind is not (PvzActivityKinds.PlantPlaced or PvzActivityKinds.ZombieSpawned)) return true;
            if (typeId is not { } tid || !CreatureSpeciesCatalog.IsConfigured) return false;
            var side = factKind == PvzActivityKinds.PlantPlaced ? "plant" : "zombie";
            return new LawnElementIndex(CreatureSpeciesCatalog.All).TryGet(side, tid, out var species)
                && string.Equals(species.SpeciesId, general.SpeciesId, StringComparison.Ordinal);
        }
        catch (InvalidOperationException) { return false; }
        catch (FormatException) { return false; }
    }

    static string MergePowerScalePayload(string? payloadJson, double powerScale)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.NameEquals("powerScale")) continue;
                    p.WriteTo(writer);
                }
                writer.WriteNumber("powerScale", powerScale);
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return System.Text.Json.JsonSerializer.Serialize(new { powerScale });
        }
    }

    RpgProgressionDirty? TryApplyXpUnlocked(
        SqliteConnection db, EmpireRef owner, string kind, int typeId, long runId, string t,
        long delta, string reason, string dedupeKey, long? factId, string? payloadJson, string? scopeKey = null,
        List<RpgProgressionDirty>? sideEffects = null)
    {
        var playerId = owner.Save.Value;
        var empire = owner.Empire.Value;
        EnsureActorRowUnlocked(db, owner, kind, typeId, scopeKey);
        var state = ReadActorStateUnlocked(db, owner, kind, typeId);
        var levelBefore = state.Level;
        var xpBefore = state.Xp;
        var demotionBefore = state.DemotionCount;
        // `RpgXpApply.Apply` mutates the state it is handed, `HighestLevel` included, so the pre-apply
        // value has to be read here — the same reason the three locals above are. EP4.3's credit reads
        // this PAIR, and reading it after the call would make `highestAfter <= highestBefore` always
        // true and silently credit nothing.
        var highestBefore = state.HighestLevel;

        var applied = RpgXpApply.Apply(kind, state, delta, playerId, typeId, reason);
        ProgressionPipeline.RunAll(applied.LevelChanges);

        using (var ins = db.CreateCommand())
        {
            ins.CommandText = """
                INSERT OR IGNORE INTO rpg_xp_ledger(
                  save_id, empire_id, kind, type_id, run_id, t, delta, reason, activity_fact_id,
                  level_before, xp_before, level_after, xp_after, demotion_before, demotion_after, payload_json, dedupe_key)
                VALUES($p,$e,$k,$tid,$r,$t,$d,$reason,$fid,$lb,$xb,$la,$xa,$db,$da,$pj,$dk);
                """;
            ins.Parameters.AddWithValue("$p", playerId);
            ins.Parameters.AddWithValue("$e", empire);
            ins.Parameters.AddWithValue("$k", kind);
            ins.Parameters.AddWithValue("$tid", typeId);
            ins.Parameters.AddWithValue("$r", runId);
            ins.Parameters.AddWithValue("$t", t);
            ins.Parameters.AddWithValue("$d", delta);
            ins.Parameters.AddWithValue("$reason", reason);
            ins.Parameters.AddWithValue("$fid", (object?)factId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$lb", levelBefore);
            ins.Parameters.AddWithValue("$xb", xpBefore);
            ins.Parameters.AddWithValue("$la", applied.State.Level);
            ins.Parameters.AddWithValue("$xa", applied.State.Xp);
            ins.Parameters.AddWithValue("$db", demotionBefore);
            ins.Parameters.AddWithValue("$da", applied.State.DemotionCount);
            ins.Parameters.AddWithValue("$pj", (object?)payloadJson ?? DBNull.Value);
            ins.Parameters.AddWithValue("$dk", dedupeKey);
            if (ins.ExecuteNonQuery() <= 0)
                return null;
        }

        long ledgerId;
        using (var idCmd = db.CreateCommand())
        {
            idCmd.CommandText = "SELECT last_insert_rowid();";
            ledgerId = Convert.ToInt64(idCmd.ExecuteScalar() ?? 0L);
        }

        var bucketsJson = ReadXpByReasonJsonUnlocked(db, owner, kind, typeId);
        bucketsJson = MergeXpReasonBucket(bucketsJson, reason, delta);

        var now = ServerClock.UtcNowDateTime.ToString("o");
        using (var up = db.CreateCommand())
        {
            up.CommandText = """
                UPDATE rpg_actor_progression SET
                  level=$l, xp=$x, highest_level=$h, demotion_count=$dm, revision=$rev, updated_utc=$t,
                  through_ledger_id=$tl, xp_by_reason_json=$bj
                WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$tid;
                """;
            up.Parameters.AddWithValue("$l", applied.State.Level);
            up.Parameters.AddWithValue("$x", applied.State.Xp);
            up.Parameters.AddWithValue("$h", applied.State.HighestLevel);
            up.Parameters.AddWithValue("$dm", applied.State.DemotionCount);
            up.Parameters.AddWithValue("$rev", applied.State.Revision);
            up.Parameters.AddWithValue("$t", now);
            up.Parameters.AddWithValue("$tl", ledgerId);
            up.Parameters.AddWithValue("$bj", bucketsJson);
            up.Parameters.AddWithValue("$p", playerId);
            up.Parameters.AddWithValue("$e", empire);
            up.Parameters.AddWithValue("$k", kind);
            up.Parameters.AddWithValue("$tid", typeId);
            up.ExecuteNonQuery();
        }

        // empire-level EP4.3 (spec-empire-level.md): a species reaching a NEW highest level credits its
        // OWN empire, once per level ever. Placed here — after the species ledger row landed and the row
        // update ran — because a replayed species award returns at the `INSERT OR IGNORE` above and must
        // credit nothing, and because the credit must be written inside the SAME transaction as the
        // species level that paid for it. `state.HighestLevel` is the pre-apply highest level the row
        // carried; `applied.State.HighestLevel` is the post-apply one, and the rule reads the PAIR so a
        // demote-and-reclimb pays nothing the second time (see the credit's own doc comment).
        if (string.Equals(kind, RpgActorKinds.Species, StringComparison.Ordinal))
            CreditEmpireForSpeciesLevelsUnlocked(
                db, owner, typeId, SpeciesSideOf(typeId), highestBefore, applied.State.HighestLevel,
                runId, t, factId, sideEffects ?? new List<RpgProgressionDirty>());

        // The empire write is the only kind that queues level crossings; every other kind returns the
        // same dirty it always did (a null LevelUps member, which reads as "nothing to broadcast").
        var levelUps = string.Equals(kind, RpgActorKinds.Empire, StringComparison.Ordinal)
            ? LevelUpEventsFor(playerId, owner, applied.LevelChanges)
            : null;
        // respec-free-counter EP4.5: each crossing's grants land in THIS transaction, keyed `L{level}`,
        // so the stock and the level that paid for it commit or roll back together. Zero-amount grants
        // write nothing (the `For` call returns an empty list for a published 0).
        if (levelUps is { Count: > 0 }) ApplyEmpireLevelGrantsUnlocked(db, owner, levelUps, now);
        return new RpgProgressionDirty(playerId, kind, typeId, applied.State.Revision, levelUps, owner.Empire.Value);
    }

    static string ReadXpByReasonJsonUnlocked(SqliteConnection db, EmpireRef owner, string kind, int typeId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT xp_by_reason_json FROM rpg_actor_progression
            WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
            """;
        cmd.Parameters.AddWithValue("$p", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", typeId);
        var o = cmd.ExecuteScalar();
        if (o is string s && !string.IsNullOrWhiteSpace(s)) return s;
        return "{}";
    }

    /// <summary>
    /// Reads a whole-XP column tolerantly. XP became `long`/INTEGER on 2026-09-04 (docs/architecture/numeric-types.md numeric
    /// rule — it is a persisted magnitude), but `CREATE TABLE IF NOT EXISTS` never rewrites an
    /// existing database, so a player who has played before still has a column with REAL affinity
    /// holding whole values like `100.0`. `GetValue` returns the STORED class (double there, long in
    /// a fresh db) and `Convert.ToInt64` accepts both, so no data migration is needed and no legacy
    /// save fails to load. Every value ever written was integral: awards and curve steps are whole,
    /// and the one scaled award rounds at its own boundary (RpgXpAwardMap.ScaledAward).
    /// </summary>
    static long ReadXp(Microsoft.Data.Sqlite.SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? 0L : Convert.ToInt64(r.GetValue(ordinal));

    static string MergeXpReasonBucket(string bucketsJson, string reason, long delta)
    {
        Dictionary<string, XpReasonBucket> map;
        try
        {
            map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, XpReasonBucket>>(bucketsJson)
                  ?? new Dictionary<string, XpReasonBucket>(StringComparer.Ordinal);
        }
        catch
        {
            map = new Dictionary<string, XpReasonBucket>(StringComparer.Ordinal);
        }
        if (!map.TryGetValue(reason, out var bucket) || bucket is null)
            bucket = new XpReasonBucket();
        bucket.Sum += delta;
        bucket.Count += 1;
        map[reason] = bucket;
        return System.Text.Json.JsonSerializer.Serialize(map);
    }

    /// <summary>A denormalized per-reason aggregate, persisted as JSON in a payload column.
    /// <c>Sum</c> stays <c>double</c> deliberately: existing payloads on live databases serialize it as
    /// `12.0`, and a `long` property would refuse to deserialize those. This is a CACHE derived from
    /// `rpg_xp_ledger.delta`, which IS `long` end to end — the SSOT value is exact, and only this
    /// rebuildable summary carries the wider type.</summary>
    sealed class XpReasonBucket
    {
        public double Sum { get; set; }
        public int Count { get; set; }
    }

    void EnsureActorRowUnlocked(SqliteConnection db, EmpireRef owner, string kind, int typeId, string? scopeKey = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO rpg_actor_progression(
              save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, scope_key)
            VALUES($p,$e,$k,$t,1,0,1,0,0,$u,$sk);
            """;
        cmd.Parameters.AddWithValue("$p", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", typeId);
        cmd.Parameters.AddWithValue("$u", ServerClock.UtcNowDateTime.ToString("o"));
        // scope_key (species-build T1.1): a human-readable key alongside type_id for kind='species'
        // rows — NULL for every other kind, and NULL here means "leave the column's default alone"
        // (CreatureTypeId never repeats across species, so nothing keys off this column, only reads it).
        cmd.Parameters.AddWithValue("$sk", (object?)scopeKey ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    RpgActorState ReadActorStateUnlocked(SqliteConnection db, EmpireRef owner, string kind, int typeId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT level, xp, highest_level, demotion_count, revision
            FROM rpg_actor_progression WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
            """;
        cmd.Parameters.AddWithValue("$p", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", typeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return new RpgActorState();
        return new RpgActorState
        {
            Level = r.GetInt64(0),
            Xp = r.GetInt64(1),
            HighestLevel = r.GetInt64(2),
            DemotionCount = r.GetInt64(3),
            Revision = r.GetInt64(4)
        };
    }

    public RpgProgressionSummaryDto? GetRpgProgressionSummary(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            var player = ReadActorDtoUnlocked(db, playerId, RpgActorKinds.Player, 0)
                ?? DefaultPlayerDtoUnlocked(playerId);
            var plants = ListActorsUnlocked(db, playerId, RpgActorKinds.Plant, "level", 5);
            var zombies = ListActorsUnlocked(db, playerId, RpgActorKinds.Zombie, "level", 5);
            var plantCount = CountActorsUnlocked(db, playerId, RpgActorKinds.Plant);
            var zombieCount = CountActorsUnlocked(db, playerId, RpgActorKinds.Zombie);
            return new RpgProgressionSummaryDto
            {
                PlayerId = playerId,
                Player = player,
                PlantActorCount = plantCount,
                ZombieActorCount = zombieCount,
                HighestPlantLevel = MaxHighestLevelUnlocked(db, playerId, RpgActorKinds.Plant),
                HighestZombieLevel = MaxHighestLevelUnlocked(db, playerId, RpgActorKinds.Zombie),
                TopPlants = plants,
                TopZombies = zombies
            };
        }
    }

    static RpgActorProgressionDto DefaultPlayerDtoUnlocked(long playerId)
    {
        var (first, step) = RpgXpCurve.ParamsFor(RpgActorKinds.Player);
        return new RpgActorProgressionDto
        {
            PlayerId = playerId,
            Kind = RpgActorKinds.Player,
            TypeId = 0,
            TypeName = "Player",
            Level = 1,
            Xp = 0,
            XpToNext = RpgXpCurve.XpToNext(RpgActorKinds.Player, 1),
            HighestLevel = 1,
            DemotionCount = 0,
            Revision = 0,
            UpdatedAt = "",
            CurveFirst = first,
            CurveStep = step
        };
    }

    static long MaxHighestLevelUnlocked(SqliteConnection db, long playerId, string kind)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(highest_level), 0)
            FROM rpg_actor_progression WHERE save_id=$p AND empire_id=$e AND kind=$k;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
        cmd.Parameters.AddWithValue("$k", kind);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    public RpgProgressionListDto? ListRpgProgression(
        long playerId, string? kind, string sort = "level", int limit = 200, int offset = 0)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            limit = Math.Clamp(limit, 1, 500);
            offset = Math.Max(0, offset);
            var total = string.IsNullOrWhiteSpace(kind)
                ? CountActorsUnlocked(db, playerId, null)
                : CountActorsUnlocked(db, playerId, kind);
            return new RpgProgressionListDto
            {
                PlayerId = playerId,
                Items = ListActorsUnlocked(db, playerId, kind, sort, limit, offset),
                Total = total,
                Limit = limit,
                Offset = offset
            };
        }
    }

    public RpgActorProgressionDto? GetRpgActor(long playerId, string kind, int typeId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            if (!RpgActorKinds.IsKnown(kind)) return null;
            return ReadActorDtoUnlocked(db, playerId, kind, typeId);
        }
    }

    /// <summary>`species-build` T3.1 (module 6, `allocation-transport`) — every species this player has
    /// EVER fielded (a `kind='species'` row exists the first time a placement or expedition win awards
    /// it any XP, `EnsureActorRowUnlocked`) — the small, levelled subset the aptitude payload actually
    /// sends, never the full 829-row corpus. Reads `scope_key` (T1.1's own human-readable column)
    /// rather than reconstructing speciesId from `type_id` (`CreatureTypeId`), which would need a reverse
    /// catalog lookup this store has no reason to own.</summary>
    public IReadOnlyList<string> ListLevelledSpeciesIds(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT scope_key FROM rpg_actor_progression
                WHERE save_id=$p AND empire_id=$e AND kind=$k AND scope_key IS NOT NULL
                ORDER BY scope_key;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
            cmd.Parameters.AddWithValue("$k", RpgActorKinds.Species);
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read())
                ids.Add(r.GetString(0));
            return ids;
        }
    }

    public RpgXpLedgerPageDto? ListRpgXpLedger(
        long playerId, string? kind, int? typeId, string? reason, int limit = 100, long? afterId = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            limit = Math.Clamp(limit, 1, 500);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT id, save_id, kind, type_id, run_id, t, delta, reason, activity_fact_id,
                       level_before, xp_before, level_after, xp_after, demotion_before, demotion_after, payload_json
                FROM rpg_xp_ledger WHERE save_id=$p AND empire_id=$e
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
            if (!string.IsNullOrWhiteSpace(kind))
            {
                cmd.CommandText += " AND kind=$k";
                cmd.Parameters.AddWithValue("$k", kind.Trim());
            }
            if (typeId is { } tid)
            {
                cmd.CommandText += " AND type_id=$tid";
                cmd.Parameters.AddWithValue("$tid", tid);
            }
            if (!string.IsNullOrWhiteSpace(reason))
            {
                cmd.CommandText += " AND reason=$rs";
                cmd.Parameters.AddWithValue("$rs", reason.Trim());
            }
            if (afterId is { } aid)
            {
                cmd.CommandText += " AND id < $after";
                cmd.Parameters.AddWithValue("$after", aid);
            }
            cmd.CommandText += " ORDER BY id DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$lim", limit);
            var items = new List<(long Id, long PlayerId, string Kind, int TypeId, long RunId, string T, long Delta, string Reason, long? FactId, long Lb, long Xb, long La, long Xa, long Db, long Da, string? Payload)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    items.Add((
                        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4), r.GetString(5),
                        // ReadXp, not GetDouble/GetInt64: a legacy database still holds REAL affinity on
                        // these three, a fresh one holds INTEGER, and Convert.ToInt64 accepts either.
                        ReadXp(r, 6), r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8),
                        r.GetInt64(9), ReadXp(r, 10), r.GetInt64(11), ReadXp(r, 12), r.GetInt64(13), r.GetInt64(14),
                        r.IsDBNull(15) ? null : r.GetString(15)));
                }
            }
            long? nextAfter = items.Count > 0 ? items[^1].Id : null;
            return new RpgXpLedgerPageDto
            {
                PlayerId = playerId,
                Items = items.Select(x => new RpgXpLedgerEntryDto
                {
                    Id = x.Id,
                    PlayerId = x.PlayerId,
                    Kind = x.Kind,
                    TypeId = x.TypeId,
                    TypeName = LookupTypeNameUnlocked(db, x.Kind, x.TypeId),
                    RunId = x.RunId,
                    T = x.T,
                    Delta = x.Delta,
                    Reason = x.Reason,
                    ActivityFactId = x.FactId,
                    LevelBefore = x.Lb,
                    XpBefore = x.Xb,
                    LevelAfter = x.La,
                    XpAfter = x.Xa,
                    DemotionBefore = x.Db,
                    DemotionAfter = x.Da,
                    PayloadJson = x.Payload
                }).ToList(),
                Limit = limit,
                NextAfterId = items.Count >= limit ? nextAfter : null
            };
        }
    }

    public RpgProgressionStatsDto? GetRpgProgressionStats(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;

            var xpByReasonMap = new Dictionary<string, RpgXpReasonStatDto>(StringComparer.Ordinal);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT xp_by_reason_json FROM rpg_actor_progression
                    WHERE save_id=$p AND empire_id=$e AND xp_by_reason_json IS NOT NULL AND xp_by_reason_json != '' AND xp_by_reason_json != '{}';
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    Dictionary<string, XpReasonBucket>? map = null;
                    try
                    {
                        map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, XpReasonBucket>>(r.GetString(0));
                    }
                    catch { /* ignore */ }
                    if (map is null) continue;
                    foreach (var kv in map)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
                        if (!xpByReasonMap.TryGetValue(kv.Key, out var agg))
                        {
                            agg = new RpgXpReasonStatDto { Reason = kv.Key };
                            xpByReasonMap[kv.Key] = agg;
                        }
                        agg.SumDelta += kv.Value.Sum;
                        agg.Count += kv.Value.Count;
                    }
                }
            }

            if (xpByReasonMap.Count == 0)
                BackfillXpReasonBucketsFromLedgerUnlocked(db, playerId, xpByReasonMap);

            var xpByReason = xpByReasonMap.Values.OrderBy(x => x.Reason, StringComparer.Ordinal).ToList();

            var recent = new List<RpgRecentDeltaDto>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT t, delta, reason FROM rpg_xp_ledger
                    WHERE save_id=$p AND empire_id=$e ORDER BY id DESC LIMIT 40;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    recent.Add(new RpgRecentDeltaDto
                    {
                        T = r.GetString(0),
                        Delta = ReadXp(r, 1),
                        Reason = r.GetString(2)
                    });
                }
            }

            return new RpgProgressionStatsDto
            {
                PlayerId = playerId,
                XpByReason = xpByReason,
                PlantLevels = LevelBucketsUnlocked(db, playerId, RpgActorKinds.Plant),
                ZombieLevels = LevelBucketsUnlocked(db, playerId, RpgActorKinds.Zombie),
                RecentDeltas = recent
            };
        }
    }

    /// <summary>
    /// One-shot heal for pre-W6 rows: rebuild per-actor buckets from ledger and fill aggregate map.
    /// </summary>
    void BackfillXpReasonBucketsFromLedgerUnlocked(
        SqliteConnection db, long playerId, Dictionary<string, RpgXpReasonStatDto> aggregate)
    {
        long ledgerCount;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM rpg_xp_ledger WHERE save_id=$p AND empire_id=$e;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
            ledgerCount = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        if (ledgerCount <= 0) return;

        var actors = new List<(string Kind, int TypeId)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT kind, type_id FROM rpg_actor_progression
                WHERE save_id=$p AND empire_id=$e
                  AND (xp_by_reason_json IS NULL OR xp_by_reason_json = '' OR xp_by_reason_json = '{}');
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                actors.Add((r.GetString(0), r.GetInt32(1)));
        }

        foreach (var (kind, typeId) in actors)
        {
            var map = new Dictionary<string, XpReasonBucket>(StringComparer.Ordinal);
            long maxId = 0;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, reason, delta FROM rpg_xp_ledger
                    WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t ORDER BY id;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$t", typeId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    maxId = Math.Max(maxId, r.GetInt64(0));
                    var reason = r.GetString(1);
                    var delta = ReadXp(r, 2);   // tolerant: REAL on legacy rows, INTEGER on fresh
                    if (!map.TryGetValue(reason, out var bucket) || bucket is null)
                        bucket = new XpReasonBucket();
                    bucket.Sum += delta;
                    bucket.Count += 1;
                    map[reason] = bucket;
                }
            }
            if (map.Count == 0) continue;
            var json = System.Text.Json.JsonSerializer.Serialize(map);
            using (var up = db.CreateCommand())
            {
                up.CommandText = """
                    UPDATE rpg_actor_progression
                    SET xp_by_reason_json=$j, through_ledger_id=$tl
                    WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
                    """;
                up.Parameters.AddWithValue("$j", json);
                up.Parameters.AddWithValue("$tl", maxId);
                up.Parameters.AddWithValue("$p", playerId);
                up.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
                up.Parameters.AddWithValue("$k", kind);
                up.Parameters.AddWithValue("$t", typeId);
                up.ExecuteNonQuery();
            }
            foreach (var kv in map)
            {
                if (!aggregate.TryGetValue(kv.Key, out var agg))
                {
                    agg = new RpgXpReasonStatDto { Reason = kv.Key };
                    aggregate[kv.Key] = agg;
                }
                agg.SumDelta += kv.Value.Sum;
                agg.Count += kv.Value.Count;
            }
        }

        // If no actors needed backfill but ledger exists (edge), still expose ledger GROUP BY once.
        if (aggregate.Count == 0)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT reason, COALESCE(SUM(delta),0), COUNT(*)
                FROM rpg_xp_ledger WHERE save_id=$p AND empire_id=$e
                GROUP BY reason ORDER BY reason;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                aggregate[r.GetString(0)] = new RpgXpReasonStatDto
                {
                    Reason = r.GetString(0),
                    SumDelta = r.GetDouble(1),
                    Count = r.GetInt32(2)
                };
            }
        }
    }

    static List<RpgLevelBucketDto> LevelBucketsUnlocked(SqliteConnection db, long playerId, string kind)
    {
        var list = new List<RpgLevelBucketDto>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT level, COUNT(*) FROM rpg_actor_progression
            WHERE save_id=$p AND empire_id=$e AND kind=$k GROUP BY level ORDER BY level;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
        cmd.Parameters.AddWithValue("$k", kind);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RpgLevelBucketDto { Level = r.GetInt64(0), Count = r.GetInt32(1) });
        return list;
    }

    public RpgActorProgressionDto? ClearRpgDemotion(long playerId, string kind, int typeId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            if (!RpgActorKinds.IsKnown(kind)) return null;
            if (ReadActorDtoUnlocked(db, playerId, kind, typeId) is null) return null;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE rpg_actor_progression
                    SET demotion_count=0, revision=revision+1, updated_utc=$t
                    WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$tid;
                    """;
                cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$tid", typeId);
                if (cmd.ExecuteNonQuery() <= 0) return null;
            }
            return ReadActorDtoUnlocked(db, playerId, kind, typeId);
        }
    }

    public RpgProgressionSummaryDto SeedRpgProgressionDemo(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null)
                throw new InvalidOperationException("player not found");
            var t = ServerClock.UtcNowDateTime.ToString("o");
            var owner = new EmpireRef(new SaveId(playerId), new EmpireId(HumanEmpireIdUnlocked(db, playerId)));
            var killPayload = MergePowerScalePayload("{}", 1.0);
            TryApplyXpUnlocked(db, owner, RpgActorKinds.Player, 0, 0, t, RpgXpAwards.Kill * 20, RpgXpReasons.Kill, "seed-player", null, killPayload);
            TryApplyXpUnlocked(db, owner, RpgActorKinds.Plant, 0, 0, t, RpgXpAwards.PlantPlace * 15, RpgXpReasons.PlantPlace, "seed-plant-0", null, "{}");
            TryApplyXpUnlocked(db, owner, RpgActorKinds.Zombie, 1, 0, t, RpgXpAwards.ZombieSpawn * 20, RpgXpReasons.ZombieSpawn, "seed-zombie-1", null, "{}");
        }
        return GetRpgProgressionSummary(playerId)!;
    }

    int CountActorsUnlocked(SqliteConnection db, long playerId, string? kind)
    {
        using var cmd = db.CreateCommand();
        if (string.IsNullOrWhiteSpace(kind))
        {
            cmd.CommandText = "SELECT COUNT(*) FROM rpg_actor_progression WHERE save_id=$p AND empire_id=$e;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM rpg_actor_progression WHERE save_id=$p AND empire_id=$e AND kind=$k;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
            cmd.Parameters.AddWithValue("$k", kind);
        }
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    List<RpgActorProgressionDto> ListActorsUnlocked(
        SqliteConnection db, long playerId, string? kind, string sort, int limit, int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var order = sort switch
        {
            "xp" => "xp DESC, level DESC",
            "updated" => "updated_utc DESC",
            "typeId" => "type_id ASC",
            _ => "level DESC, xp DESC"
        };
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT save_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc
            FROM rpg_actor_progression WHERE save_id=$p AND empire_id=$e
            {(string.IsNullOrWhiteSpace(kind) ? "" : " AND kind=$k")}
            ORDER BY {order} LIMIT $lim OFFSET $off;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$e", HumanEmpireIdUnlocked(db, playerId));
        if (!string.IsNullOrWhiteSpace(kind))
            cmd.Parameters.AddWithValue("$k", kind.Trim());
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        var rows = new List<(long PlayerId, string Kind, int TypeId, long Level, long Xp, long Highest, long Demotion, long Revision, string Updated)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                rows.Add((
                    r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), ReadXp(r, 4),
                    r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetString(8)));
            }
        }
        return rows.Select(row => ToActorDto(db, row.PlayerId, row.Kind, row.TypeId, row.Level, row.Xp, row.Highest, row.Demotion, row.Revision, row.Updated)).ToList();
    }

    /// <summary>SE4.21 (G4) — the empire-taking read. The public routes still pass a save and resolve its
    /// human empire (today's behaviour); this is the form a non-human empire will call.</summary>
    internal RpgActorProgressionDto? ReadEmpireActorUnlocked(SqliteConnection db, EmpireRef owner, string kind, int typeId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT save_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc
            FROM rpg_actor_progression WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
            """;
        cmd.Parameters.AddWithValue("$p", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", typeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ToActorDto(db, r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), ReadXp(r, 4),
            r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetString(8));
    }

    RpgActorProgressionDto? ReadActorDtoUnlocked(SqliteConnection db, long playerId, string kind, int typeId) =>
        ReadEmpireActorUnlocked(db, new EmpireRef(new SaveId(playerId), new EmpireId(HumanEmpireIdUnlocked(db, playerId))), kind, typeId);

    /// <summary>
    /// SE4.21 (G4) — a commander's level for one empire of a save. The commander pool is the
    /// <c>kind='player', type_id=0</c> row (commander-identity); an empire with no row reads level 1,
    /// which is what a fresh empire's commander is. `EP empire-level` owns what feeds it.
    /// </summary>
    internal long CommanderLevelOfUnlocked(SqliteConnection db, EmpireRef owner)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT level FROM rpg_actor_progression
            WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=0;
            """;
        cmd.Parameters.AddWithValue("$p", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$k", RpgActorKinds.Player);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 1L : Convert.ToInt64(value);
    }

    /// <summary>SE4.21 (G4) — `CommanderLevelOf(SaveId, EmpireId)`, the public empire-keyed form.</summary>
    public long CommanderLevelOf(SaveId save, EmpireId empire)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return CommanderLevelOfUnlocked(db, new EmpireRef(save, empire));
        }
    }

    RpgActorProgressionDto ToActorDto(
        SqliteConnection db, long playerId, string kind, int typeId, long level, long xp,
        long highest, long demotion, long revision, string updated)
    {
        var (first, step) = RpgXpCurve.ParamsFor(kind);
        var dto = new RpgActorProgressionDto
        {
            PlayerId = playerId,
            Kind = kind,
            TypeId = typeId,
            TypeName = LookupTypeNameUnlocked(db, kind, typeId),
            DisplayName = LookupDisplayNameUnlocked(db, kind, typeId),
            Level = level,
            Xp = xp,
            XpToNext = RpgXpCurve.XpToNext(kind, level),
            HighestLevel = highest,
            DemotionCount = demotion,
            Revision = revision,
            UpdatedAt = updated,
            CurveFirst = first,
            CurveStep = step
        };
        ApplyAlmanacPromoteUnlocked(db, dto);
        return dto;
    }

    /// <summary>
    /// Promote curated almanac dump fields onto progression actors:
    /// name → displayName, enumName → typeName fallback, info / introduce / cost.
    /// </summary>
    void ApplyAlmanacPromoteUnlocked(SqliteConnection hot, RpgActorProgressionDto dto)
    {
        if (dto.Kind is not (RpgActorKinds.Plant or RpgActorKinds.Zombie)) return;
        var side = dto.Kind == RpgActorKinds.Plant ? "plant" : "zombie";
        using var media = OpenMediaUnlocked();
        var dump = ReadAlmanacDumpUnlocked(media, hot, side, dto.TypeId);
        if (dump?.Fields is not { Count: > 0 } fields) return;

        static string? Field(Dictionary<string, string?> map, string key)
        {
            if (!map.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return null;
            return v.Trim();
        }

        var name = Field(fields, "name") ?? Field(fields, "displayName");
        if (!string.IsNullOrWhiteSpace(name))
            dto.DisplayName = name;

        var enumName = Field(fields, "enumName");
        if (!string.IsNullOrWhiteSpace(enumName))
            dto.TypeName = enumName;

        dto.AlmanacInfo = Field(fields, "info");
        dto.AlmanacIntroduce = Field(fields, "introduce");
        dto.AlmanacCost = Field(fields, "cost");
    }

    string? LookupTypeNameUnlocked(SqliteConnection db, string kind, int typeId)
    {
        if (kind == RpgActorKinds.Player) return "Player";
        var side = kind == RpgActorKinds.Plant ? "plant" : kind == RpgActorKinds.Zombie ? "zombie" : null;
        if (side is null) return null;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT type_name FROM types WHERE side=$s AND type=$t LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", side);
        cmd.Parameters.AddWithValue("$t", typeId);
        return cmd.ExecuteScalar() as string;
    }

    string? LookupDisplayNameUnlocked(SqliteConnection db, string kind, int typeId)
    {
        if (kind == RpgActorKinds.Player) return null;
        var side = kind == RpgActorKinds.Plant ? "plant" : kind == RpgActorKinds.Zombie ? "zombie" : null;
        if (side is null) return null;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT display_name FROM types WHERE side=$s AND type=$t LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", side);
        cmd.Parameters.AddWithValue("$t", typeId);
        return cmd.ExecuteScalar() as string;
    }
}
