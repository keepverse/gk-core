using System.Text.Json;

namespace FusionRpg.Core.Effects;

public sealed record DamageFxFloaterTuning(int Cap, double LifeSeconds, double RisePixels);

/// <summary>Effects-domain balance surface (tunables-ssot.md T1) — MatchupRead's per-mille price
/// share and DamageFxFloaterRules' cap/timing (the one source VfxRules' floater fields alias).</summary>
public sealed record EffectsTuning(
    int SchemaVersion, int Version, int MatchupReadSlotShareMilli, DamageFxFloaterTuning DamageFxFloater);

public sealed class EffectsTuningRejection : Exception
{
    public EffectsTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class EffectsTuningLoader
{
    public static EffectsTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new EffectsTuningRejection("effects tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new EffectsTuningRejection($"effects tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var matchupRead = Obj(root, "matchupRead");
            var floater = Obj(root, "damageFxFloater");

            return new EffectsTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                MatchupReadSlotShareMilli: Int(matchupRead, "slotShareMilli"),
                DamageFxFloater: new DamageFxFloaterTuning(
                    Cap: Int(floater, "cap"),
                    LifeSeconds: Double(floater, "lifeSeconds"),
                    RisePixels: Double(floater, "risePixels")));
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new EffectsTuningRejection($"effects tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new EffectsTuningRejection($"effects tuning: missing or non-integer '{key}'");
        return v;
    }

    static double Double(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new EffectsTuningRejection($"effects tuning: missing or non-number '{key}'");
        return el.GetDouble();
    }
}

/// <summary>Fans one effects.v{n}.json load out to both classes that read it (tunables-ssot.md §7.2).</summary>
public static class EffectsTuningHub
{
    public static void Configure(EffectsTuning tuning)
    {
        DamageFxFloaterRules.Configure(tuning);
        Atoms.Power.MatchupRead.Configure(tuning);
    }
}
