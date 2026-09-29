using FusionRpg.Core.PassiveTree.GateCounters;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// spec-gate-counters.md §4 — the ONLY table `status_applied`/`element_mastery` persist to. A partial
/// slice on <see cref="RpgStore"/>, sharing the one connection, the one <c>_gate</c> lock and the one
/// <c>EnsureHotSchema</c>/<c>Reset()</c> dispatch, the same convention <c>RpgStore.Aptitudes.cs</c>
/// documents and <c>RpgStore.PassiveTree.cs</c> already follows for this program's other tables — a
/// standalone class here "would fork that pipeline and silently drop out of <c>Reset()</c>."
///
/// <para><b>Raw counts only, sparse, uncapped</b> (§4.1). Never the index, never the equivalents — a
/// stored derived value would be a second SSOT that goes stale the moment `c` (the mastery curve
/// tunable) moves, the exact reason <c>RpgStore.Aptitudes.cs</c> already gives for storing aptitude
/// points as inputs rather than resolved channel values.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureGateCounterSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_gate_counter (
              owner_kind TEXT    NOT NULL,
              owner_key  TEXT    NOT NULL,
              quantity   TEXT    NOT NULL,
              subject_id TEXT    NOT NULL,
              count      INTEGER NOT NULL,
              PRIMARY KEY (owner_kind, owner_key, quantity, subject_id)
            );
            """);
    }

    /// <summary>
    /// §4.3's flush: every pending delta applied in ONE transaction — an `INSERT` for a key with no
    /// row yet (§4.1's sparsity: no row until the first credit), an additive `UPDATE` otherwise. The
    /// new total is computed in C# under `checked` BEFORE it reaches SQL, never as a SQL-side
    /// `count + $delta` — SQLite silently promotes an overflowing INTEGER to REAL rather than
    /// throwing, which is exactly the silent-wrap docs/architecture/numeric-types.md's numeric-overflow rule forbids for a
    /// magnitude; reading the current value first and adding under `checked` in .NET is what makes
    /// overflow throw here instead.
    /// </summary>
    public void FlushGateCounters(IReadOnlyDictionary<GateCounterKey, long> deltas)
    {
        if (deltas is null) throw new ArgumentNullException(nameof(deltas));
        if (deltas.Count == 0) return;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            foreach (var (key, delta) in deltas)
            {
                if (delta == 0) continue;
                if (delta < 0)
                    throw new ArgumentException(
                        $"gate counter delta must be non-negative -- got {delta} for " +
                        $"{key.OwnerKind}/{key.OwnerKey}/{key.Quantity}/{key.SubjectId}. A counter only " +
                        "ever credits (spec-gate-counters.md §2); there is no decrement path.",
                        nameof(deltas));

                var current = ReadGateCounterUnlocked(db, tx, key);
                long updated;
                checked { updated = current + delta; }

                ExecIn(db, tx, """
                    INSERT INTO rpg_gate_counter (owner_kind, owner_key, quantity, subject_id, count)
                    VALUES ($ok, $key, $q, $s, $c)
                    ON CONFLICT(owner_kind, owner_key, quantity, subject_id)
                    DO UPDATE SET count = $c;
                    """,
                    ("$ok", key.OwnerKind), ("$key", key.OwnerKey), ("$q", key.Quantity),
                    ("$s", key.SubjectId), ("$c", updated));
            }

            tx.Commit();
        }
    }

    long ReadGateCounterUnlocked(SqliteConnection db, SqliteTransaction? tx, GateCounterKey key)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT count FROM rpg_gate_counter
            WHERE owner_kind = $ok AND owner_key = $key AND quantity = $q AND subject_id = $s;
            """;
        cmd.Parameters.AddWithValue("$ok", key.OwnerKind);
        cmd.Parameters.AddWithValue("$key", key.OwnerKey);
        cmd.Parameters.AddWithValue("$q", key.Quantity);
        cmd.Parameters.AddWithValue("$s", key.SubjectId);
        // No row yet == no credits ever flushed for this key -- 0, the same "row presence is the only
        // state" reading §4.1 establishes (mirrors RpgStore.PassiveTree.cs's respec-count read).
        return cmd.ExecuteScalar() is long stored ? stored : 0L;
    }

    /// <summary>Single-key read — never the index, never the equivalents (§4.1: those are derived on
    /// read by a later module, task G4, not stored). 0 for a key with no row, never an error.</summary>
    public long LoadGateCounter(string ownerKind, string ownerKey, string quantity, string subjectId)
    {
        if (string.IsNullOrWhiteSpace(ownerKind)) throw new ArgumentException("ownerKind required", nameof(ownerKind));
        if (string.IsNullOrWhiteSpace(ownerKey)) throw new ArgumentException("ownerKey required", nameof(ownerKey));
        if (string.IsNullOrWhiteSpace(quantity)) throw new ArgumentException("quantity required", nameof(quantity));
        if (string.IsNullOrWhiteSpace(subjectId)) throw new ArgumentException("subjectId required", nameof(subjectId));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadGateCounterUnlocked(db, tx: null, new GateCounterKey(ownerKind, ownerKey, quantity, subjectId));
        }
    }

    /// <summary>Every subject this owner has ANY credit under, for one quantity — the shape a
    /// tree-surface read (task G6) needs to render a whole family at once rather than one `subjectId`
    /// lookup per tree. Sparse: a subject with no row is simply absent, never a zero entry.</summary>
    public IReadOnlyDictionary<string, long> LoadGateCountersForOwner(string ownerKind, string ownerKey, string quantity)
    {
        if (string.IsNullOrWhiteSpace(ownerKind)) throw new ArgumentException("ownerKind required", nameof(ownerKind));
        if (string.IsNullOrWhiteSpace(ownerKey)) throw new ArgumentException("ownerKey required", nameof(ownerKey));
        if (string.IsNullOrWhiteSpace(quantity)) throw new ArgumentException("quantity required", nameof(quantity));

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT subject_id, count FROM rpg_gate_counter
                WHERE owner_kind = $ok AND owner_key = $key AND quantity = $q;
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$key", ownerKey);
            cmd.Parameters.AddWithValue("$q", quantity);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = r.GetInt64(1);
        }
        return result;
    }
}
