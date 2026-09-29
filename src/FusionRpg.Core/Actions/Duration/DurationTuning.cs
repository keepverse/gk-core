using System.Text.Json;

namespace FusionRpg.Core.Actions.Duration;

public sealed class DurationTuningRejection : Exception
{
    public DurationTuningRejection(string message) : base(message) { }
}

/// <summary>
/// The clamp-and-convert dials (spec-duration-resolver.md §3). Both are explicit placeholders —
/// same posture as T20's <c>DiscardTaxCoeffMilli</c>: the RULE (a bound exists, excess redirects to
/// intensity) is decided; the exact numbers are not, and are rebalanced from real play data later.
/// </summary>
/// <param name="MaxVictimTurns">How many of the victim's OWN turns a single control effect may steal
/// at most — a bounded ratio (PS-8 exempt, spec §1's own table says so), never a magnitude ceiling.</param>
/// <param name="IntensityPerExcessTurnMilli">Per-mille of <c>status.intensity.*</c> redirected for
/// every full excess turn beyond <see cref="MaxVictimTurns"/> — the "soft" half of the soft cap.</param>
public sealed record DurationTuning(int MaxVictimTurns, int IntensityPerExcessTurnMilli);

/// <summary>Pure parser for `data/tuning/action-duration.v{n}.json` (tunables-ssot.md §7.2 — no file
/// I/O here).</summary>
public static class DurationTuningLoader
{
    public static DurationTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DurationTuningRejection("action duration tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new DurationTuningRejection($"action duration tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var maxVictimTurns = Int(root, "maxVictimTurns");
            var intensityPerExcessTurnMilli = Int(root, "intensityPerExcessTurnMilli");

            if (maxVictimTurns <= 0)
                throw new DurationTuningRejection(
                    "action duration tuning: maxVictimTurns must be > 0 (PS-8 — a zero bound is a hard " +
                    "lock at zero turns, not a soft cap)");
            if (intensityPerExcessTurnMilli <= 0)
                throw new DurationTuningRejection(
                    "action duration tuning: intensityPerExcessTurnMilli must be > 0 — zero would make " +
                    "the excess vanish instead of redirecting, which is the hard-clamp defect §3 forbids");

            return new DurationTuning(maxVictimTurns, intensityPerExcessTurnMilli);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new DurationTuningRejection($"action duration tuning: missing or non-integer '{key}'");
        return v;
    }
}
