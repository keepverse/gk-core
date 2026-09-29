using System.Text.Json;

namespace FusionRpg.Core.Match;

/// <summary>Match RAM-cap balance surface (tunables-ssot.md T1). See <see cref="CapPolicyConfig.Defaults"/>.
/// <see cref="WaveHoldFloorSeconds"/> is a second, unrelated balance surface riding the same file —
/// E36 (spec-wave-control.md §2.2): the wave-timer floor `wave.control`'s `hold` op applies every
/// tick, moved out of `CheatActions.cs`'s own bare <c>30f</c> literal (tunables-ssot.md T1 — a
/// balance pass would tune this floor).</summary>
public sealed record MatchTuning(
    int SchemaVersion, int Version, int MaxLivingPlants, int MaxLivingZombies,
    double WaveHoldFloorSeconds);

public sealed class MatchTuningRejection : Exception
{
    public MatchTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class MatchTuningLoader
{
    public static MatchTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new MatchTuningRejection("match tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new MatchTuningRejection($"match tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new MatchTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                MaxLivingPlants: Int(root, "maxLivingPlants"),
                MaxLivingZombies: Int(root, "maxLivingZombies"),
                // E36 (spec-wave-control.md §2.2): seconds, not per-mille/ms — this is the same unit
                // Board.timeUntilNextWave already carries (a Unity float), never a magnitude in the
                // docs/architecture/numeric-types.md overflow-table sense, so no long/per-mille discipline applies here.
                WaveHoldFloorSeconds: Double(root, "waveHoldFloorSeconds"));
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new MatchTuningRejection($"match tuning: missing or non-integer '{key}'");
        return v;
    }

    static double Double(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v))
            throw new MatchTuningRejection($"match tuning: missing or non-numeric '{key}'");
        return v;
    }
}
