using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// `favour-detector` (module 5) — what a corpus' build-favour actually looks like, measured the one
/// way the ideal proved is honest: **which aptitude leads** and **which shape a vector has**.
/// Distinctness of exact share vectors is deliberately absent — it reads ~85% unique on a converged
/// corpus, so it would pass forever, and a field for it would re-open the trap
/// (spec-favour-detector.md, "Distinctness of exact vectors is deliberately not measured").
///
/// <para>Every count is a READING over the measured population, never a constant to assert. The
/// population is real creatures only: a row marked <c>speciesKind: excluded</c> is out of every count
/// (see <see cref="BuildFavourMeasurer.RosterPopulation"/>), which is also the one filter every
/// downstream consumer (the lean ranks, stage A) reads.</para>
///
/// <para><b><see cref="LeanByPrimary"/> is a distribution, not a scalar.</b> The spec declares this
/// field as "the lean each primary received"; a per-species lean (the mechanism this measure exists
/// to judge) means one primary can receive several leans at once, so the value is a histogram
/// lean-permille → species count. A scalar could not express the program's own success criterion
/// ("the measure shows leans varying within a primary"), and the spec's own reading stays a
/// one-line relation on it.</para>
/// </summary>
/// <param name="SpeciesCount">The measured population's size — a reading, reported, never asserted.</param>
/// <param name="LeadCountByAptitude">How many species hold each aptitude as their largest share.</param>
/// <param name="MaxLeadPermille">The most-common lead, as a per-mille share of the population.</param>
/// <param name="MaxLeadAptitude">Which aptitude that is (ordinal-smallest id on a tie).</param>
/// <param name="ShapeCount">Key: the vector's non-zero shares sorted descending ("350,163,163,162,162"),
/// i.e. the profile, owner-blind — two species with the same profile on different aptitudes share a key.</param>
/// <param name="LargestShapePermille">The most-common shape, as a per-mille share of the population.</param>
/// <param name="LeanByPrimary">Per leading aptitude, the leans it received: lean permille → species count.</param>
/// <param name="LeanSignalsMissing">The signals that could not be measured, `"&lt;speciesId&gt;:&lt;signal&gt;"`,
/// ordinal-sorted (EP2.6). A missing signal is NEUTRAL and recorded, never a failure — the item
/// ladder's "never reject; narrow and record" — and this list is the record, so no consumer has to
/// re-derive why a lean came out flat.</param>
public sealed record BuildFavourMeasure(
    long SpeciesCount,
    IReadOnlyDictionary<string, long> LeadCountByAptitude,
    long MaxLeadPermille,
    string MaxLeadAptitude,
    IReadOnlyDictionary<string, long> ShapeCount,
    long LargestShapePermille,
    IReadOnlyDictionary<string, IReadOnlyDictionary<long, long>> LeanByPrimary,
    IReadOnlyList<string> LeanSignalsMissing);

/// <summary>Pure function over the planner's output: no files, no models, no tuning. The tool reads
/// anchors and the plan; this type only measures them.</summary>
public static class BuildFavourMeasurer
{
    /// <summary>The one population filter (spec-favour-detector.md): real creatures only. A row
    /// marked <c>speciesKind: excluded</c> never counts as roster, so it is left out of every count and
    /// histogram — and out of the rank population <see cref="LeanSignals"/> builds, which takes this
    /// list rather than re-filtering it.</summary>
    public static IReadOnlyList<AnchorRow> RosterPopulation(IReadOnlyList<AnchorRow> species)
    {
        if (species is null) throw new ArgumentNullException(nameof(species));
        return species.Where(s => !AnchorSpeciesKind.IsExcluded(s)).ToArray();
    }

    public static BuildFavourMeasure Measure(
        SpeciesBuildResult plan, IReadOnlyList<AnchorRow> species, IReadOnlyList<string> leanSignalsMissing)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        return Measure(plan.Vectors, species, leanSignalsMissing);
    }

    /// <summary>The vectors' own view — what Phase 4 gates on (`SpeciesBuildPlanner`) and what the tool
    /// serialises. The planner measures the plan it just built through THIS entry, so the gate and the
    /// committed artifact are one implementation of the histograms rather than two.</summary>
    public static BuildFavourMeasure Measure(
        IReadOnlyList<SpeciesBuildVector> vectors, IReadOnlyList<AnchorRow> species,
        IReadOnlyList<string> leanSignalsMissing)
    {
        if (vectors is null) throw new ArgumentNullException(nameof(vectors));
        if (leanSignalsMissing is null) throw new ArgumentNullException(nameof(leanSignalsMissing));
        var population = RosterPopulation(species);
        if (population.Count == 0)
            throw new ArgumentException("no roster species to measure", nameof(species));

        var vectorById = vectors.ToDictionary(v => v.SpeciesId, StringComparer.Ordinal);

        var leadCount = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var shapeCount = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var leanByPrimary = new SortedDictionary<string, SortedDictionary<long, long>>(StringComparer.Ordinal);

        // Ordinal speciesId order: the artefact must not depend on the caller's input order.
        foreach (var row in population.OrderBy(s => s.SpeciesId, StringComparer.Ordinal))
        {
            if (!vectorById.TryGetValue(row.SpeciesId, out var vector))
                throw new ArgumentException(
                    $"no vector for species '{row.SpeciesId}' — the measure cannot skip a " +
                    "roster member silently", nameof(vectors));

            // The lead is the vector's largest share, tie broken by the ordinal-smallest aptitude id
            // — never the anchor's declared primary, so a later tuning publish that moves what leads
            // a vector is measured, not assumed away.
            var lead = vector.SharePermille
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First();

            leadCount[lead.Key] = leadCount.GetValueOrDefault(lead.Key) + 1;
            var shapeKey = ShapeKey(vector.SharePermille);
            shapeCount[shapeKey] = shapeCount.GetValueOrDefault(shapeKey) + 1;

            // The lean a species received is its lead share: under the shipped tuning the primary
            // holds `lean` permille straight and any secondary gets at most 30% of the remainder.
            if (!leanByPrimary.TryGetValue(lead.Key, out var leans))
                leanByPrimary[lead.Key] = leans = new SortedDictionary<long, long>();
            leans[lead.Value] = leans.GetValueOrDefault(lead.Value) + 1;
        }

        var speciesCount = (long)population.Count;
        var topLead = leadCount.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
        var topShape = shapeCount.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();

        long maxLeadPermille, largestShapePermille;
        checked
        {
            maxLeadPermille = topLead.Value * 1000 / speciesCount;
            largestShapePermille = topShape.Value * 1000 / speciesCount;
        }

        return new BuildFavourMeasure(
            SpeciesCount: speciesCount,
            LeadCountByAptitude: leadCount,
            MaxLeadPermille: maxLeadPermille,
            MaxLeadAptitude: topLead.Key,
            ShapeCount: shapeCount,
            LargestShapePermille: largestShapePermille,
            LeanByPrimary: leanByPrimary.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyDictionary<long, long>)kv.Value, StringComparer.Ordinal),
            // Sorted here as well as at the source: this list is what the committed artifact carries,
            // and two callers assembling the same facts must serialise the same bytes.
            LeanSignalsMissing: leanSignalsMissing.OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The profile, not the owner: the vector's non-zero shares sorted descending, ordinal
    /// and invariant, so the key is deterministic and owner-blind.</summary>
    static string ShapeKey(IReadOnlyDictionary<string, long> vector) =>
        string.Join(",", vector.Values.Where(v => v > 0).OrderByDescending(v => v));
}

/// <summary>
/// The committed measure's canonical form
/// (`gk-data/packs/fusion/data/generated/creatures/_species-build-measure.json`) — sorted keys at every level and LF line
/// endings, so a rerun over the same corpus is byte-identical and the `--check` byte-compare means
/// something (same discipline as <see cref="SpeciesBuildPlanSerializer"/>).
/// </summary>
public static class BuildFavourMeasureSerializer
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Canonical(BuildFavourMeasure measure)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["speciesCount"] = measure.SpeciesCount,
            ["leadCountByAptitude"] = Sorted(measure.LeadCountByAptitude.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)),
            ["maxLeadPermille"] = measure.MaxLeadPermille,
            ["maxLeadAptitude"] = measure.MaxLeadAptitude,
            ["shapeCount"] = Sorted(measure.ShapeCount.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)),
            ["largestShapePermille"] = measure.LargestShapePermille,
            // Ordinal strings, sorted — a reading of what could not be measured, never a count.
            ["leanSignalsMissing"] = measure.LeanSignalsMissing.OrderBy(m => m, StringComparer.Ordinal).ToArray(),
            // Long keys serialise as strings; the inner dictionary is sorted ascending by lean.
            ["leanByPrimary"] = new SortedDictionary<string, object?>(
                measure.LeanByPrimary.ToDictionary(
                    kv => kv.Key,
                    kv => (object?)SortedLeans(kv.Value),
                    StringComparer.Ordinal), StringComparer.Ordinal)
        };
        return JsonSerializer.Serialize(root, Options).ToLf() + "\n";
    }

    static SortedDictionary<long, long> SortedLeans(IReadOnlyDictionary<long, long> leans)
    {
        var sorted = new SortedDictionary<long, long>();
        foreach (var (lean, count) in leans) sorted[lean] = count;
        return sorted;
    }

    static SortedDictionary<string, object?> Sorted(IDictionary<string, object?> values) =>
        new(values, StringComparer.Ordinal);
}
