using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>One threat rung — `creature-threat.v1.json`'s own row (`threat-band`, creature-seed module 4).
/// `ThetaOffset` lives inside Θ itself, additive, before `P(Θ)` runs (ssot-power-scale.md, row 18).</summary>
public sealed record ThreatRung(int Rung, string Id, int? MaxScore, int ThetaOffset);

/// <summary>`species-generator`'s read of `creature-threat.v1.json` — first ported to C# here (T4.4);
/// `threat-audit` (the Python module that classifies a species INTO a rung) owns the file, this
/// module only reads the `thetaOffset` column it already publishes.</summary>
public sealed record CreatureThreatTuning(
    int Version, IReadOnlyList<ThreatRung> Thresholds, int InferredDefaultRung)
{
    /// <summary>The threat rung ids in file order — the ladder's declaring read
    /// (tier-propagation-contract T-2). Consumers validate against this, never a restated tuple.</summary>
    public IReadOnlyList<string> RungIds => Thresholds.Select(t => t.Id).ToList();

    /// <summary>The offset for a real `threatBand` id, or the file's own sanctioned fallback
    /// (<see cref="InferredDefaultRung"/>'s rung) when the anchor never classified this field —
    /// verified against real anchor data, not a hypothetical: `pea.json`/`sunflower.json` both
    /// genuinely omit `threatBand` today.</summary>
    public int OffsetFor(string? threatBand)
    {
        if (threatBand is not null)
        {
            var match = Thresholds.FirstOrDefault(t => string.Equals(t.Id, threatBand, StringComparison.Ordinal));
            if (match is not null) return match.ThetaOffset;
        }

        var fallback = Thresholds.FirstOrDefault(t => t.Rung == InferredDefaultRung);
        return fallback?.ThetaOffset
            ?? throw new InvalidOperationException($"inferredDefaultRung {InferredDefaultRung} names no threshold row");
    }
}

public sealed class CreatureThreatTuningRejection : Exception
{
    public CreatureThreatTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class CreatureThreatTuningLoader
{
    public static CreatureThreatTuning Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CreatureThreatTuningRejection($"creature threat: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("thresholds", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new CreatureThreatTuningRejection("creature threat: missing or non-array 'thresholds'");

            var rungs = new List<ThreatRung>();
            foreach (var el in arr.EnumerateArray())
                rungs.Add(new ThreatRung(
                    Int(el, "rung"), Str(el, "id"),
                    el.TryGetProperty("maxScore", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : null,
                    Int(el, "thetaOffset")));

            return new CreatureThreatTuning(Int(root, "version"), rungs, Int(root, "inferredDefaultRung"));
        }
    }

    static int Int(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : throw new CreatureThreatTuningRejection($"creature threat: missing or non-integer '{key}'");

    static string Str(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new CreatureThreatTuningRejection($"creature threat: missing or non-string '{key}'");
}
