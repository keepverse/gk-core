using System.Text.Json;

namespace FusionRpg.Core.Items.Materials;

/// <summary>The shared tuning both head fields read (species-gear-chain T10/T11, ask #4 layout) —
/// ONE file, ONE parser, never two. `gk-core/data/tuning/deployment-hierarchy.v5.json` (species-gear-chain T23
/// added `repairDestroyChanceMilli` and `potentialCostPerVerb.repair`; v2 added the cache blocks, v1 was
/// the first publish)
/// <see cref="CacheDecay"/> and <see cref="CacheRetrieval"/>, moved out of RpgStore constants). Reserved for
/// module 7's own future build and refused-as-unknown nowhere: `wearPerBattleMilli`, the repair
/// keys. Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public sealed record DeploymentHierarchyTuning(
    int SchemaVersion,
    int Version,
    IReadOnlyDictionary<string, long> DurabilityBaseByClass,
    IReadOnlyDictionary<string, long> DurabilityRarityMultiplierMilli,
    IReadOnlyDictionary<string, long> PotentialBaseByClass,
    IReadOnlyDictionary<string, long> PotentialRarityMultiplierMilli,
    IReadOnlyDictionary<string, long> PotentialOverrides,
    IReadOnlyList<string> PotentialOverrideExpected,
    long CraftWearPerAttemptMilli,
    long RepairDestroyChanceMilli,
    IReadOnlyDictionary<string, long> PotentialCostPerVerb,
    CacheDecayTuning CacheDecay,
    CacheRetrievalTuning CacheRetrieval);

/// <summary>Per-turn survival of a dropped corpse cache (deployment-hierarchy, cache-decay). Every value
/// is a bounded per-mille ratio 0..1000 -- the PS-8 bounded-ratio exemption, stated here. Effective
/// survival = min(CapMilli, BaseMilli + rarityOrdinal x RarityStepMilli); CapMilli &lt; 1000 so no cache
/// is immortal (closed: ideal doc "Alternatives rejected").</summary>
public sealed record CacheDecayTuning(long BaseMilli, long RarityStepMilli, long CapMilli);

/// <summary>The retrieval contest (deployment-hierarchy, cache-retrieval): success per-mille =
/// clamp(BaseMilli + (retrieverTheta - originTheta) x ThetaStepMilli, FloorMilli, CapMilli), resolved
/// DueTurns world turns after filing. Bounded ratios 0..1000; FloorMilli &gt; 0 and CapMilli &lt; 1000 so
/// neither the weakest party is hopeless nor the strongest certain.</summary>
public sealed record CacheRetrievalTuning(
    long BaseMilli, long ThetaStepMilli, long FloorMilli, long CapMilli, int DueTurns);

/// <summary>Process-wide, configured once at host startup (the <c>LawnAttritionTuningHub</c> shape):
/// no built-in default, because a silent default is how balance numbers hid in code before.</summary>
public static class DeploymentHierarchyTuningHub
{
    static DeploymentHierarchyTuning? _tuning;

    public static void Configure(DeploymentHierarchyTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static DeploymentHierarchyTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "DeploymentHierarchyTuningHub.Configure(...) has not run. Read data/tuning/deployment-hierarchy.v5.json at " +
        "host startup (the server's Program.cs does).");

    public static bool IsConfigured => _tuning != null;

    public static void Reset() => _tuning = null;
}

public sealed class DeploymentHierarchyTuningRejection : Exception
{
    public DeploymentHierarchyTuningRejection(string message) : base(message) { }
}

public static class DeploymentHierarchyTuningLoader
{
    public static DeploymentHierarchyTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DeploymentHierarchyTuningRejection("deployment-hierarchy tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = RequireInt(root, "schemaVersion");
            var version = RequireInt(root, "version");

            if (!root.TryGetProperty("durability", out var durability) || durability.ValueKind != JsonValueKind.Object)
                throw new DeploymentHierarchyTuningRejection("deployment-hierarchy tuning: missing or non-object 'durability'");
            if (!root.TryGetProperty("potential", out var potential) || potential.ValueKind != JsonValueKind.Object)
                throw new DeploymentHierarchyTuningRejection("deployment-hierarchy tuning: missing or non-object 'potential'");

            var wear = RequireMilli(root, "craftWearPerAttemptMilli", 0, 1000, "craftWearPerAttemptMilli");
            // species-gear-chain T23: D1's destroy chance. A bounded per-mille ratio (PS-8 exemption,
            // stated at the key), required rather than defaulted — a silent 0 would make every repair
            // risk-free and quietly delete the module's own sink.
            var repairDestroy = RequireMilli(root, "repairDestroyChanceMilli", 0, 1000, "repairDestroyChanceMilli");
            var costs = RequireVerbCosts(root);
            var decay = RequireCacheDecay(root);
            var retrieval = RequireCacheRetrieval(root);

            return new DeploymentHierarchyTuning(
                schemaVersion, version,
                RequireBaseTable(durability, "durability", "baseByClass"),
                RequireMilliTable(durability, "durability", "rarityMultiplierMilli"),
                RequireBaseTable(potential, "potential", "baseByClass"),
                RequireMilliTable(potential, "potential", "rarityMultiplierMilli"),
                RequireOverrides(potential),
                RequireExpected(potential),
                wear, repairDestroy, costs, decay, retrieval);
        }
    }

    static IReadOnlyDictionary<string, long> RequireBaseTable(JsonElement section, string sectionName, string key)
    {
        if (!section.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{sectionName}.{key}' is missing or not an object");
        var table = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number)
                throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{sectionName}.{key}[{prop.Name}]' is not a number");
            var value = prop.Value.GetInt64();
            if (value < 0)
                throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{sectionName}.{key}[{prop.Name}]' is negative ({value}) — a head-field base is a magnitude, never negative");
            table[prop.Name] = value;
        }
        if (table.Count == 0)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{sectionName}.{key}' is empty — an empty base table derives nothing");
        return table;
    }

    static IReadOnlyDictionary<string, long> RequireMilliTable(JsonElement section, string sectionName, string key)
    {
        if (!section.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{sectionName}.{key}' is missing or not an object");
        var table = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number)
                throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{sectionName}.{key}[{prop.Name}]' is not a number");
            table[prop.Name] = prop.Value.GetInt64();
        }
        if (table.Count == 0)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{sectionName}.{key}' is empty");
        return table;
    }

    static IReadOnlyDictionary<string, long> RequireOverrides(JsonElement potential)
    {
        var overrides = new Dictionary<string, long>(StringComparer.Ordinal);
        if (potential.TryGetProperty("overrides", out var el) && el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Number)
                    throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'potential.overrides[{prop.Name}]' is not a number");
                var value = prop.Value.GetInt64();
                if (value < 0)
                    throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'potential.overrides[{prop.Name}]' is negative ({value}) — a present override must be a real authored value");
                overrides[prop.Name] = value;
            }
        }
        return overrides;
    }

    static IReadOnlyList<string> RequireExpected(JsonElement potential)
    {
        var expected = new List<string>();
        if (potential.TryGetProperty("overrideExpected", out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new DeploymentHierarchyTuningRejection("deployment-hierarchy tuning: 'potential.overrideExpected' carries a non-string entry");
                expected.Add(item.GetString()!);
            }
        }
        return expected;
    }

    static long RequireMilli(JsonElement root, string key, long min, long max, string label)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: missing or non-numeric '{key}'");
        var milli = v.GetInt64();
        // A bounded ratio in [min..max] — exempt from the magnitude rules, saying so here.
        if (milli < min || milli > max)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: '{label}' must be in [{min}..{max}], got {milli}");
        return milli;
    }

    static IReadOnlyDictionary<string, long> RequireVerbCosts(JsonElement root)
    {
        if (!root.TryGetProperty("potentialCostPerVerb", out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DeploymentHierarchyTuningRejection("deployment-hierarchy tuning: missing or non-object 'potentialCostPerVerb'");
        var costs = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (CraftOperation op in Enum.GetValues<CraftOperation>())
        {
            var id = CraftOperations.Id(op);
            if (!el.TryGetProperty(id, out var v) || v.ValueKind != JsonValueKind.Number)
                throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'potentialCostPerVerb' has no cost for verb '{id}' — an absent verb is not a free verb");
            var cost = v.GetInt64();
            if (cost < 0)
                throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'potentialCostPerVerb[{id}]' is negative ({cost})");
            costs[id] = cost;
        }
        foreach (var name in el.EnumerateObject().Select(p => p.Name))
            if (!costs.ContainsKey(name))
                throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'potentialCostPerVerb' names unknown verb '{name}'");
        return costs;
    }

    static CacheDecayTuning RequireCacheDecay(JsonElement root)
    {
        if (!root.TryGetProperty("cacheDecay", out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DeploymentHierarchyTuningRejection("deployment-hierarchy tuning: missing or non-object 'cacheDecay' (v2+)");
        var baseMilli = RequireMilli(el, "baseMilli", 0, 999, "cacheDecay.baseMilli");
        var step = RequireMilli(el, "rarityStepMilli", 0, 999, "cacheDecay.rarityStepMilli");
        // Strictly below 1000: a cap of 1000 would make some cache immortal, which the design closed.
        var cap = RequireMilli(el, "capMilli", 0, 999, "cacheDecay.capMilli");
        if (baseMilli > cap)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'cacheDecay.baseMilli' ({baseMilli}) exceeds 'capMilli' ({cap})");
        return new CacheDecayTuning(baseMilli, step, cap);
    }

    static CacheRetrievalTuning RequireCacheRetrieval(JsonElement root)
    {
        if (!root.TryGetProperty("cacheRetrieval", out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DeploymentHierarchyTuningRejection("deployment-hierarchy tuning: missing or non-object 'cacheRetrieval' (v2+)");
        var floor = RequireMilli(el, "floorMilli", 1, 999, "cacheRetrieval.floorMilli");
        var cap = RequireMilli(el, "capMilli", 1, 999, "cacheRetrieval.capMilli");
        var baseMilli = RequireMilli(el, "baseMilli", 0, 1000, "cacheRetrieval.baseMilli");
        var step = RequireMilli(el, "thetaStepMilli", 0, 1000, "cacheRetrieval.thetaStepMilli");
        if (floor > cap)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'cacheRetrieval.floorMilli' ({floor}) exceeds 'capMilli' ({cap})");
        var due = RequireInt(el, "dueTurns");
        if (due < 1)
            throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: 'cacheRetrieval.dueTurns' must be >= 1, got {due}");
        return new CacheRetrievalTuning(baseMilli, step, floor, cap, due);
    }

    static int RequireInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : throw new DeploymentHierarchyTuningRejection($"deployment-hierarchy tuning: missing or non-numeric '{prop}'");
}
