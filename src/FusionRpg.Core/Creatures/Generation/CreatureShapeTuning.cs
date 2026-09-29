using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>`species-generator`'s own balance surface (`gk-core/data/tuning/creature-shape.v1.json`,
/// tunables-ssot.md T1) — the fallback tempo/reach tables (spec §4: a stated interval always wins),
/// the impure primary/secondary split, and the species' own base Θ before the threat-rung offset.</summary>
public sealed record CreatureShapeTuning(
    int SchemaVersion, int Version,
    IReadOnlyDictionary<string, long> AttackTempoIntervalMs,
    IReadOnlyDictionary<string, long> ReachRangeCells,
    long ImpureSecondaryShareMilli,
    int SpeciesBaseTheta);

public sealed class CreatureShapeTuningRejection : Exception
{
    public CreatureShapeTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class CreatureShapeTuningLoader
{
    public static CreatureShapeTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CreatureShapeTuningRejection("creature shape: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CreatureShapeTuningRejection($"creature shape: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new CreatureShapeTuning(
                SchemaVersion: Int(root, "schemaVersion", "$"),
                Version: Int(root, "version", "$"),
                AttackTempoIntervalMs: LongMap(root, "attackTempoIntervalMs"),
                ReachRangeCells: LongMap(root, "reachRangeCells"),
                ImpureSecondaryShareMilli: Long(root, "impureSecondaryShareMilli", "$"),
                SpeciesBaseTheta: Int(root, "speciesBaseTheta", "$"));
        }
    }

    static Dictionary<string, long> LongMap(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var obj) || obj.ValueKind != JsonValueKind.Object)
            throw new CreatureShapeTuningRejection($"creature shape: missing or non-object '{key}'");

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.StartsWith('_')) continue; // "_note" and friends
            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt64(out var v))
                throw new CreatureShapeTuningRejection($"creature shape: '{key}.{prop.Name}' is not an integer");
            map[prop.Name] = v;
        }
        return map;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new CreatureShapeTuningRejection($"creature shape: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new CreatureShapeTuningRejection($"creature shape: missing or non-integer '{path}.{key}'");
        return v;
    }
}
