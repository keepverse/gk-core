using System.Text.Json;
using FusionRpg.Core.Commanders;

namespace FusionRpg.Core.World.Ai;

public sealed record FrontierRulesTuning(
    int RecoverAtMilli,
    int ExploreTurns,
    long SeveranceThresholdCost,
    /// <summary>
    /// Momentum, as hysteresis (spec-ai-commander.md §Momentum, amended 2026-08-31). A rule that
    /// would send an entity somewhere other than where this faction's own last order sent it must
    /// beat the standing choice by this margin, in per-mille of the standing score.
    ///
    /// <para>Hysteresis rather than a bonus because <c>FrontierRulesPolicy</c> is a rule ladder, not
    /// a utility scorer: there is no single score to add a bonus to, and the observed oscillation is
    /// a feedback loop between two rules rather than a near-tie inside one.</para>
    /// </summary>
    int MomentumMarginMilli);

public sealed record ThreatMapTuning(int StaleDecayPerTurn, int MaxSpreadHops, int ProximityFalloffPerHop);

public sealed record ValueWeightsTuning(int Yield, int Strategic, int Defensibility, int Cost, int Risk, int Curiosity);  // overflow-bounded: a per-mille desirability weight/score, 0..1000 ('only the ratios matter'), not a loam quantity

public sealed record ValueMapTuning(
    int OptimismMilli, int OverextensionPenaltyMilli, int HabitabilityPenaltyMilli, ValueWeightsTuning DefaultWeights);

/// <summary>
/// `ai-build-scorer` EP5.2 — the <c>buildScorer</c> block: the scorer's (EP5.1) tunables as GLOBAL
/// DEFAULTS plus per-empire OVERRIDES keyed by <see cref="EmpireId"/>.Value (the tunables-ssot shape for
/// a per-empire tunable, the ideal's Zubek note: global values, per-empire overrides).
///
/// <para><b>Overrides are partial by design.</b> An override lists only the keys it changes; every key it
/// omits reads the block's own <see cref="Defaults"/>, so a per-empire entry states a difference rather
/// than a second complete copy of the block. <see cref="Defaults"/> itself must be COMPLETE — a missing
/// key there is a load rejection, never a code default (tunables-ssot.md T5).</para>
/// </summary>
public sealed record BuildScorerTuningBlock(
    BuildScorerTuning Defaults,
    IReadOnlyDictionary<string, BuildScorerTuning> ByEmpire)
{
    /// <summary>The empire's own override when it has one, else the block's global defaults. An empire
    /// with no entry is the normal case, not a fallback: an empty map is a legal published block.</summary>
    public BuildScorerTuning For(EmpireId empire) =>
        ByEmpire.TryGetValue(empire.Value, out var tuning) ? tuning : Defaults;
}

/// <summary>AI-commander balance surface (tunables-ssot.md T1) — spec-ai-commander.md.</summary>
public sealed record WorldAiTuning(
    int SchemaVersion, int Version,
    FrontierRulesTuning FrontierRules, ThreatMapTuning ThreatMap, ValueMapTuning ValueMap,
    BuildScorerTuningBlock BuildScorer);

public sealed class WorldAiTuningRejection : Exception
{
    public WorldAiTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class WorldAiTuningLoader
{
    public static WorldAiTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new WorldAiTuningRejection("ai tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new WorldAiTuningRejection($"ai tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var frontier = Obj(root, "frontierRules");
            var threat = Obj(root, "threatMap");
            var value = Obj(root, "valueMap");
            var weightsEl = Obj(value, "defaultWeights");
            // `ai-build-scorer` EP5.2: REQUIRED, both halves of the block. A document without it is a
            // load rejection rather than a scorer running on a code-invented default — the same rule
            // every other key in this file already follows, and the reason the file gained a version.
            var buildScorer = Obj(root, "buildScorer");
            var buildDefaults = Obj(buildScorer, "defaults");
            var byEmpireEl = Obj(buildScorer, "byEmpire");
            var overrides = new Dictionary<string, BuildScorerTuning>(StringComparer.Ordinal);
            foreach (var empire in byEmpireEl.EnumerateObject())
            {
                if (empire.Value.ValueKind != JsonValueKind.Object)
                    throw new WorldAiTuningRejection(
                        $"ai tuning: 'buildScorer.byEmpire.{empire.Name}' is not an object");
                overrides[empire.Name] = BuildScorerTuning(
                    buildDefaults, empire.Value, $"buildScorer.byEmpire.{empire.Name}");
            }

            return new WorldAiTuning(
                SchemaVersion: Int(root, "schemaVersion", "$"),
                Version: Int(root, "version", "$"),
                FrontierRules: new FrontierRulesTuning(
                    RecoverAtMilli: Int(frontier, "recoverAtMilli", "frontierRules"),
                    ExploreTurns: Int(frontier, "exploreTurns", "frontierRules"),
                    SeveranceThresholdCost: Long(frontier, "severanceThresholdCost", "frontierRules"),
                    // A missing tunable is a load rejection naming it, never a default
                    // (tunables-ssot.md): a silent default would make a mis-authored config
                    // indistinguishable from a deliberate one.
                    MomentumMarginMilli: Int(frontier, "momentumMarginMilli", "frontierRules")),
                ThreatMap: new ThreatMapTuning(
                    StaleDecayPerTurn: Int(threat, "staleDecayPerTurn", "threatMap"),
                    MaxSpreadHops: Int(threat, "maxSpreadHops", "threatMap"),
                    ProximityFalloffPerHop: Int(threat, "proximityFalloffPerHop", "threatMap")),
                ValueMap: new ValueMapTuning(
                    OptimismMilli: Int(value, "optimismMilli", "valueMap"),
                    OverextensionPenaltyMilli: Int(value, "overextensionPenaltyMilli", "valueMap"),
                    HabitabilityPenaltyMilli: Int(value, "habitabilityPenaltyMilli", "valueMap"),
                    DefaultWeights: new ValueWeightsTuning(
                        Yield: Int(weightsEl, "yield", "valueMap.defaultWeights"),
                        Strategic: Int(weightsEl, "strategic", "valueMap.defaultWeights"),
                        Defensibility: Int(weightsEl, "defensibility", "valueMap.defaultWeights"),
                        Cost: Int(weightsEl, "cost", "valueMap.defaultWeights"),
                        Risk: Int(weightsEl, "risk", "valueMap.defaultWeights"),
                        Curiosity: Int(weightsEl, "curiosity", "valueMap.defaultWeights"))),
                BuildScorer: new BuildScorerTuningBlock(
                    Defaults: BuildScorerTuning(buildDefaults, buildDefaults, "buildScorer.defaults"),
                    ByEmpire: overrides));
        }
    }

    /// <summary>
    /// `ai-build-scorer` EP5.2 — one tuning set, read from <paramref name="overrides"/> first and from
    /// <paramref name="defaults"/> for every key the override does not name. Curves are name-parsed, not
    /// integer-parsed: the document names a curve from the closed <see cref="Utility.ResponseCurve"/>
    /// vocabulary, so an unknown or misspelled id is a load rejection naming it rather than a silent
    /// numeric coercion.
    /// </summary>
    static BuildScorerTuning BuildScorerTuning(JsonElement defaults, JsonElement overrides, string path) => new(
        NeedFitCurve: Curve(overrides, defaults, "needFitCurve", path),
        NeedFitThreshold: Int(overrides, defaults, "needFitThreshold", path),
        CounterFitCurve: Curve(overrides, defaults, "counterFitCurve", path),
        CounterFitThreshold: Int(overrides, defaults, "counterFitThreshold", path),
        ContinuityCooldownTurns: Int(overrides, defaults, "continuityCooldownTurns", path));

    /// <summary>An override's key when it names it, else the defaults' — so a per-empire entry is a
    /// difference, and a key missing from BOTH is the rejection below.</summary>
    static int Int(JsonElement overrides, JsonElement defaults, string key, string path)
    {
        if (TryInt(overrides, key, out var value) || TryInt(defaults, key, out value)) return value;
        throw new WorldAiTuningRejection(
            $"ai tuning: missing or non-integer '{path}.{key}' (and no 'buildScorer.defaults.{key}' to inherit)");
    }

    static Utility.ResponseCurve Curve(JsonElement overrides, JsonElement defaults, string key, string path)
    {
        if (TryCurve(overrides, key, out var curve) || TryCurve(defaults, key, out curve)) return curve;

        // "Absent" and "present but not a curve name" are different mistakes, and the second is a typo:
        // the message names the offending value so nobody has to diff the vocabulary by eye.
        var raw = RawString(overrides, key) ?? RawString(defaults, key);
        var attempted = raw is null ? string.Empty : $" '{raw}' is not one of them;";
        throw new WorldAiTuningRejection(
            $"ai tuning: missing or unknown curve '{path}.{key}' — expected one of: "
            + string.Join(", ", Enum.GetNames<Utility.ResponseCurve>())
            + ";" + attempted + $" no 'buildScorer.defaults.{key}' to inherit either");
    }

    static string? RawString(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(key, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    static bool TryInt(JsonElement parent, string key, out int value)
    {
        value = 0;
        return parent.TryGetProperty(key, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    static bool TryCurve(JsonElement parent, string key, out Utility.ResponseCurve curve)
    {
        curve = default;
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String) return false;
        // Ordinal NAME match against the closed vocabulary: `Enum.TryParse` would also accept "1"/"Linear ",
        // and a numeric spelling is exactly the silent coercion this file refuses everywhere else.
        var name = el.GetString();
        var index = Array.IndexOf(Enum.GetNames<Utility.ResponseCurve>(), name);
        if (index < 0) return false;
        curve = Enum.Parse<Utility.ResponseCurve>(name!);
        return true;
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new WorldAiTuningRejection($"ai tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new WorldAiTuningRejection($"ai tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new WorldAiTuningRejection($"ai tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}

/// <summary>Holds one ai.v{n}.json load for FrontierRulesPolicy/ThreatMap/ValueMap (tunables-ssot.md §7.2).</summary>
public static class WorldAiPolicy
{
    static WorldAiTuning? _tuning;

    public static void Configure(WorldAiTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static WorldAiTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "WorldAiPolicy.Configure(...) has not run. The AI commander reads data/tuning/ai.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static ValueWeights DefaultWeights => new()
    {
        Yield = Tuning.ValueMap.DefaultWeights.Yield,
        Strategic = Tuning.ValueMap.DefaultWeights.Strategic,
        Defensibility = Tuning.ValueMap.DefaultWeights.Defensibility,
        Cost = Tuning.ValueMap.DefaultWeights.Cost,
        Risk = Tuning.ValueMap.DefaultWeights.Risk,
        Curiosity = Tuning.ValueMap.DefaultWeights.Curiosity
    };
}
