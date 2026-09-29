using System.Text.Json;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Mutation;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// The mutation head as the store holds it — the five columns this module adds to
/// <c>effect_instance</c>.
/// </summary>
public sealed record InstanceMutationHead(
    string InstanceId, int EnhanceLevel, int PityCounter, int MutationSeq, string? StateHash, string? OriginValuesJson);

/// <summary>What an append did. <c>Replayed</c> is clause 8's idempotent retry.</summary>
public sealed record MutationAppendResult(bool Ok, bool Replayed, int Seq, string Reason);

/// <summary>
/// <c>effect_instance_op</c> — the mutation ledger (D2 §9 clause 2), plus the five head columns and
/// <c>effect_instance_atom.suppressed</c> (clause 9). The only schema module 15 owns.
///
/// <para>⚠ <c>origin_catalog_revision</c> is NOT a new column: it already exists as
/// <c>effect_instance.catalog_revision</c>, and D2 §7.1 granted it as a <b>semantic lock</b> —
/// origin-only, no operation rewrites it. I6 §5.1's request for a new column was refused.</para>
/// </summary>
/// <summary>One head-field pair as the store holds it — both nullable, null meaning "not yet
/// derived" (species-gear-chain T10/T11). A present max with a null current backfills current = max
/// on read; a null max derives both.</summary>
public sealed record InstanceHeadPair(long? Max, long? Current);

public sealed partial class RpgStore
{
    /// <summary>Raw read of the potential pair, no derivation. Null pair = never derived.</summary>
    public InstanceHeadPair GetPotential(string instanceId) =>
        GetHeadPair(instanceId, "craft_potential_max", "craft_potential_current");

    /// <summary>Raw read of the durability pair, no derivation. Null pair = never derived.</summary>
    public InstanceHeadPair GetDurability(string instanceId) =>
        GetHeadPair(instanceId, "durability_max", "durability_current");

    public void SetPotential(string instanceId, long max, long current) =>
        SetHeadPair(instanceId, "craft_potential_max", "craft_potential_current", max, current);

    public void SetDurability(string instanceId, long max, long current) =>
        SetHeadPair(instanceId, "durability_max", "durability_current", max, current);

    /// <summary>
    /// species-gear-chain T23 — a repair attempt that destroyed the item, on the caller's connection so
    /// it commits with the attempt. Deliberately the SAME update a voluntary salvage runs
    /// (`TrySalvageItem`): same table, same `revision + 1`, same closed vocabulary's fourth value. The
    /// spec's "deleted via the existing salvage path" is this write — a disposition, never a row
    /// deletion (the `rpg_item` row keeps the audit trail, and nothing is orphaned).
    /// </summary>
    internal void MarkItemDestroyedUnlocked(SqliteConnection db, string playerKey, string instanceId)
    {
        ExecOn(db, """
            UPDATE rpg_item SET disposition = 'destroyed', revision = revision + 1
            WHERE instance_id = $id AND player_id = $p AND disposition = 'owned';
            """,
            ("$id", instanceId), ("$p", playerKey));
    }

    /// <summary>
    /// species-gear-chain T24 — the durability decrement on a connection the CALLER owns, so craft wear
    /// commits in the same transaction as the craft that caused it (the `AppendMutationOpUnlocked`
    /// convention). Only <c>durability_current</c> moves: <c>max</c> is derived once (T11) and repair —
    /// not crafting — is what the ideal's D-rules allow to touch anything else.
    /// </summary>
    /// <summary>
    /// species-gear-chain T61 — the craft-potential decrement on a connection the CALLER owns, so the spend
    /// commits in the same transaction as the op that spent it (the `SetDurabilityCurrentUnlocked` shape
    /// beside it). Only <c>craft_potential_current</c> moves: <c>max</c> is derived once and the authored
    /// override is the only thing that can raise it.
    /// </summary>
    internal void SetPotentialCurrentUnlocked(SqliteConnection db, string instanceId, long current)
    {
        if (current < 0)
            throw new ArgumentOutOfRangeException(nameof(current), current,
                "craft potential floors at zero — a craft never takes an item below it (spec-craft-risk-ladder §Design 2)");
        ExecOn(db, "UPDATE effect_instance SET craft_potential_current = $c WHERE instance_id = $id;",
            ("$c", current), ("$id", instanceId));
    }

    internal void SetDurabilityCurrentUnlocked(SqliteConnection db, string instanceId, long current)
    {
        if (current < 0)
            throw new ArgumentOutOfRangeException(nameof(current), current,
                "durability floors at zero — wear never takes an item below it (spec-craft-risk-ladder §Design 3)");
        ExecOn(db, "UPDATE effect_instance SET durability_current = $c WHERE instance_id = $id;",
            ("$c", current), ("$id", instanceId));
    }

    /// <summary>Derive-and-backfill on first read (spec-craft-risk-ladder.md §Design 1, module 7 §1):
    /// a pre-existing instance's NULL columns derive via <paramref name="deriveMax"/> and persist as
    /// max/current = max — never mistaken for exhausted. T24 wires the real callers; until then this
    /// is the seam they call, unit-tested here.</summary>
    public InstanceHeadPair GetOrDerivePotential(string instanceId, Func<long> deriveMax) =>
        GetOrDerive(instanceId, "craft_potential_max", "craft_potential_current", deriveMax);

    /// <summary>Same seam for durability (module 7 §1, pulled forward as T11).</summary>
    public InstanceHeadPair GetOrDeriveDurability(string instanceId, Func<long> deriveMax) =>
        GetOrDerive(instanceId, "durability_max", "durability_current", deriveMax);

    InstanceHeadPair GetHeadPair(string instanceId, string maxCol, string currentCol)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT {maxCol}, {currentCol} FROM effect_instance WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$id", instanceId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new InstanceHeadPair(null, null);
            return new InstanceHeadPair(
                r.IsDBNull(0) ? null : r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt64(1));
        }
    }

    void SetHeadPair(string instanceId, string maxCol, string currentCol, long max, long current)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"UPDATE effect_instance SET {maxCol} = $max, {currentCol} = $current WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$max", max);
            cmd.Parameters.AddWithValue("$current", current);
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.ExecuteNonQuery();
        }
    }

    InstanceHeadPair GetOrDerive(string instanceId, string maxCol, string currentCol, Func<long> deriveMax)
    {
        var pair = GetHeadPair(instanceId, maxCol, currentCol);
        var max = pair.Max ?? deriveMax();
        var current = pair.Current ?? max;
        if (pair.Max is null || pair.Current is null)
            SetHeadPair(instanceId, maxCol, currentCol, max, current);
        return new InstanceHeadPair(max, current);
    }
    void EnsureInstanceOpSchemaUnlocked(SqliteConnection db)
    {
        // The head columns. CREATE TABLE IF NOT EXISTS is a no-op against a database created before
        // this module, so the additions have to be explicit -- the same migration shape T3.4 used.
        EnsureColumn(db, "effect_instance", "enhance_level", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "effect_instance", "enhance_pity_counter", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "effect_instance", "mutation_seq", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "effect_instance", "state_hash", "TEXT");
        // D2 rung 1', written LAZILY at first mutation (D2 §11.3's lean): an item that is never
        // mutated never pays for a second copy of its own numbers.
        EnsureColumn(db, "effect_instance", "origin_values_json", "TEXT");
        // species-gear-chain T10 (craft-risk-ladder Stage 1) + T11 (durability-slice a): the two
        // head-field pairs beside the mutation head. NULL is the correct "not yet derived" state
        // for a pre-existing instance — never a fabricated 0 (which reads as exhausted/broken) and
        // never a fabricated full value. Non-equipment instances never populate these columns: the
        // callers that derive (T24's consumption wiring) only ever read rolled equipment rows.
        EnsureColumn(db, "effect_instance", "craft_potential_max", "INTEGER");
        EnsureColumn(db, "effect_instance", "craft_potential_current", "INTEGER");
        EnsureColumn(db, "effect_instance", "durability_max", "INTEGER");
        EnsureColumn(db, "effect_instance", "durability_current", "INTEGER");
        // D2 clause 9 -- an identity change is suppress-then-append. The row stays, seq is never
        // renumbered and no op row is ever deleted.
        EnsureColumn(db, "effect_instance_atom", "suppressed", "INTEGER NOT NULL DEFAULT 0");

        Exec(db, """
            -- D2 §9 clause 2: the ledger. UNIQUE(instance_id, correlation_id) is the second net
            -- under the caller's own check, exactly as rpg_material_spend_log is under the recipe
            -- gate: a race that slips past the read still cannot double-apply an operation.
            CREATE TABLE IF NOT EXISTS effect_instance_op (
              instance_id    TEXT NOT NULL,
              op_seq         INTEGER NOT NULL,
              op_kind        TEXT NOT NULL,
              correlation_id TEXT NOT NULL,
              op_seed        INTEGER NOT NULL,
              result_json    TEXT NOT NULL,
              applied_utc    TEXT NOT NULL,
              -- D2 clause 5: the op stamps its OWN catalog revision and rules version. Neither is
              -- effect_instance.catalog_revision, which is origin-only and which no operation rewrites.
              catalog_revision INTEGER NOT NULL DEFAULT 0,
              rules_version    INTEGER NOT NULL DEFAULT 0,
              -- D2 clause 11: the spend, in module 14's material vocabulary. "A spent cost with no op
              -- is theft; an op with no cost is duplication."
              cost_json        TEXT NOT NULL DEFAULT '{}',
              PRIMARY KEY (instance_id, op_seq),
              FOREIGN KEY (instance_id) REFERENCES effect_instance(instance_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_effect_instance_op_correlation
              ON effect_instance_op(instance_id, correlation_id);
            """);
    }

    public InstanceMutationHead? GetInstanceMutationHead(string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT enhance_level, enhance_pity_counter, mutation_seq, state_hash, origin_values_json
                FROM effect_instance WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new InstanceMutationHead(
                instanceId,
                r.GetInt32(0), r.GetInt32(1), r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4));
        }
    }

    /// <summary>
    /// Append one op and rewrite the head, <b>in one transaction</b> (the Boundaries: "commit op row,
    /// material debit and head rewrite in one transaction" — the debit is the caller's
    /// <see cref="TrySpendRecipe"/>, which runs its own `perform` delegate inside its own).
    ///
    /// <para>Clause 8 — a replayed <c>correlation_id</c> returns the RECORDED result rather than
    /// applying anything; a reused one carrying different parameters is refused, never silently
    /// applied.</para>
    /// </summary>
    public MutationAppendResult AppendMutationOp(
        string instanceId, MutationOpKind kind, string correlationId, long opSeed,
        MutationResult result, string? newStateHash, string? originValuesJson, string appliedUtc,
        long catalogRevision = 0, int rulesVersion = 0, string costJson = "{}")
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var appended = AppendMutationOpUnlocked(db, instanceId, kind, correlationId, opSeed, result,
                newStateHash, originValuesJson, appliedUtc, catalogRevision, rulesVersion, costJson);
            // A refusal writes nothing, so a retried refusal re-evaluates -- the shipped
            // TrySpendSouls contract, kept here too. A replay wrote nothing either.
            if (appended.Ok && !appended.Replayed) tx.Commit();
            return appended;
        }
    }

    /// <summary>
    /// The body of <see cref="AppendMutationOp"/> on a connection the CALLER owns, so the op row, the
    /// head rewrite and a module-14 material debit can commit together —
    /// `spec-enhance-reroll.md`'s Boundaries: <i>"commit op row, material debit and head rewrite in
    /// one transaction"</i>. Every command comes from <c>db.CreateCommand()</c>, which inherits the
    /// connection's active transaction, so this participates in the caller's rather than opening a
    /// second connection against a database its own transaction already has open.
    ///
    /// <para><b>Internal on purpose:</b> the transaction is the caller's to commit, and the only
    /// caller is <see cref="TrySpendAndApply"/> in this same project.</para>
    /// </summary>
    internal MutationAppendResult AppendMutationOpUnlocked(
        SqliteConnection db,
        string instanceId, MutationOpKind kind, string correlationId, long opSeed,
        MutationResult result, string? newStateHash, string? originValuesJson, string appliedUtc,
        long catalogRevision = 0, int rulesVersion = 0, string costJson = "{}")
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("a mutation op needs a correlation id — it is what makes a retry idempotent", nameof(correlationId));

        var resultJson = MutationCanonical.WriteResult(result);
        var kindId = MutationOpKinds.Id(kind);

        using (var existing = db.CreateCommand())
        {
            existing.CommandText = """
                SELECT op_seq, op_kind, result_json FROM effect_instance_op
                WHERE instance_id = $id AND correlation_id = $cid;
                """;
            existing.Parameters.AddWithValue("$id", instanceId);
            existing.Parameters.AddWithValue("$cid", correlationId);
            using var r = existing.ExecuteReader();
            if (r.Read())
            {
                var seq = r.GetInt32(0);
                var sameKind = string.Equals(r.GetString(1), kindId, StringComparison.Ordinal);
                var sameResult = string.Equals(r.GetString(2), resultJson, StringComparison.Ordinal);
                return sameKind && sameResult
                    ? new MutationAppendResult(true, Replayed: true, seq, "replay")
                    : new MutationAppendResult(false, Replayed: false, seq,
                        $"correlation '{correlationId}' was already applied to '{instanceId}' with different parameters — refused, never silently applied");
            }
        }

        int nextSeq;
        using (var head = db.CreateCommand())
        {
            head.CommandText = "SELECT mutation_seq, enhance_level FROM effect_instance WHERE instance_id = $id;";
            head.Parameters.AddWithValue("$id", instanceId);
            using var r = head.ExecuteReader();
            if (!r.Read())
                return new MutationAppendResult(false, false, 0, $"no effect_instance '{instanceId}'");
            nextSeq = r.GetInt32(0) + 1;
            var level = r.GetInt32(1) + result.EnhanceLevelDelta;
            if (level < 0)
                return new MutationAppendResult(false, false, nextSeq,
                    "the op takes the enhancement level below zero");
        }

        // The one legal ceiling in the module, and it THROWS -- an absolute bound derived from
        // the arithmetic, never a silent clamp (AGENTS.md). It bounds a retry loop and a log's
        // length, not how strong an item may become.
        if (nextSeq > MutationLimits.MutationSeqCap)
            throw new OverflowException(
                $"instance '{instanceId}' would reach mutation_seq {nextSeq}, past the structural cap of " +
                $"{MutationLimits.MutationSeqCap}. This is a retry-loop bound, not a design ceiling — it refuses rather than wrapping");

        // species-gear-chain T13: appended milestone atoms are allocated here, BEFORE the op row is
        // written — the recorded result carries the same seqs the rows will have, so the ledger
        // never disagrees with the table. Allocation is max+1 per instance in this transaction
        // (never reused, never renumbered); the workbench's incoming Seq is an explicit placeholder
        // and is always replaced, because allocation is the store's job, never the caller's.
        var recorded = result;
        if (result.Appended.Count > 0)
        {
            // One MAX query, then dense allocation in memory: the rows land below in the same
            // transaction, so max+1+i is exact — a second query per append would re-read the same
            // max and collide.
            var baseSeq = NextAtomSeq(db, instanceId);
            var allocated = new List<AtomAppend>(result.Appended.Count);
            for (var i = 0; i < result.Appended.Count; i++)
                allocated.Add(result.Appended[i] with { Seq = baseSeq + i });
            recorded = result with { Appended = allocated };
            resultJson = MutationCanonical.WriteResult(recorded);
        }

        ExecOn(db, """
            INSERT INTO effect_instance_op
              (instance_id, op_seq, op_kind, correlation_id, op_seed, result_json, applied_utc,
               catalog_revision, rules_version, cost_json)
            VALUES ($id, $seq, $kind, $cid, $seed, $json, $utc, $rev, $rules, $cost);
            """,
            ("$id", instanceId), ("$seq", nextSeq), ("$kind", kindId), ("$cid", correlationId),
            ("$seed", opSeed), ("$json", resultJson), ("$utc", appliedUtc),
            ("$rev", catalogRevision), ("$rules", rulesVersion), ("$cost", costJson));

        ExecOn(db, """
            UPDATE effect_instance
            SET mutation_seq = $seq,
                enhance_level = enhance_level + $delta,
                state_hash = $hash,
                origin_values_json = COALESCE(origin_values_json, $origin)
            WHERE instance_id = $id;
            """,
            ("$seq", nextSeq), ("$delta", result.EnhanceLevelDelta), ("$hash", (object?)newStateHash ?? DBNull.Value),
            ("$origin", (object?)originValuesJson ?? DBNull.Value), ("$id", instanceId));

        foreach (var seq in result.Suppressed)
            ExecOn(db, "UPDATE effect_instance_atom SET suppressed = 1 WHERE instance_id = $id AND seq = $seq;",
                ("$id", instanceId), ("$seq", seq));

        // The rows for the seqs allocated above — same transaction, same seqs, same values. Power
        // and identity are NULL: an append carries its values only, and inventing either would be
        // fabrication, not derivation.
        foreach (var append in recorded.Appended)
            ExecOn(db,
                "INSERT INTO effect_instance_atom (instance_id, seq, atom_id, values_json) " +
                "VALUES ($id, $seq, $atom, $vals);",
                ("$id", instanceId), ("$seq", append.Seq), ("$atom", append.AtomId),
                ("$vals", CanonicalValuesJson(append.Values)));

        return new MutationAppendResult(true, Replayed: false, nextSeq, "");
    }

    /// <summary>Next atom seq for an instance — max+1 in the caller's transaction (T13).</summary>
    static int NextAtomSeq(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), -1) + 1 FROM effect_instance_atom WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>values_json for an appended row — sorted keys, mirroring
    /// <c>MutationCanonical.WriteResult</c>'s own values shape so the row and the ledger agree.</summary>
    static string CanonicalValuesJson(IReadOnlyDictionary<string, long> values)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            foreach (var (k, v) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                w.WriteNumber(k, v);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Set the pity counter. Separate from the append because a failed attempt moves the
    /// counter without appending a value delta, and a guarantee resets it.</summary>
    public void SetInstancePityCounter(string instanceId, int counter)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            SetInstancePityCounterUnlocked(db, instanceId, counter);
            tx.Commit();
        }
    }

    /// <summary>Same write on the caller's connection — see <see cref="AppendMutationOpUnlocked"/>.</summary>
    internal void SetInstancePityCounterUnlocked(SqliteConnection db, string instanceId, int counter)
    {
        if (counter < 0) throw new ArgumentOutOfRangeException(nameof(counter), counter, "a pity counter cannot be negative");
        ExecOn(db, "UPDATE effect_instance SET enhance_pity_counter = $c WHERE instance_id = $id;",
            ("$c", counter), ("$id", instanceId));
    }

    /// <summary>The transcript, dense and in order.</summary>
    public IReadOnlyList<MutationOp> ReadMutationOps(string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT op_seq, op_kind, correlation_id, op_seed, result_json, applied_utc,
                       catalog_revision, rules_version, cost_json
                FROM effect_instance_op WHERE instance_id = $id ORDER BY op_seq;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            using var r = cmd.ExecuteReader();

            var ops = new List<MutationOp>();
            while (r.Read())
            {
                if (!MutationOpKinds.TryParse(r.GetString(1), out var kind))
                    throw new InvalidOperationException(
                        $"instance '{instanceId}' op {r.GetInt32(0)} carries op_kind '{r.GetString(1)}', which is not in the closed namespace");
                ops.Add(new MutationOp(instanceId, r.GetInt32(0), kind, r.GetString(2), r.GetInt64(3),
                    MutationCanonical.ReadResult(r.GetString(4)), r.GetString(5),
                    r.GetInt64(6), r.GetInt32(7), r.GetString(8)));
            }

            return ops;
        }
    }

    /// <summary>
    /// Seed <c>reroll_cost_mult</c>'s rung leg for every rung. Deliberately its own method rather
    /// than folded into <see cref="SeedRarityLadder"/>, so module 7's seeding never grows a
    /// dependency on a later module's tuning file — the precedent module 14 set with
    /// <c>SeedSalvageYield</c>. Idempotent: safe on every boot.
    /// </summary>
    public void SeedRerollCostMult(EnhancementTuning tuning)
    {
        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            SetRarityBudget(RarityLadder.RungIds[i], "reroll_cost_mult", RerollPolicy.RungLegMilli(i, tuning));
    }
}
