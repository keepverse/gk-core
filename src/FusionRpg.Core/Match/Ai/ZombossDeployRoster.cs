using System.Text.Json;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Match.Ai;

public sealed class ZombossDeployTuningRejection : Exception
{
    public ZombossDeployTuningRejection(string message) : base(message) { }
}

public sealed record ZombossWaveRarityCeiling(int MaxWaveAtLeast, CreatureRarity RarityCeiling);

/// <summary>T3.3's own weights — <see cref="ZombossDeployPolicy"/> never reads a bare literal for any
/// of these, matching this repo's own tunables-ssot.md rule.</summary>
public sealed record ZombossScorerTuning(
    int FireChanceMilli, int MinEnemyUnitsToConsiderDeploy, int MaxConcurrentOwnUnits);

public sealed record ZombossDeployTuning(
    int Version,
    IReadOnlyDictionary<string, string> DifficultyPolicyIds,
    IReadOnlyList<ZombossWaveRarityCeiling> WaveRarityCeilings,
    int RosterSizeMax,
    ZombossScorerTuning Scorer);

/// <summary>
/// zomboss-deploy-ai T3.2 — hand-rolled `JsonDocument` parsing, matching this repo's own established
/// convention for every tuning loader (never bare `JsonSerializer.Deserialize&lt;T&gt;`) — see
/// `LawnDeployEventsTuningLoader`'s own identical shape.
/// </summary>
public static class ZombossDeployTuningLoader
{
    public static ZombossDeployTuning Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = Int(root, "version");

        var policies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Obj(root, "difficultyPolicyIds").EnumerateObject())
            policies[p.Name] = p.Value.GetString()
                ?? throw new ZombossDeployTuningRejection($"difficultyPolicyIds.{p.Name}: expected a string policy id");
        if (policies.Count == 0)
            throw new ZombossDeployTuningRejection("difficultyPolicyIds: must name at least one difficulty");

        var ceilings = new List<ZombossWaveRarityCeiling>();
        foreach (var c in Arr(root, "waveRarityCeilings").EnumerateArray())
        {
            var maxWaveAtLeast = Int(c, "maxWaveAtLeast");
            var rarityText = Str(c, "rarityCeiling");
            if (!Enum.TryParse<CreatureRarity>(rarityText, ignoreCase: false, out var rarity))
                throw new ZombossDeployTuningRejection($"waveRarityCeilings: unknown rarityCeiling '{rarityText}'");
            ceilings.Add(new ZombossWaveRarityCeiling(maxWaveAtLeast, rarity));
        }
        if (ceilings.Count == 0)
            throw new ZombossDeployTuningRejection("waveRarityCeilings: must name at least one rung");
        // Ascending by maxWaveAtLeast so ZombossDeployRoster.RarityCeilingForWave can scan once and stop
        // at the last threshold the current wave has reached — a stale/unsorted file would silently
        // pick the wrong ceiling instead of throwing, so this is enforced here, not assumed at read time.
        for (var i = 1; i < ceilings.Count; i++)
            if (ceilings[i].MaxWaveAtLeast <= ceilings[i - 1].MaxWaveAtLeast)
                throw new ZombossDeployTuningRejection(
                    "waveRarityCeilings: maxWaveAtLeast must be strictly ascending, found " +
                    $"{ceilings[i - 1].MaxWaveAtLeast} then {ceilings[i].MaxWaveAtLeast}");

        var rosterSizeMax = Int(root, "rosterSizeMax");
        if (rosterSizeMax <= 0)
            throw new ZombossDeployTuningRejection("rosterSizeMax: must be positive");

        var scorerEl = Obj(root, "scorer");
        var fireChanceMilli = Int(scorerEl, "fireChanceMilli");
        if (fireChanceMilli < 0 || fireChanceMilli > 1000)
            throw new ZombossDeployTuningRejection("scorer.fireChanceMilli: must be 0..1000");
        var minEnemyUnits = Int(scorerEl, "minEnemyUnitsToConsiderDeploy");
        if (minEnemyUnits < 0)
            throw new ZombossDeployTuningRejection("scorer.minEnemyUnitsToConsiderDeploy: must be >= 0");
        var maxConcurrentOwn = Int(scorerEl, "maxConcurrentOwnUnits");
        if (maxConcurrentOwn < 0)
            throw new ZombossDeployTuningRejection("scorer.maxConcurrentOwnUnits: must be >= 0");
        var scorer = new ZombossScorerTuning(fireChanceMilli, minEnemyUnits, maxConcurrentOwn);

        return new ZombossDeployTuning(version, policies, ceilings, rosterSizeMax, scorer);
    }

    static JsonElement Obj(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : throw new ZombossDeployTuningRejection($"{name}: expected an object");

    static JsonElement Arr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v : throw new ZombossDeployTuningRejection($"{name}: expected an array");

    static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new ZombossDeployTuningRejection($"{name}: expected a string");

    static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i : throw new ZombossDeployTuningRejection($"{name}: expected an integer");
}

/// <summary>Process-wide holder, matching every other injector/server tuning file's own `Configure`
/// convention (`LawnDeployEventsTuningHub`'s exact shape) — each host process calls `Configure` once at
/// startup from its own copy of `gk-core/data/tuning/zomboss-deploy-ai.v1.json`.</summary>
public static class ZombossDeployTuningHub
{
    static ZombossDeployTuning? _tuning;
    public static bool IsConfigured => _tuning != null;
    public static ZombossDeployTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ZombossDeployTuningHub.Configure(...) has not run — call it once at host startup " +
        "from data/tuning/zomboss-deploy-ai.v1.json, matching every other tuning hub.");
    public static void Configure(ZombossDeployTuning tuning) => _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>Tests only.</summary>
    internal static void ResetForTests() => _tuning = null;
}

/// <summary>
/// zomboss-deploy-ai T3.2 (spec-zomboss-deploy-ai.md Assumption 1, plan's own Gates section) — a pure
/// function over already-loaded data, matching `AmbushDraw.cs`'s own "every tunable an explicit
/// parameter" shape: every call site passes its own tuning/catalog, nothing read from a static hub
/// internally by this class itself (the two `Hub`-based callers below do the hub read, once, at their
/// own call site — the same split `LawnDeployEventEvaluator` already established).
///
/// <para><b>Named, reversible default (plan's own "Gates vs. checkpoints" section)</b>: "the same
/// summonable species pool the player draws from" is
/// <c>CreatureSpeciesCatalog.All.Where(Acquisition.HasFlag(Summonable))</c> — the EXACT predicate
/// `SummonRoller.BandWithFallback` itself filters on (confirmed by direct read, not the sibling
/// `Acquisition != CaptureOnly` convention some OTHER call sites use, which is a materially different
/// filter — a `CaptureOnly|EventOnly` species with no `Summonable` flag would pass that one and fail
/// this one). "Filtered to the current level's own threat band" substitutes `BaseRarity` (already on
/// the live `CreatureSpeciesDef` the summon roller reads) for the spec's own "threat band" language: a
/// real per-species `ThreatBand` exists (`gk-core/data/tuning/creature-threat.v1.json`) but is discarded during
/// species generation and unreachable from this catalog (`SlotFilter.cs`'s own doc comment, confirmed)
/// — reusing the summon system's own "rarity is power" assumption is the closest available proxy, named
/// explicitly here (and in the tuning file's own `_meta`) as a reversible substitution, not a permanent
/// design decision.</para>
/// </summary>
public static class ZombossDeployRoster
{
    /// <summary>The last threshold `waveNumber` has reached or passed, per the ascending, validated
    /// list `ZombossDeployTuningLoader.Parse` already enforces. `waveNumber` below every threshold
    /// (e.g. 0, before wave 1 starts) returns the lowest rung named — never throws, matching this
    /// module's own "the scorer declines gracefully, never crashes on an edge wave" boundary.</summary>
    public static CreatureRarity RarityCeilingForWave(int waveNumber, IReadOnlyList<ZombossWaveRarityCeiling> ceilings)
    {
        var ceiling = ceilings[0].RarityCeiling;
        foreach (var c in ceilings)
        {
            if (waveNumber < c.MaxWaveAtLeast) break;
            ceiling = c.RarityCeiling;
        }
        return ceiling;
    }

    /// <summary>Deterministic and reproducible from the catalog + tuning alone — same
    /// <paramref name="waveNumber"/> against the same catalog/tuning revision always returns the same
    /// list, in the catalog's own stable `SpeciesId` ordinal order (never a set, never enumeration
    /// order of some other collection) so a caller's own later `SeededRng` pick over this list is
    /// reproducible too.</summary>
    public static IReadOnlyList<string> AvailableSpeciesFor(
        int waveNumber, IReadOnlyList<CreatureSpeciesDef> catalog, IReadOnlyList<ZombossWaveRarityCeiling> ceilings)
    {
        var ceiling = RarityCeilingForWave(waveNumber, ceilings);
        return catalog
            .Where(s => s.Acquisition.HasFlag(CreatureAcquisition.Summonable) && s.BaseRarity <= ceiling
                // T1.4's own established exclusion (ExpeditionStoreTests.cs/ContractGateTests.cs):
                // HypnoAlly has no deploy path yet (DeployAsync refuses `deploy.hypno-ally-not-implemented`).
                // Caught here proactively, not live: T3.4's own deploy-wiring research found this exact
                // gap already existed in T2.1's own player-facing roster before it was fixed there.
                && s.DeployMode != CreatureDeployMode.HypnoAlly)
            .Select(s => s.SpeciesId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}
