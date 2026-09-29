using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>
/// passive-tree-todo.md G5 — D43 / spec-gate-counters.md §16 OQ1's one-time, auditable, stamped seed
/// for an EXISTING save. Orchestration only: the actual proxy math lives in
/// <see cref="ExistingSaveSeed.SeededCount"/> (Core, no DAL dependency); this file's own job is the
/// three things Core cannot do — read `rpg_aptitude_allocation` for the proxy signal, check/write the
/// one-time stamp, and write the seeded `rpg_gate_counter` rows, all inside one transaction.
///
/// <para><b>Deliberately NOT in <see cref="GateCounterBoundaryGuardTests"/>'s scanned file list</b> —
/// unlike the ongoing gate-quantity READ path (§5.1, which D35 keeps out of <c>AllocationScope</c> for
/// good, because entering it there would make a counter a fifth allocation-scope member), this is a
/// one-time MIGRATION that reads the player's own EXISTING primary-tree investment —
/// <c>rpg_aptitude_allocation</c>'s Commander-scope total — as the proxy D43 explicitly calls for
/// ("derived from something already persisted"). Reading an allocation to derive a seed value is not
/// the thing §14 forbids ("write an <c>AptitudeAllocation</c> row FROM a counter") — this file never
/// writes one, only reads one, and only into a completely separate table
/// (<c>rpg_gate_counter</c>/<c>rpg_gate_counter_seed</c>) that no aptitude-share computation ever
/// touches.</para>
///
/// <para><b>Stamped, once, per owner, auditably.</b> <c>rpg_gate_counter_seed</c> is checked and written
/// inside the SAME transaction as the seeded <c>rpg_gate_counter</c> rows, so a crash between the two
/// cannot leave a stamp with no counters or counters with no stamp. A second call for an
/// already-stamped owner is a pure no-op — it does not even re-read the allocation. The stamp also
/// records the exact <c>commander_points_at_seed</c> the proxy was computed from, so a later audit can
/// answer "what was this player's save seeded from" without re-deriving it.</para>
///
/// <para><b>Never clobbers real play.</b> A subject that already has a gate-counter row (organic credit
/// arrived before the seed pass ran, or a prior partial seed) is left untouched — a seed is a STARTING
/// value, never an addition on top of real progress and never a reduction of it. §4.1's sparsity rule
/// applies here too: a subject whose seeded count comes out to zero gets no row at all, so a fresh
/// account (commander total 0) is left with the exact zero-row state it would have had if this pass
/// never ran — "never runs for a new player" made structural rather than a branch to remember.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureGateCounterSeedSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_gate_counter_seed (
              owner_kind               TEXT    NOT NULL,
              owner_key                TEXT    NOT NULL,
              seeded_utc               TEXT    NOT NULL,
              commander_points_at_seed INTEGER NOT NULL,
              PRIMARY KEY (owner_kind, owner_key)
            );
            """);
    }

    /// <param name="ownerKind">"player" today, mirroring every other gate-counter row's <c>owner_kind</c>.</param>
    /// <param name="ownerKey"><c>AptitudeEndpoints.ScopeKey(playerId)</c>'s exact <c>"player:{id}"</c>
    /// shape — it doubles as the Commander allocation's own <c>scope_key</c> with no translation, the
    /// same equivalence <c>GateCounterKey.cs</c>'s own doc comment already states for the live
    /// path.</param>
    /// <param name="statusIds">Every <c>status_applied</c> subject id to seed — the caller's own roster
    /// (<c>StatusCategoryRegistry.Map.Keys</c> at the composition root), never hardcoded here so this
    /// file stays agnostic to that registry's shape — the same delegate/parameter-based decoupling
    /// <see cref="StatusAppliedSource"/> already uses across the DAL boundary.</param>
    /// <param name="elementIds">Every <c>element_mastery</c> subject id to seed (<c>ElementRoster.Concrete</c>
    /// at the composition root).</param>
    /// <param name="tuning">The already-validated <c>gateCounters</c> tuning block — the same one
    /// <see cref="StatusAppliedSource"/>/<see cref="ElementMasterySource"/> read for the live path, so a
    /// seed and a real credit for the same player always agree on which rate and which mastery-curve
    /// pair are in force.</param>
    public GateCounterSeedOutcome SeedExistingSaveGateCounters(
        string ownerKind, string ownerKey,
        IReadOnlyList<string> statusIds, IReadOnlyList<string> elementIds,
        GateCountersTuning tuning)
    {
        if (string.IsNullOrWhiteSpace(ownerKind)) throw new ArgumentException("ownerKind required", nameof(ownerKind));
        if (string.IsNullOrWhiteSpace(ownerKey)) throw new ArgumentException("ownerKey required", nameof(ownerKey));
        if (statusIds is null) throw new ArgumentNullException(nameof(statusIds));
        if (elementIds is null) throw new ArgumentNullException(nameof(elementIds));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            if (ReadGateCounterSeedStampUnlocked(db, ownerKind, ownerKey) is { } stamp)
                return new GateCounterSeedOutcome(AlreadySeeded: true, SeededSubjectCount: 0, CommanderPoints: stamp.CommanderPointsAtSeed);

            // The proxy signal — D43: "derived from something already persisted." Read INSIDE this
            // transaction (matching the shipped RpgStore.ZombossAdaptive.cs precedent for reading an
            // allocation mid-transaction) so the value used to compute every seeded row and the value
            // stamped for audit are the same snapshot, never a value that could move between the two.
            var commanderTotal = LoadAllocationUnlocked(db, AllocationScope.Commander, ownerKey)
                .TotalForScope(AllocationScope.Commander);

            var seededSubjectCount = 0;
            foreach (var statusId in statusIds)
            {
                var seeded = ExistingSaveSeed.SeededCount(commanderTotal, tuning.StatusMasteryRatePoints, tuning);
                if (SeedOneUnlocked(db, tx, ownerKind, ownerKey, StatusAppliedCounter.Quantity, statusId, seeded))
                    seededSubjectCount++;
            }
            foreach (var elementId in elementIds)
            {
                var seeded = ExistingSaveSeed.SeededCount(commanderTotal, tuning.ElementMasteryRatePoints, tuning);
                if (SeedOneUnlocked(db, tx, ownerKind, ownerKey, ElementMasteryCounter.Quantity, elementId, seeded))
                    seededSubjectCount++;
            }

            ExecIn(db, tx, """
                INSERT INTO rpg_gate_counter_seed (owner_kind, owner_key, seeded_utc, commander_points_at_seed)
                VALUES ($ok, $key, $t, $c);
                """,
                ("$ok", ownerKind), ("$key", ownerKey), ("$t", ServerClock.UtcNowDateTime.ToString("o")), ("$c", commanderTotal));

            tx.Commit();
            return new GateCounterSeedOutcome(AlreadySeeded: false, SeededSubjectCount: seededSubjectCount, CommanderPoints: commanderTotal);
        }
    }

    /// <summary>One subject's seed write — INSERT only, and only when no row exists yet for this exact
    /// key. `ON CONFLICT ... DO NOTHING` is belt-and-suspenders around the same "never clobber" contract
    /// the preceding existence check already gives; both exist so a future caller that skips the
    /// existence check (e.g. a batched rewrite) still cannot overwrite a real row by accident. Never
    /// writes a row for a zero seed (§4.1's sparsity). Returns whether a row was actually written.</summary>
    bool SeedOneUnlocked(SqliteConnection db, SqliteTransaction tx, string ownerKind, string ownerKey,
        string quantity, string subjectId, long seededCount)
    {
        if (seededCount <= 0) return false;

        var key = new GateCounterKey(ownerKind, ownerKey, quantity, subjectId);
        if (ReadGateCounterUnlocked(db, tx, key) != 0) return false; // never clobber real progress

        ExecIn(db, tx, """
            INSERT INTO rpg_gate_counter (owner_kind, owner_key, quantity, subject_id, count)
            VALUES ($ok, $key, $q, $s, $c)
            ON CONFLICT(owner_kind, owner_key, quantity, subject_id) DO NOTHING;
            """,
            ("$ok", ownerKind), ("$key", ownerKey), ("$q", quantity), ("$s", subjectId), ("$c", seededCount));
        return true;
    }

    (string SeededUtc, long CommanderPointsAtSeed)? ReadGateCounterSeedStampUnlocked(
        SqliteConnection db, string ownerKind, string ownerKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT seeded_utc, commander_points_at_seed FROM rpg_gate_counter_seed
            WHERE owner_kind = $ok AND owner_key = $key;
            """;
        cmd.Parameters.AddWithValue("$ok", ownerKind);
        cmd.Parameters.AddWithValue("$key", ownerKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    /// <summary>Whether this owner has already been seeded — exposed for a caller (a startup/login hook,
    /// a health endpoint, or a test) that needs to know without re-running the seed itself.</summary>
    public bool HasSeededGateCounters(string ownerKind, string ownerKey)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadGateCounterSeedStampUnlocked(db, ownerKind, ownerKey) is not null;
        }
    }
}

/// <summary>The result of one <see cref="RpgStore.SeedExistingSaveGateCounters"/> call — auditable:
/// <see cref="CommanderPoints"/> is the exact proxy value the seed was (or, on a re-run, WAS) computed
/// from, and <see cref="AlreadySeeded"/> distinguishes "this call did nothing because it already ran"
/// from "this call ran and seeded N subjects."</summary>
public readonly record struct GateCounterSeedOutcome(bool AlreadySeeded, int SeededSubjectCount, long CommanderPoints);
