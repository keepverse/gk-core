using FusionRpg.Core.Combat.Element;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// The element roster and both matchup matrices as rows (spec-element-roster-data.md, E18).
///
/// <para><b>Three tables, not two.</b> The combat ring and the shield matrix are seeded identical —
/// verified across all 36 pairs — and are independently editable, which the shield spec makes an
/// Ask-first balance decision. One shared table would make divergence impossible to express and a
/// future edit to one silently change the other.</para>
///
/// <para><b>The ordinal is load-bearing and append-only.</b> It drives the generated channel set, so
/// changing one renames every channel derived from that element. An ordinal already held by a
/// different element is refused here, the same rule the rarity bands live under.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>Called from EnsureHotSchema so a fresh database has all three.</summary>
    void EnsureElementSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS effect_element (
              element_id   TEXT    NOT NULL PRIMARY KEY,
              display_name TEXT    NOT NULL DEFAULT '',
              ordinal      INTEGER NOT NULL,
              enabled      INTEGER NOT NULL DEFAULT 1,
              revision     INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_effect_element_ordinal ON effect_element(ordinal);

            CREATE TABLE IF NOT EXISTS effect_element_matrix_combat (
              attacker_element TEXT    NOT NULL,
              defender_element TEXT    NOT NULL,
              unit             INTEGER NOT NULL,
              PRIMARY KEY (attacker_element, defender_element)
            );

            CREATE TABLE IF NOT EXISTS effect_element_matrix_shield (
              attacker_element TEXT    NOT NULL,
              defender_element TEXT    NOT NULL,
              unit             INTEGER NOT NULL,
              PRIMARY KEY (attacker_element, defender_element)
            );
            """);
    }

    /// <summary>The roster as rows, or the shipped table when nothing has been imported yet.</summary>
    public ElementTable GetElementTable()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();

            var elements = ReadElements(db, null);
            if (elements.Count == 0) return ElementTable.Shipped();

            return new ElementTable(
                elements,
                ReadMatrix(db, null, "effect_element_matrix_combat"),
                ReadMatrix(db, null, "effect_element_matrix_shield"));
        }
    }

    /// <summary>
    /// Replace the roster and both matrices. Whole-table replacement, in one transaction: a matrix is
    /// one authored statement and a stale cell from a previous revision is content nobody wrote.
    /// </summary>
    public (bool Ok, string Reason) UpsertElementTable(ElementTable table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        var byOrdinal = new Dictionary<int, string>();
        foreach (var e in table.Elements)
        {
            if (string.IsNullOrWhiteSpace(e.ElementId)) return (false, "element_id is empty");
            if (byOrdinal.TryGetValue(e.Ordinal, out var taken))
                return (false, $"ordinal {e.Ordinal} is claimed by both '{taken}' and '{e.ElementId}'");
            byOrdinal[e.Ordinal] = e.ElementId;
        }

        lock (_gate)
        {
            var stored = ReadElementsLocked();

            // Append-only. An ordinal that moves renames every channel generated from that element,
            // and a retired ordinal that comes back re-points content that still names the old one.
            foreach (var e in table.Elements)
            {
                var was = stored.FirstOrDefault(s => string.Equals(s.ElementId, e.ElementId, StringComparison.Ordinal));
                if (was is not null && was.Ordinal != e.Ordinal)
                    return (false,
                        $"'{e.ElementId}' would move from ordinal {was.Ordinal} to {e.Ordinal}; " +
                        "ordinals are append-only because they name every generated channel");

                var owner = stored.FirstOrDefault(s => s.Ordinal == e.Ordinal);
                if (owner is not null && !string.Equals(owner.ElementId, e.ElementId, StringComparison.Ordinal))
                    return (false, $"ordinal {e.Ordinal} already belongs to '{owner.ElementId}'");
            }

            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            WriteElementTableUnlocked(db, tx, table);
            tx.Commit();
            return (true, "");
        }
    }

    /// <summary>The write half, on a caller-owned transaction — E14a imports everything at once.</summary>
    void WriteElementTableUnlocked(SqliteConnection db, SqliteTransaction tx, ElementTable table)
    {
            foreach (var e in table.Elements)
                ExecIn(db, tx, """
                    INSERT INTO effect_element (element_id, display_name, ordinal, enabled, revision)
                    VALUES ($id, $name, $ord, $on, 1)
                    ON CONFLICT(element_id) DO UPDATE SET
                      display_name = excluded.display_name, enabled = excluded.enabled,
                      revision = effect_element.revision + 1
                    WHERE effect_element.display_name IS NOT excluded.display_name
                       OR effect_element.enabled IS NOT excluded.enabled;
                    """,
                    ("$id", e.ElementId), ("$name", e.DisplayName ?? ""),
                    ("$ord", e.Ordinal), ("$on", e.Enabled ? 1 : 0));

            WriteMatrix(db, tx, "effect_element_matrix_combat", table.CombatRows);
            WriteMatrix(db, tx, "effect_element_matrix_shield", table.ShieldRows);
    }

    List<ElementRow> ReadElementsLocked()
    {
        using var db = OpenUnlocked();
        return ReadElements(db, null);
    }

    static List<ElementRow> ReadElements(SqliteConnection db, SqliteTransaction? tx)
    {
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT element_id, display_name, ordinal, enabled FROM effect_element ORDER BY ordinal;";
        using var r = cmd.ExecuteReader();

        var list = new List<ElementRow>();
        while (r.Read())
            list.Add(new ElementRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3) != 0));
        return list;
    }

    static List<ElementMatrixRow> ReadMatrix(SqliteConnection db, SqliteTransaction? tx, string table)
    {
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText =
            $"SELECT attacker_element, defender_element, unit FROM {table} " +
            "ORDER BY attacker_element, defender_element;";
        using var r = cmd.ExecuteReader();

        var list = new List<ElementMatrixRow>();
        while (r.Read()) list.Add(new ElementMatrixRow(r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    static void WriteMatrix(
        SqliteConnection db, SqliteTransaction tx, string table, IReadOnlyList<ElementMatrixRow> rows)
    {
        ExecIn(db, tx, $"DELETE FROM {table};");
        foreach (var row in rows)
            ExecIn(db, tx,
                $"INSERT INTO {table} (attacker_element, defender_element, unit) VALUES ($a, $d, $u);",
                ("$a", row.Attacker), ("$d", row.Defender), ("$u", row.Unit));
    }
}
