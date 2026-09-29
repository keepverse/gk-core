using System.Text.Json;

namespace FusionRpg.Core.Actions.Rungs;

public sealed class RungTableRejection : Exception
{
    public RungTableRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser for `data/tuning/action-rungs.v{n}.json` (tunables-ssot.md §7.2 — no file I/O here).
/// Every rejection names the row or axis at fault; a bad table never loads partially.
/// </summary>
public static class RungTableLoader
{
    public static RungTable Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new RungTableRejection("action rung table: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new RungTableRejection($"action rung table: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var cap = Int(root, "cap", "$");

            if (!root.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                throw new RungTableRejection("action rung table: missing 'rows' array");

            var rows = new List<RungRow>();
            foreach (var el in rowsEl.EnumerateArray())
                rows.Add(ParseRow(el));

            if (rows.Count == 0)
                throw new RungTableRejection("action rung table: zero rows — an empty ladder is not a valid ladder");

            // Contiguous 1..N, in file order — a gap is a load rejection naming the missing index.
            for (var i = 0; i < rows.Count; i++)
            {
                var expected = i + 1;
                if (rows[i].Rung != expected)
                    throw new RungTableRejection(
                        $"action rung table: expected rung {expected} at position {i}, found {rows[i].Rung} — " +
                        "the rung sequence must be contiguous with no gap");
            }

            return new RungTable(cap, rows);
        }
    }

    static RungRow ParseRow(JsonElement el)
    {
        var rung = Int(el, "rung", "rows[]");
        var minTier = Int(el, "minTier", $"rows[rung={rung}]");
        var maxTier = Int(el, "maxTier", $"rows[rung={rung}]");
        if (minTier > maxTier)
            throw new RungTableRejection($"action rung table: rung {rung} tier window [{minTier}, {maxTier}] is inverted");

        var poolRolls = Int(el, "poolRolls", $"rows[rung={rung}]");
        var qPower = Int(el, "qPowerMilli", $"rows[rung={rung}]");
        var cost = Int(el, "costMulti", $"rows[rung={rung}]");
        var cd = Int(el, "cdMulti", $"rows[rung={rung}]");

        if (qPower <= 0 || cost <= 0 || cd <= 0)
            throw new RungTableRejection($"action rung table: rung {rung} has a non-positive multiplier");

        var budget = new List<string>();
        if (el.TryGetProperty("structureBudget", out var budgetEl))
        {
            if (budgetEl.ValueKind != JsonValueKind.Array)
                throw new RungTableRejection($"action rung table: rung {rung} 'structureBudget' must be an array");

            foreach (var axisEl in budgetEl.EnumerateArray())
            {
                var axis = axisEl.GetString() ?? "";
                if (!StructureAxes.IsKnown(axis))
                    throw new RungTableRejection(
                        $"action rung table: rung {rung} structureBudget names unknown axis '{axis}' — " +
                        $"the closed set is [{string.Join(", ", StructureAxes.Closed)}]");
                budget.Add(axis);
            }
        }

        // A-G1 (spec-tier-access-gate.md §3.1): optional, not required like qPower/cost/cd above --
        // action-rungs.v1.json and every inline test fixture written before this column existed must
        // keep loading unchanged. Absent means "no ceiling data source loaded" (RungRow.PowerBudgetMilli
        // is null, never 0). Present but malformed still throws -- a half-authored column is a defect,
        // an absent one is a pre-v2 table.
        long? powerBudgetMilli = null;
        if (el.TryGetProperty("powerBudgetMilli", out var powerBudgetEl))
        {
            if (powerBudgetEl.ValueKind != JsonValueKind.Number || !powerBudgetEl.TryGetInt64(out var pb))
                throw new RungTableRejection($"action rung table: rung {rung} 'powerBudgetMilli' must be an integer");
            if (pb < 0)
                throw new RungTableRejection($"action rung table: rung {rung} has a negative powerBudgetMilli");
            powerBudgetMilli = pb;
        }

        return new RungRow(rung, minTier, maxTier, poolRolls, qPower, cost, cd, budget, powerBudgetMilli);
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new RungTableRejection($"action rung table: missing or non-integer '{path}.{key}'");
        return v;
    }
}
