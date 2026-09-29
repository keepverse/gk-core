using System.Text.Json;

namespace FusionRpg.Core.BuildPresets;

/// <summary>
/// build-preset BP1.10 (spec-preset-store.md "Tunables") — the build-preset library's own knobs, in
/// the first version of a new tuning domain (<c>data/tuning/build-preset.v{n}.json</c>). The first
/// version of a new domain is authored with its module; every later change is <c>v{n+1}</c> through
/// <c>gk-core/tools/tuning/publish.py</c> (tunables-ssot T4), never an in-place edit.
/// </summary>
public sealed record BuildPresetTuning(
    int SchemaVersion,
    int Version,
    /// <summary>
    /// Soft max build presets per player — create refuses past this with <c>build-preset.softMax</c>.
    /// This is a <b>list-length soft refusal</b>, not a §11 progression cap: it bounds how many presets
    /// a player may keep, never a magnitude the ladder produces, and a later revision may raise it.
    /// </summary>
    long SoftMaxBuildPresets);

public sealed class BuildPresetTuningRejection : Exception
{
    public BuildPresetTuningRejection(string message) : base(message) { }
}

public static class BuildPresetTuningHub
{
    static BuildPresetTuning? _tuning;

    public static void Configure(BuildPresetTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>The same best-effort branch <c>AptitudePresetTuningHub.IsConfigured</c> gives its own
    /// callers: a real server configures this at boot, but a fixture reached indirectly may not, and a
    /// hard throw there would take down an unrelated caller's whole resolve.</summary>
    public static bool IsConfigured => _tuning is not null;

    public static BuildPresetTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "BuildPresetTuningHub.Configure(...) has not run. The build-preset library's soft max reads " +
        "data/tuning/build-preset.v{n}.json (tunables-ssot.md §7.2) — there is no built-in default.");
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class BuildPresetTuningLoader
{
    public static BuildPresetTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new BuildPresetTuningRejection("build-preset tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new BuildPresetTuningRejection($"build-preset tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var softMax = Long(root, "softMaxBuildPresets");
            if (softMax < 1)
                throw new BuildPresetTuningRejection("build-preset tuning: softMaxBuildPresets must be >= 1");
            return new BuildPresetTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                SoftMaxBuildPresets: softMax);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new BuildPresetTuningRejection($"build-preset tuning: missing or non-int '{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new BuildPresetTuningRejection($"build-preset tuning: missing or non-long '{key}'");
        return v;
    }
}
