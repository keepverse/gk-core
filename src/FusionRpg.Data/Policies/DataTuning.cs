using System.Text.Json;

namespace FusionRpg.Data.Policies;

public sealed record RetainTuning(int ActivityTail, int XpTailPerActor, int SoulTailPerPlayer, int KeepLastNFullCaptureRuns);

/// <summary>Data-layer balance surface (tunables-ssot.md T1) — retention tails. Schema-version fields
/// stay structural consts (SealedCompactionPolicy). The souls-award overflow ceiling used to live here
/// as a fixed constant; T3.5 (spec-caps-reconcile.md §2.1) made it dynamic (headroom to long.MaxValue
/// from the current balance) instead, so there is no longer a constant to carry.</summary>
public sealed record DataTuning(int SchemaVersion, int Version, RetainTuning Retain);

public sealed class DataTuningRejection : Exception
{
    public DataTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class DataTuningLoader
{
    public static DataTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DataTuningRejection("data tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new DataTuningRejection($"data tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var retain = Obj(root, "retain");

            return new DataTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                Retain: new RetainTuning(
                    ActivityTail: Int(retain, "activityTail"),
                    XpTailPerActor: Int(retain, "xpTailPerActor"),
                    SoulTailPerPlayer: Int(retain, "soulTailPerPlayer"),
                    KeepLastNFullCaptureRuns: Int(retain, "keepLastNFullCaptureRuns")));
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DataTuningRejection($"data tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new DataTuningRejection($"data tuning: missing or non-integer '{key}'");
        return v;
    }
}
