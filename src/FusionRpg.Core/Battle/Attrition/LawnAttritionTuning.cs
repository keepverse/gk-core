using System.Text.Json;

namespace FusionRpg.Core.Battle.Attrition;

/// <summary>
/// The lawn's attrition balance surface (`gk-core/data/tuning/lawn-attrition.v2.json`) — loaded, never
/// hard-coded (tunables-ssot).
/// </summary>
/// <param name="InjuryFloorMilli">Damage taken, as per-mille of max HP, below which nothing is risked.</param>
/// <param name="InjuryChanceAtFullMilli">Injury chance at a full 1000‰ of max HP taken.</param>
/// <param name="PermadeathFloorMilli">The same floor for permanent death — higher, so a member risks
/// a life only after risking a limb.</param>
/// <param name="PermadeathChanceAtFullMilli">Permanent-death chance at a full 1000‰ taken.</param>
public sealed record LawnAttritionTuning(
    int SchemaVersion,
    int Version,
    int InjuryFloorMilli,
    int InjuryChanceAtFullMilli,
    int PermadeathFloorMilli,
    int PermadeathChanceAtFullMilli);

public sealed class LawnAttritionTuningRejection : Exception
{
    public LawnAttritionTuningRejection(string message) : base(message) { }
}

/// <summary>Process-wide holder, matching <c>LawnDeployEventsTuningHub</c>'s own plain-holder shape.
/// The ladder itself stays a pure function of the tuning it is handed.</summary>
public static class LawnAttritionTuningHub
{
    static LawnAttritionTuning? _tuning;

    public static void Configure(LawnAttritionTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static LawnAttritionTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "LawnAttritionTuningHub.Configure(...) has not run. Read data/tuning/lawn-attrition.v2.json at " +
        "startup — there is no built-in default to fall back to.");

    public static bool IsConfigured => _tuning != null;

    /// <summary>Tests only.</summary>
    public static void Reset() => _tuning = null;
}

public static class LawnAttritionTuningLoader
{
    public static LawnAttritionTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new LawnAttritionTuningRejection("lawn-attrition tuning is empty");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int Req(string name)
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
                throw new LawnAttritionTuningRejection($"lawn-attrition tuning is missing integer '{name}'");
            return v;
        }

        var tuning = new LawnAttritionTuning(
            SchemaVersion: Req("schemaVersion"),
            Version: Req("version"),
            InjuryFloorMilli: Req("injuryFloorMilli"),
            InjuryChanceAtFullMilli: Req("injuryChanceAtFullMilli"),
            PermadeathFloorMilli: Req("permadeathFloorMilli"),
            PermadeathChanceAtFullMilli: Req("permadeathChanceAtFullMilli"));

        // Refuse a shape that could never produce the designed DIRECTION. The constants are explicitly
        // unmeasured and free to move; the direction is the design, so a file that inverted it would be
        // a content error rather than a balance choice.
        if (tuning.PermadeathFloorMilli < tuning.InjuryFloorMilli)
            throw new LawnAttritionTuningRejection(
                $"permadeathFloorMilli ({tuning.PermadeathFloorMilli}) sits below injuryFloorMilli "
                + $"({tuning.InjuryFloorMilli}) — a member must risk a limb before it risks a life");

        foreach (var (name, value) in new[]
                 {
                     ("injuryFloorMilli", tuning.InjuryFloorMilli),
                     ("injuryChanceAtFullMilli", tuning.InjuryChanceAtFullMilli),
                     ("permadeathFloorMilli", tuning.PermadeathFloorMilli),
                     ("permadeathChanceAtFullMilli", tuning.PermadeathChanceAtFullMilli),
                 })
        {
            if (value is < 0 or > 1000)
                throw new LawnAttritionTuningRejection($"'{name}' is {value}; a per-mille field is 0..1000");
        }

        return tuning;
    }
}
