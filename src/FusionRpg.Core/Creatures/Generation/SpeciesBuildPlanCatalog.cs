using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// The committed plan (`gk-data/packs/fusion/data/generated/creatures/_species-build-plan.json`), read back at runtime by
/// `creature-type-allocation` (module 5) to compose a species' baseline allocation
/// (spec-creature-type-allocation.md: `baseline(player, species) = plan.shares[species] ⊗
/// PointBudget.PointsFor(...)`). Configured once by the SERVER HOST from the generated file — the same
/// "server loads, Core reads no file" discipline as `CreatureSpeciesCatalog`/`SpeciesProgressionTuningHub`
/// — never regenerated or written to at runtime; `gk-forge/tools/CreatureBuildPlanGen` owns writing it.
/// </summary>
public static class SpeciesBuildPlanCatalog
{
    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>? _configured;

    public static void Configure(IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> plan) =>
        _configured = plan ?? throw new ArgumentNullException(nameof(plan));

    public static bool IsConfigured => _configured != null;

    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Plan => _configured
        ?? throw new InvalidOperationException(
            "SpeciesBuildPlanCatalog.Configure(...) has not run. The redistribution plan reads " +
            "data/generated/creatures/_species-build-plan.json — there is no built-in default to fall back to.");

    /// <summary>A species with no entry in the committed plan has no shares — empty, never a thrown
    /// error, matching `AptitudeAllocation.Empty`'s own "no build" contract. This is reachable for a
    /// species whose anchor is still `unresolved` on `aptitudePrimary` (`CreatureBuildPlanGen`'s own
    /// skip list) — such a species simply has no CreatureType baseline yet, not a startup error.</summary>
    public static IReadOnlyDictionary<string, long> SharesFor(string speciesId) =>
        Plan.TryGetValue(speciesId, out var shares) ? shares : EmptyShares;

    static readonly IReadOnlyDictionary<string, long> EmptyShares = new Dictionary<string, long>();
}

public sealed class SpeciesBuildPlanRejection : Exception
{
    public SpeciesBuildPlanRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — reads the exact canonical shape
/// `SpeciesBuildPlanSerializer.Canonical` writes: a flat `{ speciesId: { aptitudeId: sharePermille } }`
/// object.</summary>
public static class SpeciesBuildPlanReader
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SpeciesBuildPlanRejection("species build plan: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new SpeciesBuildPlanRejection($"species build plan: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new SpeciesBuildPlanRejection("species build plan: expected a top-level object");

            var result = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
            foreach (var speciesProp in doc.RootElement.EnumerateObject())
            {
                if (speciesProp.Value.ValueKind != JsonValueKind.Object)
                    throw new SpeciesBuildPlanRejection($"species build plan: '{speciesProp.Name}' is not an object");

                var shares = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var aptProp in speciesProp.Value.EnumerateObject())
                {
                    if (aptProp.Value.ValueKind != JsonValueKind.Number || !aptProp.Value.TryGetInt64(out var v))
                        throw new SpeciesBuildPlanRejection(
                            $"species build plan: '{speciesProp.Name}.{aptProp.Name}' is not an integer");
                    shares[aptProp.Name] = v;
                }
                result[speciesProp.Name] = shares;
            }
            return result;
        }
    }
}
