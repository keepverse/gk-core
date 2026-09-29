using FusionRpg.Core.Effects.Atoms.Power;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// The price tables (spec-power-vector.md, E9): <c>power_coefficient</c>,
/// <c>power_trigger_frequency</c>, and the sweep's side table <c>power_coefficient_proposal</c>.
///
/// <para><b>A sweep never overwrites a shipped number.</b> It writes proposals; a test reports the
/// gap; humans decide what ships. That is what makes "hand-authored now, fitted later" mechanical
/// rather than aspirational — and it is why the proposal table is deliberately <b>not</b> covered by
/// the content hash. A sweep running must not move a stamp, or every replay verdict downstream would
/// report a mismatch for a number nobody adopted.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsurePowerSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS power_coefficient (
              kind_id         TEXT    NOT NULL,
              channel         TEXT    NOT NULL DEFAULT '',
              coeff_milli     INTEGER NOT NULL,
              reference_scale INTEGER NOT NULL,
              PRIMARY KEY (kind_id, channel)
            );

            CREATE TABLE IF NOT EXISTS power_trigger_frequency (
              trigger_id TEXT    NOT NULL PRIMARY KEY,
              per_minute INTEGER NOT NULL
            );

            -- P0.3 (spec-power-vector.md, "predicates ARE priced"): the four-factor chain per leaf.
            -- arg_key defaults to '' for an arg-independent leaf, mirroring power_coefficient's own
            -- channel-defaults-to-'' shape.
            CREATE TABLE IF NOT EXISTS power_predicate_frequency (
              leaf_id               TEXT    NOT NULL,
              arg_key               TEXT    NOT NULL DEFAULT '',
              reachability_milli    INTEGER NOT NULL,
              susceptibility_milli  INTEGER NOT NULL,
              coincidence_milli     INTEGER NOT NULL,
              uptime_milli          INTEGER NOT NULL,
              PRIMARY KEY (leaf_id, arg_key)
            );

            -- The sweep's output. Uncovered by the content hash on purpose: a proposal is a
            -- suggestion, and a suggestion must not move a stamp.
            CREATE TABLE IF NOT EXISTS power_coefficient_proposal (
              kind_id         TEXT    NOT NULL,
              channel         TEXT    NOT NULL DEFAULT '',
              coeff_milli     INTEGER NOT NULL,
              reference_scale INTEGER NOT NULL,
              note            TEXT    NOT NULL DEFAULT '',
              PRIMARY KEY (kind_id, channel)
            );
            """);
    }

    /// <summary>The authored tables, or the hand-authored defaults when nothing has been imported.</summary>
    public PowerTables GetPowerTables()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();

            var coefficients = ReadCoefficients(db, "power_coefficient");
            if (coefficients.Count == 0) return PowerTables.Authored();

            return new PowerTables(coefficients, ReadFrequencies(db), ReadPredicateFrequencies(db));
        }
    }

    /// <summary>Replace both authored tables. Whole-table, in one transaction.</summary>
    public (bool Ok, string Reason) UpsertPowerTables(PowerTables tables)
    {
        if (tables is null) throw new ArgumentNullException(nameof(tables));

        foreach (var c in tables.Coefficients)
            if (c.ReferenceScale <= 0)
                return (false,
                    $"{c.KindId}/{(c.Channel.Length == 0 ? "*" : c.Channel)}: reference scale " +
                    $"{c.ReferenceScale} — normalisation divides by it, and a zero scale prices " +
                    "every magnitude alike, which is the units trap this column exists to close");

        foreach (var p in tables.PredicateFrequencies)
        {
            var key = $"{p.LeafId}/{(p.ArgKey.Length == 0 ? "*" : p.ArgKey)}";
            foreach (var (name, value) in new (string, int)[]
                     {
                         ("reachability", p.ReachabilityMilli), ("susceptibility", p.SusceptibilityMilli),
                         ("coincidence", p.CoincidenceMilli), ("uptime", p.UptimeMilli),
                     })
                if (value is < 0 or > 1000)
                    return (false,
                        $"{key}: {name}Milli {value} — every factor in the chain is a per-mille " +
                        "probability (PS-8 bounded ratio) and must sit in [0, 1000]");
        }

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            WritePowerTablesUnlocked(db, tx, tables);
            // C4 (completeness-audit.md): this direct API has no production caller today and no
            // skip-when-identical tracking to bump conditionally on, unlike the import path — bump
            // unconditionally so an E19 receiver actually re-negotiates after a policy edit through it.
            ExecIn(db, tx, "UPDATE content_meta SET catalog_revision = catalog_revision + 1 WHERE id = 1;");
            tx.Commit();
            return (true, "");
        }
    }

    void WritePowerTablesUnlocked(SqliteConnection db, SqliteTransaction tx, PowerTables tables)
    {
        ExecIn(db, tx, "DELETE FROM power_coefficient;");
        foreach (var c in tables.Coefficients)
            ExecIn(db, tx,
                "INSERT INTO power_coefficient (kind_id, channel, coeff_milli, reference_scale) " +
                "VALUES ($k, $c, $m, $r);",
                ("$k", c.KindId), ("$c", c.Channel ?? ""), ("$m", c.CoeffMilli), ("$r", c.ReferenceScale));

        ExecIn(db, tx, "DELETE FROM power_trigger_frequency;");
        foreach (var f in tables.Frequencies)
            ExecIn(db, tx,
                "INSERT INTO power_trigger_frequency (trigger_id, per_minute) VALUES ($t, $p);",
                ("$t", f.Trigger), ("$p", f.PerMinute));

        ExecIn(db, tx, "DELETE FROM power_predicate_frequency;");
        foreach (var p in tables.PredicateFrequencies)
            ExecIn(db, tx,
                "INSERT INTO power_predicate_frequency " +
                "(leaf_id, arg_key, reachability_milli, susceptibility_milli, coincidence_milli, uptime_milli) " +
                "VALUES ($l, $a, $r, $s, $c, $u);",
                ("$l", p.LeafId), ("$a", p.ArgKey ?? ""), ("$r", p.ReachabilityMilli),
                ("$s", p.SusceptibilityMilli), ("$c", p.CoincidenceMilli), ("$u", p.UptimeMilli));
    }

    /// <summary>
    /// E44 criterion 0 (spec-power-sweep.md §4.1): the seed import path's own writer, and the first
    /// real caller of <see cref="WritePowerTablesUnlocked"/> — <see cref="UpsertPowerTables"/> already
    /// called it, but has zero production callers itself. Called from <c>RpgStore.Import.cs</c>'s
    /// <c>ImportContent</c>, inside its own transaction, so this needs no new SQL.
    ///
    /// <para><b>Overlays the incoming rows onto whatever is already stored</b>, rather than replacing
    /// the whole table with just this batch — the same "batch overlays stored" rule <c>ImportContent</c>
    /// already applies to atoms. A coefficients file only ever adds or retunes the kind/channel pairs
    /// it names; <see cref="WritePowerTablesUnlocked"/> itself is a whole-table delete-then-insert; the
    /// merge is what keeps a one-row coefficients file from wiping every other authored coefficient.
    /// <c>power_trigger_frequency</c>/<c>power_predicate_frequency</c> carry over unchanged — this seed
    /// kind authors neither, and the writer replaces all three tables together.</para>
    /// </summary>
    /// <returns>How many coefficient rows actually differ from what was stored. Zero skips the write
    /// entirely, so a repeat import of an unchanged coefficients file does not bump
    /// <c>catalog_revision</c> — the same no-op discipline every other content kind here already has.</returns>
    int WriteCoefficientsUnlocked(
        SqliteConnection db, SqliteTransaction tx, IReadOnlyList<PowerCoefficientRow> incoming)
    {
        var merged = new Dictionary<(string KindId, string Channel), PowerCoefficientRow>();
        foreach (var c in ReadCoefficients(db, "power_coefficient")) merged[(c.KindId, c.Channel)] = c;

        var changedRows = 0;
        foreach (var c in incoming)
        {
            var key = (c.KindId, c.Channel);
            if (!merged.TryGetValue(key, out var existing) || existing != c) changedRows++;
            merged[key] = c;
        }

        if (changedRows == 0) return 0;

        var tables = new PowerTables(merged.Values.ToList(), ReadFrequencies(db), ReadPredicateFrequencies(db));
        WritePowerTablesUnlocked(db, tx, tables);
        return changedRows;
    }

    /// <summary>
    /// ST4.5c (manager ruling on the ST4.5a diagnosis): the seed import path's writer for
    /// <c>power_trigger_frequency</c> — the sibling of <see cref="WriteCoefficientsUnlocked"/>, carrying
    /// the identical overlay discipline, and the fix for the hole its own doc comment above records:
    /// <c>WriteCoefficientsUnlocked</c> carries the stored frequency rows over UNCHANGED, and nothing
    /// else ever authored one, so the table stayed empty in every database that had ever imported a
    /// coefficient and <c>CostFunction.Conditionality</c> multiplied every triggered atom by zero.
    ///
    /// <para>Overlays rather than replaces, for the same reason as the coefficients writer: a
    /// frequencies file only ever adds or retunes the triggers it names. <b>Zero changed rows skips the
    /// write entirely</b>, so a repeat import of an unchanged file does not bump
    /// <c>catalog_revision</c> — C4's no-op discipline, the same as every other content kind here.</para>
    /// </summary>
    /// <returns>How many frequency rows actually differ from what was stored.</returns>
    int WriteTriggerFrequenciesUnlocked(
        SqliteConnection db, SqliteTransaction tx, IReadOnlyList<TriggerFrequencyRow> incoming)
    {
        var merged = new Dictionary<string, TriggerFrequencyRow>(StringComparer.Ordinal);
        foreach (var f in ReadFrequencies(db)) merged[f.Trigger] = f;

        var changedRows = 0;
        foreach (var f in incoming)
        {
            if (!merged.TryGetValue(f.Trigger, out var existing) || existing != f) changedRows++;
            merged[f.Trigger] = f;
        }

        if (changedRows == 0) return 0;

        var tables = new PowerTables(
            ReadCoefficients(db, "power_coefficient"), merged.Values.ToList(), ReadPredicateFrequencies(db));
        WritePowerTablesUnlocked(db, tx, tables);
        return changedRows;
    }

    /// <summary>
    /// Record what a sweep would like the coefficients to be. Never touches the authored table.
    /// </summary>
    public void UpsertCoefficientProposals(IReadOnlyList<(PowerCoefficientRow Row, string Note)> proposals)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            foreach (var (row, note) in proposals)
                ExecIn(db, tx, """
                    INSERT INTO power_coefficient_proposal
                      (kind_id, channel, coeff_milli, reference_scale, note)
                    VALUES ($k, $c, $m, $r, $n)
                    ON CONFLICT(kind_id, channel) DO UPDATE SET
                      coeff_milli = excluded.coeff_milli,
                      reference_scale = excluded.reference_scale,
                      note = excluded.note;
                    """,
                    ("$k", row.KindId), ("$c", row.Channel ?? ""), ("$m", row.CoeffMilli),
                    ("$r", row.ReferenceScale), ("$n", note ?? ""));

            tx.Commit();
        }
    }

    /// <summary>What the sweep proposed, beside what ships — the gap a test reports.</summary>
    public IReadOnlyList<(PowerCoefficientRow Proposed, string Note)> ListCoefficientProposals()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT kind_id, channel, coeff_milli, reference_scale, note " +
                "FROM power_coefficient_proposal ORDER BY kind_id, channel;";
            using var r = cmd.ExecuteReader();

            var list = new List<(PowerCoefficientRow, string)>();
            while (r.Read())
                list.Add((new PowerCoefficientRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)),
                    r.GetString(4)));
            return list;
        }
    }

    /// <summary>
    /// Price every atom and store the result in <c>power_json</c>.
    ///
    /// <para><b>An override is never overwritten.</b> <c>power_override_json</c> is a designer's
    /// decision with a required note; the computed base sits beside it, and E14b's drift test is what
    /// reports the gap. A backfill that clobbered overrides would erase exactly the record of where
    /// the cost function is bad.</para>
    ///
    /// <para><c>power_json</c> is a hashed column, so this moves the content hash — correctly, and
    /// attributably: prices are content.</para>
    /// </summary>
    /// <returns>How many atoms were priced, and the ids of those that could not be.</returns>
    public (int Priced, IReadOnlyList<string> Unpriced) BackfillAtomPower(PowerTables? tables = null)
    {
        var t = tables ?? GetPowerTables();
        var atoms = ListAtoms();
        var unpriced = new List<string>();
        var priced = 0;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            foreach (var atom in atoms)
            {
                var result = CostFunction.Price(atom, t);
                if (!result.Ok)
                {
                    // Left NULL rather than written as zero. An unpriced atom must look unpriced, or
                    // a budget would happily accept a whole family that costs nothing.
                    unpriced.Add(atom.AtomId);
                    continue;
                }

                ExecIn(db, tx,
                    "UPDATE effect_atom SET power_json = $p, revision = revision + 1 " +
                    "WHERE atom_id = $id AND power_json IS NOT $p;",
                    ("$p", result.Power.ToJson()), ("$id", atom.AtomId));
                priced++;
            }

            tx.Commit();
        }

        return (priced, unpriced);
    }

    static List<PowerCoefficientRow> ReadCoefficients(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            $"SELECT kind_id, channel, coeff_milli, reference_scale FROM {table} ORDER BY kind_id, channel;";
        using var r = cmd.ExecuteReader();

        var list = new List<PowerCoefficientRow>();
        while (r.Read())
            list.Add(new PowerCoefficientRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    static List<TriggerFrequencyRow> ReadFrequencies(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT trigger_id, per_minute FROM power_trigger_frequency ORDER BY trigger_id;";
        using var r = cmd.ExecuteReader();

        var list = new List<TriggerFrequencyRow>();
        while (r.Read()) list.Add(new TriggerFrequencyRow(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    static List<PredicateFrequencyRow> ReadPredicateFrequencies(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT leaf_id, arg_key, reachability_milli, susceptibility_milli, coincidence_milli, uptime_milli
            FROM power_predicate_frequency ORDER BY leaf_id, arg_key;
            """;
        using var r = cmd.ExecuteReader();

        var list = new List<PredicateFrequencyRow>();
        while (r.Read())
            list.Add(new PredicateFrequencyRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
        return list;
    }
}
