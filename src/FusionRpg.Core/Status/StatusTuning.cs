using System.Text.Json;

namespace FusionRpg.Core.Status;

/// <summary>Status balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="StatusPolicy.Configure"/> and <see cref="StatusTuningLoader"/>.</summary>
public sealed record StatusTuning(
    int SchemaVersion, int Version,
    double ApplyScaleK, double ApplyScaleFloor,
    double ResistFromPowerRatio, double MinNetFactor, double MaxNetFactor, double NetFactorScale,
    double ProgressionPowerStubDefault, int ProcDepthLimitDefault, double ApplySteepnessDefault,
    StatusApplyShape ApplyShape, double ApplyOffsetK);

public sealed class StatusTuningRejection : Exception
{
    public StatusTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class StatusTuningLoader
{
    public static StatusTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new StatusTuningRejection("status tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new StatusTuningRejection($"status tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new StatusTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                ApplyScaleK: Dbl(root, "applyScaleK"),
                ApplyScaleFloor: Dbl(root, "applyScaleFloor"),
                ResistFromPowerRatio: Dbl(root, "resistFromPowerRatio"),
                MinNetFactor: Dbl(root, "minNetFactor"),
                MaxNetFactor: Dbl(root, "maxNetFactor"),
                NetFactorScale: Dbl(root, "netFactorScale"),
                ProgressionPowerStubDefault: Dbl(root, "progressionPowerStubDefault"),
                ProcDepthLimitDefault: Int(root, "procDepthLimitDefault"),
                ApplySteepnessDefault: Dbl(root, "applySteepnessDefault"),
                ApplyShape: Shape(root, "applyShape"),
                ApplyOffsetK: Dbl(root, "applyOffsetK"));
        }
    }

    static StatusApplyShape Shape(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new StatusTuningRejection($"status tuning: missing or non-string '$.{key}'");
        // Explicit list, no Enum.TryParse: a typo'd shape must reject loudly rather than fall back to
        // a default, exactly like every other missing tunable (tunables-ssot.md T5).
        return el.GetString() switch
        {
            "sigmoid" => StatusApplyShape.Sigmoid,
            "linearFromZero" => StatusApplyShape.LinearFromZero,
            var other => throw new StatusTuningRejection(
                $"status tuning: '$.{key}' must be 'sigmoid' or 'linearFromZero', got '{other}'")
        };
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new StatusTuningRejection($"status tuning: missing or non-integer '$.{key}'");
        return v;
    }

    static double Dbl(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new StatusTuningRejection($"status tuning: missing or non-numeric '$.{key}'");
        return el.GetDouble();
    }
}
