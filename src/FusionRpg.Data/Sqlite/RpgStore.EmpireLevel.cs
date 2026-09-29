using FusionRpg.Core.Battle;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>
/// `empire-level` (module 13, `empire-progression` Wave D, ruling R19; spec:
/// `docs/architecture/empire-progression/spec-empire-level.md`). An empire's level is a
/// <c>kind = 'empire'</c> row of <c>rpg_actor_progression</c> keyed `(SaveId, EmpireId)`, fed ONLY by
/// that empire's own species reaching a NEW highest level. This file is the feed; the row, the ledger,
/// the dedupe, the revision counter and the broadcast are all the machinery that already exists for
/// every other progression kind (EP4.1 added the kind and the Core vocabulary).
///
/// <para><b>One place covers both species XP paths.</b> Both of them — the per-placement award and the
/// run-completion award — go through <c>RpgStore.Progression.cs</c>'s <c>TryApplyXpUnlocked</c>, so the
/// hook lives there, in the one function they share. A third path added later is covered with no change
/// here.</para>
///
/// <para><b>Once per `(species, level)`, ever — keyed on <c>highest_level</c>, not on the ledger.</b>
/// The credit pays exactly the levels in <c>(highestBefore, highestAfter]</c>, both read from the
/// species row's own <c>HighestLevel</c>. A species that demotes from 5 to 4 and climbs back to 5 never
/// raises <c>HighestLevel</c> past 5, so it credits nothing the second time. This is deliberately NOT
/// the ledger's dedupe alone: <c>rpg_xp_ledger</c> is tail-trimmed per actor by compaction
/// (`TrimXpTailsCore`), and the empire row is one actor that gains a row per species level, so its
/// OLDEST `sp:{id}:L{n}` keys are exactly the ones compaction removes — after which a dedupe-only rule
/// would pay a demote-and-reclimb a second time. <c>highest_level</c> lives on the species row and is
/// never trimmed. The dedupe key remains as the in-transaction replay guard and to keep the ledger
/// readable (`sp:{typeId}:L{level}` says which species level paid for which empire level).</para>
///
/// <para><b>Nothing is registered in <c>ProgressionPipeline</c>.</b> That pipeline runs BEFORE the
/// ledger's `INSERT OR IGNORE`, so a handler would fire again on a replayed fact. This credit is
/// written after the species ledger row is known to have landed, in the same connection and
/// transaction, as <c>ClaimResolver</c>-style post-insert work.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>
    /// One empire level crossing, as the host needs it AFTER the transaction commits: which empire, the
    /// levels it moved between, and what that pays. Queued on the <see cref="RpgProgressionDirty"/> the
    /// empire credit returns — the same post-commit envelope every other progression write uses, which is
    /// what makes "a rolled-back transaction broadcasts nothing" structural rather than a promise.
    ///
    /// <para><b>No <c>freeRespecStock</c> here.</b> That is `respec-free-counter`'s read (EP4.5's ledger),
    /// so the broadcaster adds it when it renders the event (EP4.7), never this queue.</para>
    /// </summary>
    public readonly record struct EmpireLevelUpEvent(
        long PlayerId, string EmpireId, long LevelBefore, long LevelAfter,
        IReadOnlyList<EmpireLevelGrant> Grants);

    /// <summary>
    /// The R1 side rule, in one expression: a species credits its own empire's level only when the
    /// species' SIDE maps to that empire through the one existing mapping,
    /// <see cref="KillAttribution.EmpireOf(string, bool)"/>. Ruling R1 gives zombie species to Zomboss's
    /// empire and plant species to the player's, so the human's pre-R1 zombie species rows — which
    /// `ai-empire-species` leaves as history on the human empire — never raise the human's empire level,
    /// live or by backfill, and the order in which `empire-level` and `ai-empire-species` land stops
    /// mattering. A second copy of "zombies are Zomboss's" is exactly the parallel-path defect the
    /// species-progression program exists to remove, so this reads the SSOT rather than switching.
    /// </summary>
    static bool SpeciesCreditsOwner(EmpireRef owner, string? side) =>
        side is not null
        && string.Equals(KillAttribution.EmpireOf(side).Value, owner.Empire.Value, StringComparison.Ordinal);

    /// <summary>
    /// Which side a creature-species row belongs to, from the roster itself rather than from arithmetic
    /// on the id space: `CreatureTypeIdFor`'s plant offset is a constructor convention, not an
    /// invariant anything validates, so reading <see cref="FusionRpg.Core.Creatures.CreatureSpeciesDef.Side"/>
    /// is the honest source. `null` for a type id the roster does not carry (a PvZ almanac type id, which
    /// never reaches this hook because the hook is gated on `kind == Species`) or for an unconfigured
    /// catalog — and no side means no empire to credit.
    /// </summary>
    static string? SpeciesSideOf(int speciesTypeId)
    {
        if (!FusionRpg.Core.Creatures.CreatureSpeciesCatalog.IsConfigured) return null;
        foreach (var def in FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All)
            if (def.CreatureTypeId == speciesTypeId) return def.Side;
        return null;
    }

    /// <summary>
    /// Credits <paramref name="owner"/>'s empire for every species level in
    /// <c>(highestBefore, highestAfter]</c>. Called from `TryApplyXpUnlocked` AFTER the species row
    /// update has landed, so a replayed species award (dropped by the species ledger's own
    /// `INSERT OR IGNORE`, which returns before the row update) never reaches it.
    ///
    /// <para>Each crossed level is one nested `TryApplyXpUnlocked` call with
    /// <c>kind = Empire</c>, <c>delta = RpgXpAwards.SpeciesLevelUp</c> and the dedupe key
    /// `sp:{speciesTypeId}:L{level}` — the same write path, the same ledger, the same revision counter
    /// as any other kind. The nested call cannot recurse: the credit is gated on `kind == Species`.</para>
    /// </summary>
    void CreditEmpireForSpeciesLevelsUnlocked(
        SqliteConnection db, EmpireRef owner, int speciesTypeId, string? side,
        long highestBefore, long highestAfter, long runId, string t, long? factId,
        List<RpgProgressionDirty> sideEffects)
    {
        if (highestAfter <= highestBefore) return;
        if (!SpeciesCreditsOwner(owner, side)) return;

        for (var level = highestBefore + 1; level <= highestAfter; level++)
        {
            var d = TryApplyXpUnlocked(
                db, owner, RpgActorKinds.Empire, 0, runId, t,
                delta: RpgXpAwards.SpeciesLevelUp,
                reason: RpgXpReasons.EmpireSpeciesLevelUp,
                dedupeKey: $"sp:{speciesTypeId}:L{level}",
                factId, payloadJson: null);
            if (d is { } item) sideEffects.Add(item);
        }
    }

    /// <summary>
    /// The level crossings an empire write produced, as queued events. Every event is a `Direction ==
    /// "up"` change: the empire row is only ever credited with a POSITIVE delta, so it never demotes, and
    /// a grant is never taken back.
    /// </summary>
    IReadOnlyList<EmpireLevelUpEvent>? LevelUpEventsFor(
        long playerId, EmpireRef owner, IReadOnlyList<LevelChangeEvent> changes)
    {
        List<EmpireLevelUpEvent>? events = null;
        foreach (var change in changes)
        {
            if (!string.Equals(change.Direction, "up", StringComparison.Ordinal)) continue;
            events ??= new List<EmpireLevelUpEvent>();
            // The grant amount is the host's wired `EmpireLevelTuning` (EP4.4), built once from
            // `species-build.v{n}.json`'s `freeRespecsPerEmpireLevel`. Reading it here rather than from
            // a store field keeps the number read once, at start, by the layer that owns tuning.
            events.Add(new EmpireLevelUpEvent(
                playerId, owner.Empire.Value, change.LevelBefore, change.LevelAfter,
                EmpireLevelGrants.For(change.LevelAfter, EmpireLevelTuningHub.Tuning)));
        }
        return events;
    }
    /// <summary>
    /// `empire-level` EP4.6 (spec-empire-level.md §"Existing saves"): the catch-up pass that runs at
    /// STORE START, before the host serves a request, for every empire that has species rows but no
    /// `kind = 'empire'` row yet — a save that already has species levels would otherwise open at empire
    /// level 1 while its species sit at 30.
    ///
    /// <para><b>It credits through the same call live play uses</b>
    /// (<see cref="CreditEmpireForSpeciesLevelsUnlocked"/>) with `highestBefore = 1` and
    /// `highestAfter = highest_level`, so the R1 side rule, the curve, the ledger's dedupe keys and the
    /// grants are the SAME ones, level for level. `highest_level` is the right input rather than `level`:
    /// a demoted species still earned its highest level once, and live play would have credited it.</para>
    ///
    /// <para><b>Idempotent by construction, not by the ledger.</b> The first credit creates the empire
    /// row, so once an empire's transaction has committed the "no `kind = 'empire'` row" test is false and
    /// the pass never runs there again — even after compaction has trimmed the ledger. That is why this
    /// pass keys off the ROW and not off the `sp:*` dedupe keys, which compaction removes.</para>
    ///
    /// <para><b>One transaction per empire</b>, the spec's own shape: a crash or a throw mid-pass rolls
    /// that empire's whole transaction back, leaving no empire row at all, and the next start runs it
    /// again from a clean read. It cannot collide with live credits either, because it runs before any
    /// request and every later credit pays only levels above the species' `highest_level`, which the pass
    /// has already covered.</para>
    ///
    /// <para>An empire whose species are all at level 1 gets no work and no row: there is nothing to
    /// catch up, and its first live level-up creates the row. An EMPTY store has no candidate rows at all,
    /// so this never touches the progression tuning on a database that has never been played.</para>
    /// </summary>
    internal void BackfillEmpireLevelsUnlocked(SqliteConnection db)
    {
        List<(long Save, string Empire)> candidates = new();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT save_id, empire_id FROM rpg_actor_progression
                WHERE kind = 'species' AND highest_level > 1
                EXCEPT
                SELECT save_id, empire_id FROM rpg_actor_progression WHERE kind = 'empire';
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) candidates.Add((r.GetInt64(0), r.GetString(1)));
        }

        foreach (var (saveId, empireId) in candidates)
        {
            var owner = new EmpireRef(new SaveId(saveId), new EmpireId(empireId));
            var species = new List<(int TypeId, long Highest)>();
            using (var read = db.CreateCommand())
            {
                read.CommandText = """
                    SELECT type_id, highest_level FROM rpg_actor_progression
                    WHERE save_id = $s AND empire_id = $e AND kind = 'species' ORDER BY type_id;
                    """;
                read.Parameters.AddWithValue("$s", saveId);
                read.Parameters.AddWithValue("$e", empireId);
                using var r = read.ExecuteReader();
                while (r.Read()) species.Add((r.GetInt32(0), r.GetInt64(1)));
            }

            var t = ServerClock.UtcNowDateTime.ToString("o");
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                foreach (var (typeId, highest) in species)
                    CreditEmpireForSpeciesLevelsUnlocked(
                        db, owner, typeId, SpeciesSideOf(typeId), highestBefore: 1, highestAfter: highest,
                        runId: 0, t, factId: null, new List<RpgProgressionDirty>());
                Exec(db, "COMMIT;");
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                throw;
            }
        }
    }

}
