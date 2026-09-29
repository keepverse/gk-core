using System.Text.Json;

namespace FusionRpg.Core.Combat.Shield;

public sealed record ShieldDrainPriorityTuning(int Aura, int Skill, int Innate);

/// <summary>Shield balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="ShieldPolicy.Configure"/> and <see cref="ShieldTuningLoader"/>.</summary>
public sealed record ShieldTuning(
    int SchemaVersion, int Version,
    long MatchupShareKPm, long ChipFloorKPm, long PenCapKPm, int MaxShieldsPerActor,
    ShieldDrainPriorityTuning DrainPriority);

public sealed class ShieldTuningRejection : Exception
{
    public ShieldTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class ShieldTuningLoader
{
    public static ShieldTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ShieldTuningRejection("shield tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ShieldTuningRejection($"shield tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");
            var matchupShareKPm = Long(root, "matchupShareKPm", "$");
            var chipFloorKPm = Long(root, "chipFloorKPm", "$");
            var penCapKPm = Long(root, "penCapKPm", "$");
            var maxShieldsPerActor = Int(root, "maxShieldsPerActor", "$");

            var d = Obj(root, "drainPriority", "$");
            var drainPriority = new ShieldDrainPriorityTuning(
                Aura: Int(d, "aura", "drainPriority"),
                Skill: Int(d, "skill", "drainPriority"),
                Innate: Int(d, "innate", "drainPriority"));

            return new ShieldTuning(schemaVersion, version, matchupShareKPm, chipFloorKPm,
                penCapKPm, maxShieldsPerActor, drainPriority);
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ShieldTuningRejection($"shield tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ShieldTuningRejection($"shield tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new ShieldTuningRejection($"shield tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}
