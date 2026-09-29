using System.Text.RegularExpressions;

namespace FusionRpg.Core.Achievements;

// Achievement + title definition registry (spec-achievement-registry.md).
// Pure grammar + closed-vocab validation. Rows are data, never consts; every
// balance number lives in data/tuning/achievement-titles.v{n}.json, identity in
// the sibling *-catalog file. Core reads no files — hosts inject.

/// <summary>Closed achievement scopes. New scopes are reviewed registry changes.</summary>
public static class AchievementScopes
{
    public const string Empire = "empire";
    public const string UniqueActor = "unique-actor";
    public static bool IsKnown(string? v) => v is Empire or UniqueActor;
}

/// <summary>Closed trigger verbs, each with a per-verb payload schema owned by the registry spec.</summary>
public static class AchievementTriggers
{
    public const string CounterReach = "counter-reach";
    public const string ThresholdCross = "threshold-cross";
    public const string CollectionComplete = "collection-complete";
    public const string TurnReach = "turn-reach";
    public const string EventSeen = "event-seen";
    public const string ChallengeFlag = "challenge-flag";
    public static bool IsKnown(string? v) => v is CounterReach or ThresholdCross
        or CollectionComplete or TurnReach or EventSeen or ChallengeFlag;
}

public static class AchievementPersistence
{
    public const string Permanent = "permanent";
    public const string Ladder = "ladder";
    public const string TurnWindow = "turn-window";
    public static bool IsKnown(string? v) => v is Permanent or Ladder or TurnWindow;
}

/// <summary>Which world/season identity folds into the unlock dedupe key. Default never.</summary>
public static class AchievementReearn
{
    public const string Never = "never";
    public const string World = "world";
    public const string Season = "season";
    public static bool IsKnown(string? v) => v is Never or World or Season;
}

public static class AchievementVisibility
{
    public const string Revealed = "revealed";
    public const string Hidden = "hidden";
    public const string Vague = "vague";
    public static bool IsKnown(string? v) => v is Revealed or Hidden or Vague;
}

/// <summary>Load rejection naming the offending row. Unknown enums throw — never a silent default.</summary>
public sealed class RegistryLoadException : Exception
{
    public RegistryLoadException(string message) : base(message) { }
}

public static class AchievementId
{
    // Closed prefix, bounded length, no leading/trailing dash, no tier suffix in the id.
    static readonly Regex Grammar = new(@"^achievement\.[a-z0-9-]{1,64}$", RegexOptions.Compiled);
    static readonly Regex BundleGrammar = new(@"^bundle\.[a-z0-9-]{1,64}$", RegexOptions.Compiled);
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || !Grammar.IsMatch(id)) return false;
        var tail = id["achievement.".Length..];
        return !(tail.StartsWith('-') || tail.EndsWith('-') || tail.Contains("--", StringComparison.Ordinal));
    }
    public static bool IsValidBundleRef(string? id) =>
        !string.IsNullOrWhiteSpace(id) && BundleGrammar.IsMatch(id);
}

/// <summary>One authored definition row (Seedsmith-compatible JSON shape).</summary>
public sealed record AchievementDefinition(
    string DefId,
    int Revision,
    string Scope,
    string Trigger,
    string TriggerPayloadJson,
    string Persistence,
    string ReearnScope,
    string Visibility,
    int Tier,
    string BundleRef,
    string SinkStock,
    string SinkReason,
    string? MemberSetId,
    string? MetaForSetId);

/// <summary>Pure row validator. Throws <see cref="RegistryLoadException"/> naming the row.</summary>
public static class AchievementRegistryValidator
{
    public static void Validate(AchievementDefinition def)
    {
        if (!AchievementId.IsValid(def.DefId))
            throw new RegistryLoadException($"achievement id {def.DefId}: IdMismatch");
        if (!AchievementScopes.IsKnown(def.Scope))
            throw new RegistryLoadException($"achievement id {def.DefId}: UnknownScope {def.Scope}");
        if (!AchievementTriggers.IsKnown(def.Trigger))
            throw new RegistryLoadException($"achievement id {def.DefId}: UnknownTrigger {def.Trigger}");
        if (!AchievementPersistence.IsKnown(def.Persistence))
            throw new RegistryLoadException($"achievement id {def.DefId}: UnknownPersistence {def.Persistence}");
        if (!AchievementReearn.IsKnown(def.ReearnScope))
            throw new RegistryLoadException($"achievement id {def.DefId}: UnknownReearnScope {def.ReearnScope}");
        if (!AchievementVisibility.IsKnown(def.Visibility))
            throw new RegistryLoadException($"achievement id {def.DefId}: UnknownVisibility {def.Visibility}");
        if (def.Revision < 1)
            throw new RegistryLoadException($"achievement id {def.DefId}: RevisionMustBePositive");
        if (def.Tier < 0)
            throw new RegistryLoadException($"achievement id {def.DefId}: TierMustBeNonNegative");
        if (!AchievementId.IsValidBundleRef(def.BundleRef))
            throw new RegistryLoadException($"achievement id {def.DefId}: MissingBundleRef");
        // counter-reach carries its threshold in the payload; threshold-cross reads content Θ.
        if (def.Trigger == AchievementTriggers.CounterReach && TriggerNeed(def) < 1)
            throw new RegistryLoadException($"achievement id {def.DefId}: MissingNeed");
        // Economy P1: every faucet names its sink in the same definition.
        if (string.IsNullOrWhiteSpace(def.SinkStock) || string.IsNullOrWhiteSpace(def.SinkReason))
            throw new RegistryLoadException($"achievement id {def.DefId}: MissingSink");
        // Loam never banks: a world/season-scoped bundle must not sink loam into a dying map.
        if (def.ReearnScope != AchievementReearn.Never &&
            string.Equals(def.SinkStock, "loam", StringComparison.Ordinal))
            throw new RegistryLoadException($"achievement id {def.DefId}: LoamInWorldBundle");
    }

    /// <summary>Parses {"need":N} for counter-reach. Returns 0 when absent/unparseable.</summary>
    public static long TriggerNeed(AchievementDefinition def)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(def.TriggerPayloadJson) ? "{}" : def.TriggerPayloadJson);
            return doc.RootElement.TryGetProperty("need", out var n) &&
                n.ValueKind == System.Text.Json.JsonValueKind.Number &&
                n.TryGetInt64(out var v) ? v : 0;
        }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    /// <summary>
    /// Set→meta DAG guard: a definition carrying both MemberSetId and MetaForSetId
    /// contributes edge MemberSet→MetaSet. Cycles reject naming the sets.
    /// </summary>
    public static void ValidateSetGraph(IEnumerable<AchievementDefinition> defs)
    {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            if (d.MemberSetId is null || d.MetaForSetId is null) continue;
            if (!edges.TryGetValue(d.MemberSetId, out var outs))
                edges[d.MemberSetId] = outs = new HashSet<string>(StringComparer.Ordinal);
            outs.Add(d.MetaForSetId);
        }
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in edges.Keys)
            Visit(n);
        return;
        void Visit(string n)
        {
            if (state.TryGetValue(n, out var s))
            {
                if (s == 1) throw new RegistryLoadException($"achievement set graph: CycleAt {n}");
                return;
            }
            state[n] = 1;
            if (edges.TryGetValue(n, out var outs))
                foreach (var m in outs) Visit(m);
            state[n] = 2;
        }
    }
}

/// <summary>Cold evaluation input: durable fact evidence, never live state.</summary>
public sealed record AchievementEvidence(
    long PlayerId,
    string ScopeKey,
    long? FactId,
    long Count,
    long MaxFactId,
    string? WorldId,
    string? SeasonId,
    long Turn,
    string T);
