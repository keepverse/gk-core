using System.Text.Json;

namespace FusionRpg.Core.Match;

/// <summary>One named case's own condition + fire chance (gk-core/data/tuning/lawn-deploy-events.v1.json).
/// <see cref="ZombieCountAtLeast"/>/<see cref="PlantCountAtMost"/> are null when the case does not gate
/// on that field — e.g. `zombie-swarm` has no plant-count gate at all, not a gate pinned to zero.</summary>
public sealed record LawnDeployEventCaseTuning(
    string CaseId, int? ZombieCountAtLeast, int? PlantCountAtMost, int FireChanceMilli);

/// <summary>Lawn-deploy trigger balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="LawnDeployEventEvaluator"/> and <see cref="LawnDeployEventsTuningLoader"/>.</summary>
public sealed record LawnDeployEventsTuning(
    int SchemaVersion, int Version, int MaxFiresPerRun, IReadOnlyList<LawnDeployEventCaseTuning> Cases);

public sealed class LawnDeployEventsTuningRejection : Exception
{
    public LawnDeployEventsTuningRejection(string message) : base(message) { }
}

/// <summary>Process-wide holder — <see cref="LawnDeployEventEvaluator.Evaluate"/> itself stays a pure
/// function taking <see cref="LawnDeployEventsTuning"/> as a parameter (mirrors
/// <see cref="Delve.Events.AmbushDraw"/>'s own convention, never a static-hub read internally); this
/// hub is only where the CALLER (T2.4's own trigger-check call site) reads the loaded value from,
/// matching <c>WorldTuningHub</c>/<c>ExpeditionTuningHub</c>'s own plain-holder shape (as opposed to a
/// `Policy` class with business-logic methods, which this domain does not need).</summary>
public static class LawnDeployEventsTuningHub
{
    static LawnDeployEventsTuning? _tuning;

    public static void Configure(LawnDeployEventsTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static LawnDeployEventsTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "LawnDeployEventsTuningHub.Configure(...) has not run. Read data/tuning/lawn-deploy-events.v1.json " +
        "at startup — there is no built-in default to fall back to.");

    public static bool IsConfigured => _tuning != null;

    /// <summary>Tests only.</summary>
    internal static void ResetForTests() => _tuning = null;
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — mirrors
/// <see cref="Creatures.Patron.PatronTuningLoader"/>'s own explicit, path-qualified-failure shape.</summary>
public static class LawnDeployEventsTuningLoader
{
    public static LawnDeployEventsTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new LawnDeployEventsTuningRejection("lawn-deploy-events tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new LawnDeployEventsTuningRejection($"lawn-deploy-events tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");
            var maxFiresPerRun = Int(root, "maxFiresPerRun", "$");
            if (maxFiresPerRun < 0)
                throw new LawnDeployEventsTuningRejection("lawn-deploy-events tuning: $.maxFiresPerRun must be >= 0");

            var casesEl = Obj(root, "cases", "$");
            var cases = new List<LawnDeployEventCaseTuning>();
            foreach (var prop in casesEl.EnumerateObject())
            {
                var caseId = prop.Name;
                var path = $"cases.{caseId}";
                var el = prop.Value;
                if (el.ValueKind != JsonValueKind.Object)
                    throw new LawnDeployEventsTuningRejection($"lawn-deploy-events tuning: '{path}' must be an object");

                var zombieAtLeast = OptionalInt(el, "zombieCountAtLeast", path);
                var plantAtMost = OptionalInt(el, "plantCountAtMost", path);
                var fireChanceMilli = Int(el, "fireChanceMilli", path);
                if (fireChanceMilli < 0 || fireChanceMilli > 1000)
                    throw new LawnDeployEventsTuningRejection($"lawn-deploy-events tuning: '{path}.fireChanceMilli' must be 0..1000");
                if (zombieAtLeast is null && plantAtMost is null)
                    throw new LawnDeployEventsTuningRejection(
                        $"lawn-deploy-events tuning: '{path}' names no condition — every case must gate on at least one field");

                cases.Add(new LawnDeployEventCaseTuning(caseId, zombieAtLeast, plantAtMost, fireChanceMilli));
            }
            if (cases.Count == 0)
                throw new LawnDeployEventsTuningRejection("lawn-deploy-events tuning: $.cases is empty — nothing could ever fire");

            return new LawnDeployEventsTuning(schemaVersion, version, maxFiresPerRun, cases);
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new LawnDeployEventsTuningRejection($"lawn-deploy-events tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new LawnDeployEventsTuningRejection($"lawn-deploy-events tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static int? OptionalInt(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new LawnDeployEventsTuningRejection($"lawn-deploy-events tuning: non-integer '{path}.{key}'");
        return v;
    }
}
