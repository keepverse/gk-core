using System.Text.Json;

namespace FusionRpg.Core.Stats.Derived;

/// <summary>Stats balance surface (tunables-ssot.md T1) — loaded, not hard-coded. Covers
/// <see cref="StatChannels.MinimumInterval"/> plus <see cref="ElementMatchupPolicy"/> and
/// <see cref="CombatProbabilityPolicy"/>, which share one <c>stats.v{n}.json</c>. See
/// <see cref="StatsTuningHub.Configure"/> and <see cref="StatsTuningLoader"/>.</summary>
public sealed record StatsTuning(
    int SchemaVersion, int Version,
    double MinimumInterval, double MatchupShareK,
    double AccuracyScale, double CritRateScale, double CritDamageScale, double Steepness);

public sealed class StatsTuningRejection : Exception
{
    public StatsTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class StatsTuningLoader
{
    public static StatsTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new StatsTuningRejection("stats tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new StatsTuningRejection($"stats tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new StatsTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                MinimumInterval: Dbl(root, "minimumInterval"),
                MatchupShareK: Dbl(root, "matchupShareK"),
                AccuracyScale: Dbl(root, "accuracyScale"),
                CritRateScale: Dbl(root, "critRateScale"),
                CritDamageScale: Dbl(root, "critDamageScale"),
                Steepness: Dbl(root, "steepness"));
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new StatsTuningRejection($"stats tuning: missing or non-integer '$.{key}'");
        return v;
    }

    static double Dbl(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new StatsTuningRejection($"stats tuning: missing or non-numeric '$.{key}'");
        return el.GetDouble();
    }
}

/// <summary>Single configuration point covering <see cref="StatChannels"/>,
/// <see cref="ElementMatchupPolicy"/> and <see cref="CombatProbabilityPolicy"/>.</summary>
public static class StatsTuningHub
{
    static StatsTuning? _tuning;

    public static void Configure(StatsTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static StatsTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "StatsTuningHub.Configure(...) has not run. Every stats/combat-probability rule reads " +
        "data/tuning/stats.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
