using System.Globalization;
using System.Text.Json;
using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// EP2.2 (spec-favour-detector.md "Testing strategy" 1–5): the lead and shape histograms, measured
/// owner-blind and order-independently. Every corpus here is synthetic — no test reads the size or
/// the counts of the real corpus (that contract lives in the artifact reconciliation, EP2.3).
/// </summary>
public class BuildFavourMeasureTests
{
    static AnchorRow Anchor(string speciesId, string primary, string? speciesKind = null) => new(
        SpeciesId: speciesId, Rarity: "chaff", ThreatBand: null,
        AptitudePrimary: primary, AptitudeSecondary: null, Pure: true,
        AttackTempo: "steady", Reach: "melee", Variants: Array.Empty<string>(),
        Side: "plant", GameTypeId: 1, ElementPrimary: "fire", ElementSecondary: null,
        DeployMode: "PlantAvatar", Acquisition: new[] { "Summonable" }, Traits: Array.Empty<string>(),
        TargetPreference: "frontline", SpeciesKind: speciesKind);

    static SpeciesBuildResult Plan(params SpeciesBuildVector[] vectors) =>
        new(vectors, new Dictionary<string, long>());

    static SpeciesBuildVector Vector(string speciesId, params (string Aptitude, long Share)[] shares) =>
        new(speciesId, shares.ToDictionary(s => s.Aptitude, s => s.Share, StringComparer.Ordinal));

    /// <summary>No unmeasurable signals: the population-ranking contract is EP2.4's, and these tests
    /// are about the histograms over whatever population they were handed.</summary>
    static readonly IReadOnlyList<string> NoMissing = Array.Empty<string>();

    // ── 1. the lead histogram ───────────────────────────────────────────────────────────────────

    [Fact]
    public void One_aptitude_leading_every_species_reports_1000_permille()
    {
        var species = Enumerable.Range(0, 6).Select(i => Anchor($"s{i}", "Onslaught")).ToArray();
        var plan = Plan(species.Select(s => Vector(s.SpeciesId, ("Onslaught", 600), ("Vigor", 400))).ToArray());

        var m = BuildFavourMeasurer.Measure(plan, species, NoMissing);

        Assert.Equal(6L, m.SpeciesCount);
        Assert.Equal(6L, m.LeadCountByAptitude["Onslaught"]);
        Assert.Equal(1000L, m.MaxLeadPermille);
        Assert.Equal("Onslaught", m.MaxLeadAptitude);
        // The lean the primary received: one bucket, every species (the before-image of the
        // per-species lean — see LeanByPrimary).
        Assert.Equal(new Dictionary<long, long> { [600] = 6 }, m.LeanByPrimary["Onslaught"]);
    }

    [Fact]
    public void Twelve_leads_spread_evenly_report_the_matching_share()
    {
        // Reads the catalog live rather than a second literal 12 (population-pin pattern).
        var aptitudes = FusionRpg.Core.Stats.Aptitudes.AptitudeCatalog.All.Select(a => a.Id).ToArray();
        var species = aptitudes.Select((a, i) => Anchor($"s{i:D2}", a)).ToArray();
        var plan = Plan(species
            .Select((s, i) => Vector(s.SpeciesId,
                (s.AptitudePrimary, 600),
                (aptitudes[(i + 1) % aptitudes.Length], 400)))
            .ToArray());

        var m = BuildFavourMeasurer.Measure(plan, species, NoMissing);

        Assert.Equal(aptitudes.Length, m.LeadCountByAptitude.Count);
        Assert.All(m.LeadCountByAptitude.Values, c => Assert.Equal(1L, c));
        // 1 * 1000 / 12 = 83, integer per-mille, never a rounded float.
        Assert.Equal(1000L / aptitudes.Length, m.MaxLeadPermille);
    }

    // ── 2. the shape histogram is owner-blind ───────────────────────────────────────────────────

    [Fact]
    public void Two_species_with_the_same_profile_on_different_aptitudes_share_one_shape()
    {
        var species = new[] { Anchor("a", "Might"), Anchor("b", "Vigor") };
        var plan = Plan(
            Vector("a", ("Might", 600), ("Vigor", 400)),
            Vector("b", ("Vigor", 600), ("Might", 400)));

        var m = BuildFavourMeasurer.Measure(plan, species, NoMissing);

        var shape = Assert.Single(m.ShapeCount);
        Assert.Equal("600,400", shape.Key);
        Assert.Equal(2L, shape.Value);
        Assert.Equal(1000L, m.LargestShapePermille);
    }

    // ── 3. ties are ordinal ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_tie_counts_toward_the_ordinal_smallest_aptitude_id()
    {
        // "Might" < "Vigor" ordinally; the tie must land on Might every time, never on map order.
        var species = new[] { Anchor("a", "Vigor") };
        var plan = Plan(Vector("a", ("Vigor", 400), ("Might", 400), ("Focus", 200)));

        var m = BuildFavourMeasurer.Measure(plan, species, NoMissing);

        Assert.Equal(1L, m.LeadCountByAptitude["Might"]);
        Assert.False(m.LeadCountByAptitude.ContainsKey("Vigor"));
        Assert.Equal(new Dictionary<long, long> { [400] = 1 }, m.LeanByPrimary["Might"]);
    }

    // ── 4. determinism across input order ───────────────────────────────────────────────────────

    [Fact]
    public void Input_order_does_not_change_the_artifact_bytes()
    {
        var species = new[] { Anchor("a", "Might"), Anchor("b", "Vigor"), Anchor("c", "Might") };
        var plan = Plan(
            Vector("a", ("Might", 600), ("Vigor", 400)),
            Vector("b", ("Vigor", 550), ("Focus", 450)),
            Vector("c", ("Might", 500), ("Pierce", 300), ("Ruin", 200)));

        var forward = BuildFavourMeasureSerializer.Canonical(BuildFavourMeasurer.Measure(plan, species, NoMissing));
        var reversed = BuildFavourMeasureSerializer.Canonical(BuildFavourMeasurer.Measure(
            plan with { Vectors = plan.Vectors.Reverse().ToArray() }, species.Reverse().ToArray(), NoMissing));

        Assert.Equal(forward, reversed);
        Assert.EndsWith("\n", forward);
        Assert.DoesNotContain("\r", forward);
    }

    // ── population: excluded rows never count ───────────────────────────────────────────────────

    [Fact]
    public void An_excluded_row_is_left_out_of_every_count_and_needs_no_vector()
    {
        var species = new[]
        {
            Anchor("real-a", "Might"),
            Anchor("phantom", "Onslaught", speciesKind: "excluded"),
            Anchor("real-b", "Vigor")
        };
        // Deliberately no vector for "phantom" (which would lead Onslaught): the excluded row must
        // not be looked up, and the extra vector below is ignored rather than planned around.
        var plan = Plan(
            Vector("real-a", ("Might", 600), ("Vigor", 400)),
            Vector("real-b", ("Vigor", 600), ("Might", 400)),
            Vector("phantom", ("Onslaught", 1000)));

        var m = BuildFavourMeasurer.Measure(plan, species, NoMissing);

        Assert.Equal(2L, m.SpeciesCount);
        Assert.Equal(2L, m.LeadCountByAptitude.Values.Sum());
        Assert.False(m.LeadCountByAptitude.ContainsKey("Onslaught"));
        // Both real species share one profile; the phantom's 1000-‰ vector adds no shape.
        var shape = Assert.Single(m.ShapeCount);
        Assert.Equal("600,400", shape.Key);
        Assert.Equal(2L, shape.Value);
        Assert.False(m.LeanByPrimary.ContainsKey("Onslaught"));
    }

    [Fact]
    public void A_mimic_row_is_part_of_the_population()
    {
        // R-CS2's borrowed lawn type id is still a creature — only `excluded` removes a row.
        var species = new[] { Anchor("borrower", "Might", speciesKind: "mimic") };
        var plan = Plan(Vector("borrower", ("Might", 600), ("Vigor", 400)));

        Assert.Equal(1L, BuildFavourMeasurer.Measure(plan, species, NoMissing).SpeciesCount);
    }

    [Fact]
    public void A_roster_species_with_no_vector_is_refused_not_skipped()
    {
        var species = new[] { Anchor("a", "Might"), Anchor("b", "Vigor") };
        var plan = Plan(Vector("a", ("Might", 600), ("Vigor", 400)));

        var ex = Assert.Throws<ArgumentException>(() => BuildFavourMeasurer.Measure(plan, species, NoMissing));
        Assert.Contains("b", ex.Message);
    }

    // ── 5. no exact-vector distinctness member ──────────────────────────────────────────────────

    [Fact]
    public void The_record_carries_no_exact_vector_distinctness_member()
    {
        // Pins the ideal's warning as a contract: distinctness of exact share vectors reads ~85%
        // unique on a converged corpus, so a member for it would let a diversity assertion pass
        // forever. The property set is the closed record — adding a field is a reviewed change
        // (EP2.6 added `LeanSignalsMissing`, which is a reading of what could NOT be measured).
        var names = typeof(BuildFavourMeasure).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[]
        {
            "LargestShapePermille", "LeadCountByAptitude", "LeanByPrimary", "LeanSignalsMissing",
            "MaxLeadAptitude", "MaxLeadPermille", "ShapeCount", "SpeciesCount"
        }, names);
        Assert.DoesNotContain(names, n => n.Contains("Distinct", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LeanSignalsMissing_is_carried_verbatim_sorted_and_never_a_failure()
    {
        // A missing signal is neutral and recorded (EP2.6) — the measure reports it, and the canonical
        // form sorts it, so two callers assembling the same facts serialise the same bytes.
        var species = new[] { Anchor("b", "Vigor"), Anchor("a", "Might") };
        var plan = Plan(
            Vector("a", ("Might", 600), ("Vigor", 400)),
            Vector("b", ("Vigor", 600), ("Might", 400)));

        var m = BuildFavourMeasurer.Measure(plan, species, new[] { "b:threatRung", "a:specialisation" });

        Assert.Equal(new[] { "a:specialisation", "b:threatRung" }, m.LeanSignalsMissing);
        Assert.Contains("\"leanSignalsMissing\": [", BuildFavourMeasureSerializer.Canonical(m));
        // Still a full measurement: a missing signal never removes a species from a histogram.
        Assert.Equal(2L, m.SpeciesCount);
    }

    // ── 6. the committed artifact, contract only ────────────────────────────────────────────────

    [Fact]
    public void The_committed_measure_artifact_reconciles_with_itself_and_joins_to_the_aptitudes()
    {
        // Internal reconciliation (validation-ssot.md): every measured species leads exactly one
        // aptitude and has exactly one shape, so both histograms sum to the measured count. No
        // literal count is pinned anywhere here — the population is a reading.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "generated", "creatures", "_species-build-measure.json")));
        var root = doc.RootElement;
        var speciesCount = root.GetProperty("speciesCount").GetInt64();
        static long Sum(JsonElement obj) => obj.EnumerateObject().Sum(p => p.Value.GetInt64());

        Assert.True(speciesCount > 0);
        Assert.Equal(speciesCount, Sum(root.GetProperty("leadCountByAptitude")));
        Assert.Equal(speciesCount, Sum(root.GetProperty("shapeCount")));

        // Every lead names an allocatable aptitude and carries the leans it received, summing to
        // the same count — the per-primary view of the same population, not a second population.
        var leanByPrimary = root.GetProperty("leanByPrimary");
        foreach (var lead in root.GetProperty("leadCountByAptitude").EnumerateObject())
        {
            Assert.True(FusionRpg.Core.Stats.Aptitudes.AptitudeCatalog.IsAptitudeId(lead.Name));
            Assert.True(leanByPrimary.TryGetProperty(lead.Name, out var leans));
            Assert.Equal(lead.Value.GetInt64(), Sum(leans));
        }

        // `leanSignalsMissing` joins the same way: every entry names a species the plan carries and a
        // signal in the closed set. Envelope only — how MANY are missing is a reading.
        using var planDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "generated", "creatures", "_species-build-plan.json")));
        var planned = new HashSet<string>(
            planDoc.RootElement.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        foreach (var missing in root.GetProperty("leanSignalsMissing").EnumerateArray())
        {
            var parts = (missing.GetString() ?? "").Split(':');
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts[0], planned);
            Assert.Contains(parts[1], LeanSignals.Names);
        }
    }

    // ── 7. the balance publish's own relation, on the committed artifact ────────────────────────

    [Fact]
    public void The_committed_measure_shows_leans_varying_within_a_primary_and_inside_the_band()
    {
        // `per-species-lean` test 7 — a RELATION on the committed artifact, never a count. With the
        // mechanism shipped at zero weights every primary carried exactly ONE lean (the lean was
        // keyed to the primary); the balance publish keys it to the species, so at least one primary
        // must now carry two or more.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "generated", "creatures", "_species-build-measure.json")));
        var leanByPrimary = doc.RootElement.GetProperty("leanByPrimary");

        var withSeveral = leanByPrimary.EnumerateObject()
            .Count(p => p.Value.EnumerateObject().Count() >= 2);
        Assert.True(withSeveral > 0,
            "no primary carries two or more leans — the per-species lean is not reaching the plan");

        // Every lean is inside the shipped clamp band: the parity gate was sized for that band, and a
        // lean outside it would mean the plan and its tuning disagree.
        var tuning = SpeciesBuildTuningLoader.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "tuning", "species-build.v6.json")));
        foreach (var primary in leanByPrimary.EnumerateObject())
            foreach (var lean in primary.Value.EnumerateObject())
                Assert.InRange(
                    long.Parse(lean.Name, CultureInfo.InvariantCulture),
                    tuning.LeanMinPermille, tuning.LeanMaxPermille);
    }

    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so the `gk-core/data/tuning` read above stays valid once the injector source
        // moves to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }
}
