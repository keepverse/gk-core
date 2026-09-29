using System.Text.Json;

namespace FusionRpg.Core.Actions;

/// <summary>The action base's own balance number (spec-action-base.md, Tunables): the basic
/// attack's base power in per-mille of <c>PowerMath.One</c>. A skill or innate never reads this —
/// its base is its holder's effective rung row <c>QPowerMilli</c>.</summary>
public sealed record ActionBaseTuning(long BasicAttackBasePowerMilli);

public sealed class ActionBaseTuningRejection : Exception
{
    public ActionBaseTuningRejection(string message) : base(message) { }
}

/// <summary>Process-wide holder, matching <c>LawnAttritionTuningHub</c>'s own plain-holder shape:
/// configured once at host startup, throws when unconfigured, no built-in default.</summary>
public static class ActionBaseTuningHub
{
    static ActionBaseTuning? _tuning;

    public static void Configure(ActionBaseTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static ActionBaseTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ActionBaseTuningHub.Configure(...) has not run. Read data/tuning/action-base.v{n}.json at " +
        "startup — there is no built-in default to fall back to.");

    public static bool IsConfigured => _tuning != null;

    /// <summary>Tests only.</summary>
    public static void Reset() => _tuning = null;
}

/// <summary>Pure parser over `data/tuning/action-base.v{n}.json` — no file I/O (tunables-ssot.md §7.2:
/// "Core never reads a file. Hosts load and inject."). A missing key is a rejection naming it,
/// never a default.</summary>
public static class ActionBaseTuningLoader
{
    public static ActionBaseTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ActionBaseTuningRejection("action-base tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ActionBaseTuningRejection($"action-base tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("basicAttack", out var basic) || basic.ValueKind != JsonValueKind.Object)
                throw new ActionBaseTuningRejection("action-base tuning: missing or non-object 'basicAttack'");
            if (!basic.TryGetProperty("basePowerMilli", out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
                throw new ActionBaseTuningRejection("action-base tuning: missing or non-integer 'basicAttack.basePowerMilli'");
            if (v <= 0)
                throw new ActionBaseTuningRejection($"action-base tuning: basicAttack.basePowerMilli must be > 0; got {v}");
            return new ActionBaseTuning(v);
        }
    }
}
