using System.Text.Json;

namespace FusionRpg.Core.Items.Drops;

public sealed class LootCorpusRejection : Exception
{
    public LootCorpusRejection(string message) : base(message) { }
}

/// <summary>Everything one loot seed file declares.</summary>
public sealed record LootCorpus(
    IReadOnlyList<LootSourceRow> Sources,
    IReadOnlyList<DropTableRow> Tables);

/// <summary>
/// Pure parser for <c>gk-data/packs/fusion/data/seed/loot/*.json</c> — the RUNTIME drop-table corpus.
///
/// <para>⚠ <b>This is a different corpus from <c>gk-data/packs/fusion/data/seed/items/drop-tables/</c>, on purpose.</b>
/// That one is the seedsmith's AUTHORED input (entry-shapes.md §9): it writes a <c>dropBand</c> where
/// a weight belongs and a <c>qtyCurve</c> where a count belongs, because seed-contract.md §1 forbids
/// an author from typing a magnitude. The generator that resolves those bands into rows is stage-1b
/// infrastructure that does not exist yet. This corpus is the GENERATED shape — real integer weights,
/// real counts, real <c>affix_channel</c> — and it is what spec-drop-volume.md Correction 1's
/// per-event calibration is expressed in. Neither replaces the other.</para>
/// </summary>
public static class LootCorpusReader
{
    public static LootCorpus Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new LootCorpusRejection("loot corpus: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new LootCorpusRejection($"loot corpus: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var tables = new List<DropTableRow>();
            var sources = new List<LootSourceRow>();

            if (root.TryGetProperty("tables", out var tablesEl))
            {
                if (tablesEl.ValueKind != JsonValueKind.Array)
                    throw new LootCorpusRejection("loot corpus: 'tables' must be an array");
                foreach (var t in tablesEl.EnumerateArray()) tables.Add(ReadTable(t));
            }

            if (root.TryGetProperty("sources", out var sourcesEl))
            {
                if (sourcesEl.ValueKind != JsonValueKind.Array)
                    throw new LootCorpusRejection("loot corpus: 'sources' must be an array");
                foreach (var s in sourcesEl.EnumerateArray()) sources.Add(ReadSource(s));
            }

            return new LootCorpus(sources, tables);
        }
    }

    /// <summary>Merge several files into one corpus — the shipped seed directory is more than one file.</summary>
    public static LootCorpus Merge(IEnumerable<LootCorpus> parts)
    {
        var tables = new List<DropTableRow>();
        var sources = new List<LootSourceRow>();
        foreach (var p in parts) { tables.AddRange(p.Tables); sources.AddRange(p.Sources); }
        return new LootCorpus(sources, tables);
    }

    static LootSourceRow ReadSource(JsonElement e) => new(
        Str(e, "sourceKind"),
        Str(e, "sourceId"),
        Str(e, "tableId"),
        Int(e, "contentLevel"),
        StrOrNull(e, "firstClearGrant"));

    static DropTableRow ReadTable(JsonElement e)
    {
        var tableId = Str(e, "tableId");

        var allow = new List<string>();
        if (e.TryGetProperty("sourceAllow", out var allowEl) && allowEl.ValueKind == JsonValueKind.Array)
            foreach (var a in allowEl.EnumerateArray())
                allow.Add(a.GetString() ?? throw new LootCorpusRejection($"table '{tableId}': non-string sourceAllow member"));

        var groups = new List<DropTableGroupRow>();
        if (e.TryGetProperty("groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
        {
            var seq = 0;
            foreach (var g in groupsEl.EnumerateArray()) groups.Add(ReadGroup(tableId, g, seq++));
        }

        return new DropTableRow(
            tableId, allow, IntOrNull(e, "minIlvl"), IntOrNull(e, "maxIlvl"),
            BoolOr(e, "enabled", true), IntOr(e, "revision", 0), groups);
    }

    static DropTableGroupRow ReadGroup(string tableId, JsonElement e, int defaultSeq)
    {
        var groupKey = Str(e, "groupKey");
        var entries = new List<DropTableEntryRow>();
        if (e.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            var seq = 0;
            foreach (var en in entriesEl.EnumerateArray()) entries.Add(ReadEntry(tableId, groupKey, en, seq++));
        }

        // `rolls` is the PRE-SCALE count step 5a multiplies by the Θ volume scale.
        return new DropTableGroupRow(groupKey, IntOr(e, "seq", defaultSeq), IntOr(e, "rolls", 1), entries);
    }

    static DropTableEntryRow ReadEntry(string tableId, string groupKey, JsonElement e, int defaultSeq)
    {
        var where = $"table '{tableId}' group '{groupKey}'";
        var kindName = Str(e, "entryKind");
        if (!TryKind(kindName, out var kind))
            throw new LootCorpusRejection($"{where}: entryKind '{kindName}' is not one of the ten (entry-shapes.md §9)");

        Dictionary<int, int>? shift = null;
        if (e.TryGetProperty("rarityWeightShift", out var shiftEl) && shiftEl.ValueKind == JsonValueKind.Object)
        {
            shift = new Dictionary<int, int>();
            foreach (var p in shiftEl.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var ordinal))
                    throw new LootCorpusRejection($"{where}: rarityWeightShift key '{p.Name}' is not an ordinal");
                shift[ordinal] = p.Value.GetInt32();
            }
        }

        return new DropTableEntryRow(
            IntOr(e, "seq", defaultSeq),
            kind,
            StrOr(e, "ref", ""),
            Int(e, "weight"),
            IntOr(e, "minCount", 1),
            IntOr(e, "maxCount", 1),
            IntOrNull(e, "minIlvl"),
            IntOrNull(e, "maxIlvl"),
            StrOrNull(e, "rarityFloor"),
            shift,
            BoolOr(e, "enabled", true),
            StrOr(e, "affixChannel", AffixChannels.Drop),
            StrOrNull(e, "frame"),
            StrOrNull(e, "role"),
            // species-gear-chain T30b: the parametric trophy directive — a scope + slot INSTEAD of
            // "ref" (never both, DropTableValidator enforces the exclusion). Absent on every entry
            // that predates the concept, which is every entry today.
            StrOrNull(e, "trophyScope"),
            IntOrNull(e, "trophySlot"));
    }

    /// <summary>The ten-value enum, spelled exactly as entry-shapes.md §9 spells it.</summary>
    public static bool TryKind(string name, out DropEntryKind kind)
    {
        switch (name)
        {
            case "equipment": kind = DropEntryKind.Equipment; return true;
            case "material": kind = DropEntryKind.Material; return true;
            case "currency": kind = DropEntryKind.Currency; return true;
            case "insert": kind = DropEntryKind.Insert; return true;
            case "charm": kind = DropEntryKind.Charm; return true;
            case "consumable": kind = DropEntryKind.Consumable; return true;
            case "unique": kind = DropEntryKind.Unique; return true;
            // relic-item-kind (empire-development Task 1.3a, spec-relic-item-kind.md §Design 2).
            case "relic": kind = DropEntryKind.Relic; return true;
            case "table": kind = DropEntryKind.Table; return true;
            case "nothing": kind = DropEntryKind.Nothing; return true;
            default: kind = default; return false;
        }
    }

    public static string KindName(DropEntryKind kind) => kind.ToString().ToLowerInvariant();

    static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new LootCorpusRejection($"loot corpus: missing or non-string '{key}'");

    static string? StrOrNull(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string StrOr(JsonElement e, string key, string fallback) => StrOrNull(e, key) ?? fallback;

    static int Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : throw new LootCorpusRejection($"loot corpus: missing or non-integer '{key}'");

    static int IntOr(JsonElement e, string key, int fallback) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    static int? IntOrNull(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    static bool BoolOr(JsonElement e, string key, bool fallback) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;
}
