using System.Text.Json;

namespace FusionRpg.Core.Actions;

/// <summary>One action category's timing bases (spec-action-timing.md §2.2).</summary>
public readonly record struct ActionTimingCategoryTuning(long TimeCostBaseTicks, long CooldownBaseTicks);

public sealed class ActionTimingTuningRejection : Exception
{
    public ActionTimingTuningRejection(string message) : base(message) { }
}

/// <summary>The basic attack's own token wind-up/recovery — exempt from the power formula
/// (spec §2.2b: it has no rung and no seeded power).</summary>
public readonly record struct BasicAttackTimingTuning(long WindupTicks, long RecoveryTicks);

/// <summary>Every action-timing balance number (spec-action-timing.md §2.3): the wind-up/recovery
/// power coefficients, the relative wind-up cap, per-category time-cost/cooldown bases, and the
/// basic attack's token. Read at catalog build (`RpgStore.BuildActionCatalog`, D2) — never by the
/// seeder.</summary>
public sealed record ActionTimingTuning(
    long WindupPerPowerMilli,
    long WindupCapReferenceMilli,
    long RecoveryPerPowerMilli,
    BasicAttackTimingTuning BasicAttack,
    IReadOnlyDictionary<ActionCategory, ActionTimingCategoryTuning> Categories)
{
    /// <summary>windupCapTicks = windupCapReferenceMilli × roundDurationMs / 1000 — RELATIVE to the
    /// round (D1, action-ideal.md decision #10), never an absolute literal. Widened before
    /// multiplying, divided by 1000 last (docs/architecture/numeric-types.md numeric overflow).</summary>
    public long WindupCapTicks(long roundDurationMs) =>
        checked(WindupCapReferenceMilli * roundDurationMs) / 1000;

    /// <summary>Refuses rather than defaults: a category the compiler reaches but this file forgot is
    /// a missing balance row, not a request for a built-in fallback (same discipline
    /// `BattleTuning.ProfileOf` already uses).</summary>
    public ActionTimingCategoryTuning CategoryOf(ActionCategory category) =>
        Categories.TryGetValue(category, out var c) ? c : throw new ActionTimingTuningRejection(
            $"action-timing tuning: no categories entry for '{category}'. Every ActionCategory value " +
            "must carry its magnitudes in config — there is no built-in default to fall back to.");
}

/// <summary>Pure parser over `gk-core/data/tuning/action-timing.v1.json` — no file I/O (tunables-ssot.md §7.2:
/// "Core never reads a file. Hosts load and inject."). A missing key is a REJECTION NAMING IT, never
/// a default — a silent default here would make an unauthored category resolve to an instantaneous
/// action, which is exactly the state this module exists to end.</summary>
public static class ActionTimingTuningLoader
{
    static readonly IReadOnlyDictionary<ActionCategory, string> CategoryKeys = new Dictionary<ActionCategory, string>
    {
        [ActionCategory.Attack] = "attack",
        [ActionCategory.Defense] = "defense",
        [ActionCategory.Support] = "support",
        [ActionCategory.Movement] = "movement",
        [ActionCategory.Status] = "status",
    };

    public static ActionTimingTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ActionTimingTuningRejection("action-timing tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ActionTimingTuningRejection($"action-timing tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;

            var windupPerPowerMilli = Long(root, "windupPerPowerMilli");
            var windupCapReferenceMilli = Long(root, "windupCapReferenceMilli");
            var recoveryPerPowerMilli = Long(root, "recoveryPerPowerMilli");
            if (windupPerPowerMilli < 0)
                throw new ActionTimingTuningRejection($"action-timing tuning: windupPerPowerMilli must be >= 0; got {windupPerPowerMilli}");
            if (windupCapReferenceMilli <= 0)
                throw new ActionTimingTuningRejection($"action-timing tuning: windupCapReferenceMilli must be > 0 (it bounds every action's telegraph); got {windupCapReferenceMilli}");
            if (recoveryPerPowerMilli < 0)
                throw new ActionTimingTuningRejection($"action-timing tuning: recoveryPerPowerMilli must be >= 0; got {recoveryPerPowerMilli}");

            var basicAttackEl = Obj(root, "basicAttack");
            var basicAttackWindup = Long(basicAttackEl, "windupTicks");
            var basicAttackRecovery = Long(basicAttackEl, "recoveryTicks");
            if (basicAttackWindup <= 0)
                throw new ActionTimingTuningRejection($"action-timing tuning: basicAttack.windupTicks must be > 0 (decision 11: a felt beat, not zero); got {basicAttackWindup}");
            if (basicAttackRecovery < 0)
                throw new ActionTimingTuningRejection($"action-timing tuning: basicAttack.recoveryTicks must be >= 0; got {basicAttackRecovery}");

            var categoriesEl = Obj(root, "categories");
            var categories = new Dictionary<ActionCategory, ActionTimingCategoryTuning>();
            foreach (var (category, key) in CategoryKeys)
            {
                if (!categoriesEl.TryGetProperty(key, out var catEl) || catEl.ValueKind != JsonValueKind.Object)
                    throw new ActionTimingTuningRejection($"action-timing tuning: missing categories.{key}");

                var timeCost = Long(catEl, "timeCostBaseTicks");
                var cooldown = Long(catEl, "cooldownBaseTicks");
                if (timeCost <= 0)
                    throw new ActionTimingTuningRejection($"action-timing tuning: categories.{key}.timeCostBaseTicks must be > 0 (feeds readiness — zero schedules an event at 'now' forever); got {timeCost}");
                if (cooldown < 0)
                    throw new ActionTimingTuningRejection($"action-timing tuning: categories.{key}.cooldownBaseTicks must be >= 0; got {cooldown}");

                categories[category] = new ActionTimingCategoryTuning(timeCost, cooldown);
            }

            return new ActionTimingTuning(
                windupPerPowerMilli, windupCapReferenceMilli, recoveryPerPowerMilli,
                new BasicAttackTimingTuning(basicAttackWindup, basicAttackRecovery),
                categories);
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ActionTimingTuningRejection($"action-timing tuning: missing or non-object '{key}'");
        return el;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new ActionTimingTuningRejection($"action-timing tuning: missing or non-integer '{key}'");
        return v;
    }
}
