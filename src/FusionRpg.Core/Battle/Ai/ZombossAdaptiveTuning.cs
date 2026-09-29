using System.Text.Json;

namespace FusionRpg.Core.Battle.Ai;

/// <summary>
/// species-build-todo.md T4.4 — `zomboss-adaptive`'s own balance surface
/// (`gk-core/data/tuning/zomboss-adaptive.v1.json`, tunables-ssot.md), read by <see cref="ZombossPatternSelector"/>.
/// <see cref="RotationWeights"/> carries one entry per <see cref="ZombossPatterns.All"/> id — enforced
/// at the load boundary, which doubles as an automatic content-consistency check: a future tenth
/// pattern would fail every existing tuning file's load until its weight is authored, rather than
/// silently rolling at weight zero.
/// </summary>
public sealed record ZombossAdaptiveTuning(
    int SchemaVersion, int Version,
    int LoseStreakThreshold,
    long CounterBiasPermille,
    int RepatternCooldownEncounters,
    int RevealDelayEncounters,
    IReadOnlyDictionary<string, long> RotationWeights);

public sealed class ZombossAdaptiveTuningRejection : Exception
{
    public ZombossAdaptiveTuningRejection(string message) : base(message) { }
}

/// <summary>⛔ Server-only host wiring (spec's own callout): the Zomboss exists on battle and
/// expedition surfaces, never the lawn — the injector never configures this.</summary>
public static class ZombossAdaptiveTuningHub
{
    static ZombossAdaptiveTuning? _tuning;

    public static void Configure(ZombossAdaptiveTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static ZombossAdaptiveTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ZombossAdaptiveTuningHub.Configure(...) has not run. zomboss-adaptive reads " +
        "data/tuning/zomboss-adaptive.v{n}.json (tunables-ssot.md) — there is no built-in default.");
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class ZombossAdaptiveTuningLoader
{
    public static ZombossAdaptiveTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ZombossAdaptiveTuningRejection("zomboss adaptive tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new ZombossAdaptiveTuningRejection($"zomboss adaptive tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;

            var weightsEl = Obj(root, "rotationWeights");
            var weights = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var prop in weightsEl.EnumerateObject())
            {
                if (prop.Name.StartsWith('_')) continue; // notes, never data
                if (!ZombossPatterns.IsKnown(prop.Name))
                    throw new ZombossAdaptiveTuningRejection(
                        $"zomboss adaptive tuning: rotationWeights has an unknown pattern id '{prop.Name}'");
                weights[prop.Name] = PositiveLongValue(prop.Value, $"rotationWeights.{prop.Name}");
            }
            foreach (var id in ZombossPatterns.All)
                if (!weights.ContainsKey(id))
                    throw new ZombossAdaptiveTuningRejection(
                        $"zomboss adaptive tuning: rotationWeights is missing required pattern id '{id}'");

            return new ZombossAdaptiveTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                LoseStreakThreshold: Int(root, "loseStreakThreshold"),
                CounterBiasPermille: PositiveLong(root, "counterBiasPermille"),
                RepatternCooldownEncounters: Int(root, "repatternCooldownEncounters"),
                RevealDelayEncounters: Int(root, "revealDelayEncounters"),
                RotationWeights: weights);
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ZombossAdaptiveTuningRejection($"zomboss adaptive tuning: missing required key '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ZombossAdaptiveTuningRejection($"zomboss adaptive tuning: missing or non-integer '{key}'");
        return v;
    }

    static long PositiveLong(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new ZombossAdaptiveTuningRejection($"zomboss adaptive tuning: missing or non-integer '{key}'");
        if (v <= 0) throw new ZombossAdaptiveTuningRejection($"zomboss adaptive tuning: '{key}' must be positive — got {v}");
        return v;
    }

    static long PositiveLongValue(JsonElement el, string context)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new ZombossAdaptiveTuningRejection($"zomboss adaptive tuning: '{context}' must be a non-negative integer");
        if (v <= 0) throw new ZombossAdaptiveTuningRejection($"zomboss adaptive tuning: '{context}' must be positive — got {v}");
        return v;
    }
}
