using System.Text.Json;

namespace FusionRpg.Core.Battle.Timeline;

/// <summary>`battle-tempo` `reaction-lane` RL2's own balance surface (tunables-ssot.md T1): the
/// counter's poise spend and the riposte share it converts at — <see cref="ReactionCounter.TryCounter"/>'s
/// two magnitudes. Deliberately a SEPARATE file from `battle.v{n}.json` rather than a new required
/// section grafted onto it — `BattleTuningLoader.Parse` rejects any file missing a section it expects,
/// so adding one there would break every existing fixture that predates this module (the same
/// "new capability, new dedicated file" precedent `action-timing.v1.json` already set).</summary>
public sealed record ReactionLaneTuning(int SchemaVersion, int Version, long PoiseSpend, int RiposteShareCapMilli);

public sealed class ReactionLaneTuningRejection : Exception
{
    public ReactionLaneTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser over `gk-core/data/tuning/reaction-lane.v1.json` — no file I/O (tunables-ssot.md §7.2).
/// A missing key is a REJECTION naming it, never a silent default, matching every other tuning loader
/// in this program.</summary>
public static class ReactionLaneTuningLoader
{
    public static ReactionLaneTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ReactionLaneTuningRejection("reaction-lane tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ReactionLaneTuningRejection($"reaction-lane tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var poiseSpend = Long(root, "poiseSpend");
            if (poiseSpend < 0)
                throw new ReactionLaneTuningRejection($"reaction-lane tuning: poiseSpend must be >= 0; got {poiseSpend}");

            var riposteShareCapMilli = Int(root, "riposteShareCapMilli");
            if (riposteShareCapMilli < 0 || riposteShareCapMilli > 1000)
                throw new ReactionLaneTuningRejection(
                    $"reaction-lane tuning: riposteShareCapMilli must be within 0..1000 (a bounded ratio, " +
                    $"PS-8 exempt — see Riposte.cs's own comment); got {riposteShareCapMilli}");

            return new ReactionLaneTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                PoiseSpend: poiseSpend,
                RiposteShareCapMilli: riposteShareCapMilli);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ReactionLaneTuningRejection($"reaction-lane tuning: missing or non-integer '{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new ReactionLaneTuningRejection($"reaction-lane tuning: missing or non-integer '{key}'");
        return v;
    }
}

/// <summary>Config-backed reaction-lane tuning, mirroring <see cref="Actions.ActionTimingPolicy"/>'s
/// own shape exactly — a static holder configured once at host startup, read explicitly wherever a
/// counter is declared.</summary>
public static class ReactionLanePolicy
{
    static ReactionLaneTuning? _tuning;

    public static void Configure(ReactionLaneTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static ReactionLaneTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ReactionLanePolicy.Configure(...) has not run. A counter's spend/share reads " +
        "data/tuning/reaction-lane.v{n}.json — there is no built-in default to fall back to.");
}
