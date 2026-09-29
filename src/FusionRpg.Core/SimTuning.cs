using System.Text.Json;

namespace FusionRpg.Core;

/// <summary>SimEngine's fallback spawn stats (tunables-ssot.md T1) — used when a request omits
/// an explicit value. Name strings (level/type names) stay in <see cref="SimDefaults"/>.</summary>
public sealed record SimTuning(
    int SchemaVersion, int Version, long PlantHp, long PlantAttack, long ZombieHp, long ZombieAttack, long HitDamage);

public sealed class SimTuningRejection : Exception
{
    public SimTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class SimTuningLoader
{
    public static SimTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SimTuningRejection("sim tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new SimTuningRejection($"sim tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new SimTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                PlantHp: Long(root, "plantHp"),
                PlantAttack: Long(root, "plantAttack"),
                ZombieHp: Long(root, "zombieHp"),
                ZombieAttack: Long(root, "zombieAttack"),
                HitDamage: Long(root, "hitDamage"));
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new SimTuningRejection($"sim tuning: missing or non-integer '{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new SimTuningRejection($"sim tuning: missing or non-integer '{key}'");
        return v;
    }
}
