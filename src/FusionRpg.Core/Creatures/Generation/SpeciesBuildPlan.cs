using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>One species' full share vector over the twelve aptitudes — permille, summing to exactly
/// 1000 (largest-remainder rounding, `SpeciesBuildPlanner`'s own numeric rule).</summary>
public sealed record SpeciesBuildVector(string SpeciesId, IReadOnlyDictionary<string, long> SharePermille);

/// <summary>The planner's full output: every species' vector plus the corpus-wide share per aptitude
/// (decision 11: measured over total allocated points, i.e. the mean vector value per aptitude across
/// the whole corpus) — the exact number Phase 3 checks against the band.</summary>
public sealed record SpeciesBuildResult(
    IReadOnlyList<SpeciesBuildVector> Vectors,
    IReadOnlyDictionary<string, long> CorpusSharePermille);

/// <summary>Phase 3's refusal (spec-redistribution-plan.md §"Phase 3 — verify, and refuse"): thrown
/// instead of returning an out-of-band plan. <see cref="OffendingShares"/> carries exactly the
/// aptitudes that failed, for the CLI to name without re-deriving them.</summary>
public sealed class SpeciesBuildRefusal : Exception
{
    public IReadOnlyDictionary<string, long> OffendingShares { get; }

    public SpeciesBuildRefusal(string message, IReadOnlyDictionary<string, long> offendingShares) : base(message)
        => OffendingShares = offendingShares;
}

/// <summary>
/// The committed plan's canonical form (`gk-data/packs/fusion/data/generated/creatures/_species-build-plan.json`,
/// spec-redistribution-plan.md §"Shape": "speciesId → { aptitudeId: sharePermille }"). Sorted keys at
/// every level — species by id, aptitudes by id — so a rerun over the same corpus is byte-identical,
/// the same discipline `ConcreteSpeciesSerializer` already established for the per-species tree.
/// </summary>
public static class SpeciesBuildPlanSerializer
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Canonical(IReadOnlyList<SpeciesBuildVector> vectors)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var v in vectors)
            root[v.SpeciesId] = new SortedDictionary<string, long>(
                new Dictionary<string, long>(v.SharePermille), StringComparer.Ordinal);
        // LF, never Environment.NewLine (CanonicalEol): the plan is a committed generated file and the
        // tree is checked out as LF, so a CRLF write is stale against its own fresh checkout.
        return JsonSerializer.Serialize(root, Options).ToLf() + "\n";
    }
}
