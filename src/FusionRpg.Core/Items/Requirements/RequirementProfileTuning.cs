using System.Text.Json;

namespace FusionRpg.Core.Items.Requirements;

/// <summary>The parsed `equipment-requirements.v1.json` (item/spec-requirement-profiles.md §"Tuning
/// and Seedsmith boundary"). All balance values resolve through the tuning revision; source carries
/// none. Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public sealed record RequirementProfileTuning(
    int SchemaVersion,
    int Version,
    int ResolverRevision,
    int TuningRevision,
    IReadOnlyList<PowerBandRow> PowerBands,
    IReadOnlyList<string> FocusBands,
    IReadOnlyDictionary<(string Rarity, string PowerBand, string Focus), IReadOnlyDictionary<RequirementProfileKind, long>> ProfileMatrix,
    IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> LevelThresholds,
    IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> FixedThresholds,
    IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> RatioThresholds,
    IReadOnlyDictionary<string, bool> UpkeepEligible,
    IReadOnlyList<WeightedId> UpkeepResources,
    IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> ReserveBands,
    IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> CostBands,
    IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> PeriodBands,
    long SetEnvelopeBudget);

public sealed record PowerBandRow(string Id, long MinP, long MaxP);

public sealed record WeightedValue(long Value, long Weight);

public sealed record WeightedId(string Id, long Weight);

public static class RequirementProfileTuningLoader
{
    public static RequirementProfileTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new RequirementProfileRejection("tuning-empty", "empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new RequirementProfileRejection("tuning-invalid-json", ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            var tuning = new RequirementProfileTuning(
                RequireInt(root, "schemaVersion"),
                RequireInt(root, "version"),
                RequireInt(root, "resolverRevision"),
                RequireInt(root, "tuningRevision"),
                RequirePowerBands(root),
                RequireFocusBands(root),
                RequireMatrix(root),
                RequireBandTable(root, "levelThresholds"),
                RequireBandTable(root, "fixedThresholds"),
                RequireBandTable(root, "ratioThresholds"),
                RequireEligibility(root),
                RequireWeightedIds(root, "upkeepResources"),
                RequireBandTable(root, "reserveBands"),
                RequireBandTable(root, "costBands"),
                RequireBandTable(root, "periodBands"),
                RequireLong(root, "setEnvelopeBudget"));

            // Every matrix cell's power band and focus must resolve — a cell naming a band the file
            // does not define is an impossible cell, refused here rather than at mint.
            var powers = tuning.PowerBands.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            var focuses = tuning.FocusBands.ToHashSet(StringComparer.Ordinal);
            foreach (var (rarity, power, focus) in tuning.ProfileMatrix.Keys)
            {
                if (!powers.Contains(power))
                    throw new RequirementProfileRejection("tuning-unknown-power-band",
                        $"matrix cell ({rarity}, {power}, {focus}) names no defined power band");
                if (!focuses.Contains(focus))
                    throw new RequirementProfileRejection("tuning-unknown-focus-band",
                        $"matrix cell ({rarity}, {power}, {focus}) names no defined focus band");
            }
            return tuning;
        }
    }

    static IReadOnlyList<PowerBandRow> RequirePowerBands(JsonElement root)
    {
        if (!root.TryGetProperty("powerBands", out var el) || el.ValueKind != JsonValueKind.Array)
            throw new RequirementProfileRejection("tuning-missing-power-bands", "missing or non-array 'powerBands'");
        var bands = new List<PowerBandRow>();
        foreach (var b in el.EnumerateArray())
        {
            var id = RequireString(b, "id");
            var min = RequireLong(b, "minP");
            var max = RequireLong(b, "maxP");
            if (max < min)
                throw new RequirementProfileRejection("tuning-inverted-power-band",
                    $"power band '{id}' has maxP {max} below minP {min}");
            bands.Add(new PowerBandRow(id, min, max));
        }
        if (bands.Count == 0)
            throw new RequirementProfileRejection("tuning-empty-power-bands", "no power band defined");
        return bands;
    }

    static IReadOnlyList<string> RequireFocusBands(JsonElement root)
    {
        if (!root.TryGetProperty("focusBands", out var el) || el.ValueKind != JsonValueKind.Array)
            throw new RequirementProfileRejection("tuning-missing-focus-bands", "missing or non-array 'focusBands'");
        var bands = new List<string>();
        foreach (var b in el.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(b.GetString()))
                throw new RequirementProfileRejection("tuning-bad-focus-band", "a focus band is not a string");
            bands.Add(b.GetString()!);
        }
        // The focus vocabulary is the power vector's own five categories — a sixth focus would need
        // a sixth vector slot first (spec: focus selects from the vector, never beside it).
        var vector = new HashSet<string>(
            FusionRpg.Core.Effects.Atoms.Power.PowerVector.Categories, StringComparer.Ordinal);
        foreach (var band in bands)
            if (!vector.Contains(band))
                throw new RequirementProfileRejection("tuning-unknown-focus-band",
                    $"focus band '{band}' is not one of the power vector's five categories");
        return bands;
    }

    static IReadOnlyDictionary<(string, string, string), IReadOnlyDictionary<RequirementProfileKind, long>> RequireMatrix(JsonElement root)
    {
        if (!root.TryGetProperty("profileMatrix", out var el) || el.ValueKind != JsonValueKind.Array)
            throw new RequirementProfileRejection("tuning-missing-matrix", "missing or non-array 'profileMatrix'");
        var matrix = new Dictionary<(string, string, string), IReadOnlyDictionary<RequirementProfileKind, long>>();
        foreach (var cell in el.EnumerateArray())
        {
            var rarity = RequireString(cell, "rarity");
            if (!Creatures.CreatureRarityLadder.All.Any(r => Creatures.CreatureRarityIds.ToId(r) == rarity))
                throw new RequirementProfileRejection("tuning-unknown-rarity",
                    $"matrix cell names unknown rarity '{rarity}'");
            var power = RequireString(cell, "powerBand");
            var focus = RequireString(cell, "focus");
            if (!cell.TryGetProperty("weights", out var w) || w.ValueKind != JsonValueKind.Object)
                throw new RequirementProfileRejection("tuning-missing-cell-weights",
                    $"matrix cell ({rarity}, {power}, {focus}) has no weights object");
            var weights = new Dictionary<RequirementProfileKind, long>();
            foreach (var kv in w.EnumerateObject())
            {
                if (!Enum.TryParse<RequirementProfileKind>(kv.Name, ignoreCase: true, out var kind))
                    throw new RequirementProfileRejection("tuning-unknown-profile-kind",
                        $"matrix cell ({rarity}, {power}, {focus}) names unknown kind '{kv.Name}'");
                if (kv.Value.ValueKind != JsonValueKind.Number)
                    throw new RequirementProfileRejection("tuning-bad-cell-weight",
                        $"matrix cell ({rarity}, {power}, {focus}) kind '{kv.Name}' is not a number");
                var weight = kv.Value.GetInt64();
                if (weight < 0)
                    throw new RequirementProfileRejection("tuning-negative-cell-weight",
                        $"matrix cell ({rarity}, {power}, {focus}) kind '{kv.Name}' is negative ({weight})");
                weights[kind] = weight;
            }
            var key = (rarity, power, focus);
            if (matrix.ContainsKey(key))
                throw new RequirementProfileRejection("tuning-duplicate-cell",
                    $"matrix cell ({rarity}, {power}, {focus}) appears twice");
            matrix[key] = weights;
        }
        if (matrix.Count == 0)
            throw new RequirementProfileRejection("tuning-empty-matrix", "no matrix cell defined");
        return matrix;
    }

    static IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> RequireBandTable(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new RequirementProfileRejection($"tuning-missing-{key}", $"missing or non-object '{key}'");
        var table = new Dictionary<string, IReadOnlyList<WeightedValue>>(StringComparer.Ordinal);
        foreach (var band in el.EnumerateObject())
        {
            if (band.Value.ValueKind != JsonValueKind.Array)
                throw new RequirementProfileRejection($"tuning-bad-{key}",
                    $"'{key}[{band.Name}]' is not an array");
            var entries = new List<WeightedValue>();
            foreach (var e in band.Value.EnumerateArray())
            {
                if (!e.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number
                    || !e.TryGetProperty("weight", out var w) || w.ValueKind != JsonValueKind.Number)
                    throw new RequirementProfileRejection($"tuning-bad-{key}",
                        $"'{key}[{band.Name}]' carries a malformed entry");
                var weight = w.GetInt64();
                if (weight < 0)
                    throw new RequirementProfileRejection($"tuning-negative-{key}-weight",
                        $"'{key}[{band.Name}]' carries a negative weight ({weight})");
                entries.Add(new WeightedValue(v.GetInt64(), weight));
            }
            table[band.Name] = entries;
        }
        return table;
    }

    static IReadOnlyDictionary<string, bool> RequireEligibility(JsonElement root)
    {
        if (!root.TryGetProperty("upkeepEligible", out var el) || el.ValueKind != JsonValueKind.Object)
            throw new RequirementProfileRejection("tuning-missing-upkeep-eligibility",
                "missing or non-object 'upkeepEligible'");
        var table = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var band in el.EnumerateObject())
        {
            if (band.Value.ValueKind != JsonValueKind.True && band.Value.ValueKind != JsonValueKind.False)
                throw new RequirementProfileRejection("tuning-bad-upkeep-eligibility",
                    $"'upkeepEligible[{band.Name}]' is not a boolean");
            table[band.Name] = band.Value.GetBoolean();
        }
        return table;
    }

    static IReadOnlyList<WeightedId> RequireWeightedIds(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new RequirementProfileRejection($"tuning-missing-{key}", $"missing or non-array '{key}'");
        var ids = new List<WeightedId>();
        foreach (var e in el.EnumerateArray())
        {
            var id = RequireString(e, "id");
            if (!e.TryGetProperty("weight", out var w) || w.ValueKind != JsonValueKind.Number)
                throw new RequirementProfileRejection($"tuning-bad-{key}", $"'{key}[{id}]' has no numeric weight");
            var weight = w.GetInt64();
            if (weight <= 0)
                throw new RequirementProfileRejection($"tuning-nonpositive-{key}-weight",
                    $"'{key}[{id}]' has non-positive weight ({weight}) — mute by removing the entry, never by zeroing it");
            ids.Add(new WeightedId(id, weight));
        }
        if (ids.Count == 0)
            throw new RequirementProfileRejection($"tuning-empty-{key}", $"no '{key}' entry defined");
        return ids;
    }

    static string RequireString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new RequirementProfileRejection($"tuning-missing-{prop}", $"missing or empty '{prop}'");

    static int RequireInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : throw new RequirementProfileRejection($"tuning-missing-{prop}", $"missing or non-numeric '{prop}'");

    static long RequireLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : throw new RequirementProfileRejection($"tuning-missing-{prop}", $"missing or non-numeric '{prop}'");
}

