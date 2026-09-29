using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Consumables;

/// <summary>What one pass over the shipped seed corpus measured. A report, not a gate.</summary>
/// <param name="Rows">Every seed that parsed.</param>
/// <param name="Rejections">Every refusal, in corpus order.</param>
/// <param name="PhantomFamilies">
/// ⛔ Families the corpus names that resolve to no affix-family row, so their <c>kindId</c> is unknown.
/// <b>Excluded from the runtime-legality check rather than guessed into it</b> — module 17's rule,
/// kept: guessing would make an unresolved reference look like a balance failure.
/// </param>
/// <param name="ExclusionGroups">Distinct exclusion keys, and how many rows sit under each.</param>
/// <param name="GradeHistogram">How many rows resolved to each grade 1..5.</param>
public sealed record ConsumableCorpusReport(
    IReadOnlyList<ConsumableSeed> Rows,
    IReadOnlyList<AtomRejection> Rejections,
    IReadOnlyList<string> PhantomFamilies,
    IReadOnlyDictionary<string, int> ExclusionGroups,
    IReadOnlyDictionary<int, int> GradeHistogram);

/// <summary>
/// The corpus-wide checks — §6.3's catalog-load column, run over
/// <c>gk-data/packs/fusion/data/seed/items/consumables/*.json</c> rather than over a fixture.
///
/// <para>Cross-row checks are import-phase because they are properties of the catalog and not of a
/// row: id distinctness across partitions is the one that matters here, since three agents authored
/// twenty rows each with no sight of one another.</para>
/// </summary>
public static class ConsumableCorpusValidator
{
    /// <summary>
    /// The <c>consumable_def</c> row a seed resolves to. <b>Nothing is authored twice:</b> the grade
    /// comes from the seed's <c>powerBand</c> through the tuning's mirrored <c>gradeTierMap</c>, and
    /// the exclusion group from <c>(family, element)</c> — the shipped pool-group default. An author
    /// writing either by hand would be a second source of truth for a derived fact.
    /// </summary>
    public static bool TryToDefRow(ConsumableSeed seed, ConsumableTuning tuning, out ConsumableDefRow row)
    {
        if (seed is null) throw new ArgumentNullException(nameof(seed));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (!tuning.TryGradeFor(seed.PowerBand, out var grade))
        {
            row = null!;
            return false;
        }

        row = new ConsumableDefRow(
            seed.ContainerId, seed.ClassId, seed.UseContexts, grade, seed.ExclusionGroup,
            seed.ManifestCost, seed.GrantsActionId, seed.CooldownKey);
        return true;
    }

    /// <summary>
    /// Validate the whole corpus.
    /// </summary>
    /// <param name="seeds">Every row from every partition.</param>
    /// <param name="tuning">The parsed tuning.</param>
    /// <param name="kindOfFamily">
    /// Resolves an affix family id to its <c>kindId</c>. Supplied by the caller (the affix-family
    /// corpus lives in a file and Core never opens one). Returning <c>null</c> means the family does
    /// not resolve, which is reported as a phantom rather than guessed.
    /// </param>
    public static ConsumableCorpusReport Validate(
        IReadOnlyList<ConsumableSeed> seeds,
        ConsumableTuning tuning,
        Func<string, string?> kindOfFamily)
    {
        if (seeds is null) throw new ArgumentNullException(nameof(seeds));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (kindOfFamily is null) throw new ArgumentNullException(nameof(kindOfFamily));

        var fails = new List<AtomRejection>();
        var phantoms = new SortedSet<string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        var grades = new Dictionary<int, int>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in seeds)
        {
            if (!seenIds.Add(seed.ContainerId))
                fails.Add(ConsumableRules.Fail(ConsumableRules.CorpusMalformed,
                    $"'{seed.ContainerId}' appears twice across the partitions; three agents author " +
                    "twenty rows each with no sight of one another, so id distinctness is a catalog " +
                    "property and has to be checked as one"));

            if (!TryToDefRow(seed, tuning, out var def))
            {
                fails.Add(ConsumableRules.Fail(ConsumableRules.BadValue,
                    $"'{seed.ContainerId}' names powerBand '{seed.PowerBand}', which is not one of " +
                    "bands.v1.json's five — so it resolves to no grade"));
                continue;
            }

            groups[def.ExclusionGroup] = groups.GetValueOrDefault(def.ExclusionGroup) + 1;
            grades[def.Grade] = grades.GetValueOrDefault(def.Grade) + 1;

            var kindId = kindOfFamily(seed.Family);
            var core = new List<ConsumableCoreAtom>();
            if (kindId is null)
                phantoms.Add(seed.Family);
            else
                // The seed's core atom, as far as a seed can describe one: the family's own kind, at
                // the tier its powerBand maps to. No magnitude, because the seed authors none -- which
                // is exactly why the grade check below can never be circular.
                core.Add(new ConsumableCoreAtom($"{seed.Family}.t{def.Grade}", kindId, def.Grade, "{}"));

            fails.AddRange(ConsumableValidator.ValidateShape(
                def, core, prefixRolls: 0, suffixRolls: 0, rarityId: null,
                minTier: null, maxTier: null, tuning));
        }

        return new ConsumableCorpusReport(
            seeds, fails, phantoms.ToList(), groups, grades);
    }
}
