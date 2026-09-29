using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>`species-build` T1.5/T1.6 (module 4, `redistribution-plan`) — the closed-form planner.
/// Pure and Core-only (no file IO in the type under test); this file's own real-corpus test is the one
/// place file IO appears, mirroring `SpeciesCatalogDiffTests`' established `RepoRoot()` convention.</summary>
public class SpeciesBuildPlannerTests
{
    static readonly SpeciesBuildTuning Tuning = new(
        SchemaVersion: 1, Version: 1,
        ParityFloorPermille: 50, ParityCeilingPermille: 200,
        LeanMinPermille: 350, LeanMaxPermille: 600,
        CrowdingFactor: 633, SecondarySharePermille: 300,
        MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
        RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
        UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
        LeanSignalWeights: LeanSignalWeights.Zero);

    /// <summary>No measured signals: every row reads the planner's own neutral fallback, which at
    /// zero weights is exactly the pre-signal formula these tests were written against.</summary>
    static readonly LeanSignalInputs NoSignals = new(Array.Empty<LeanSignalSet>(), Array.Empty<string>());

    static AnchorRow Anchor(
        string speciesId, string primary, string? secondary = null, bool pure = true,
        string side = "plant", int gameTypeId = 1) => new(
        SpeciesId: speciesId, Rarity: "chaff", ThreatBand: null,
        AptitudePrimary: primary, AptitudeSecondary: secondary, Pure: pure,
        AttackTempo: "steady", Reach: "melee", Variants: Array.Empty<string>(),
        Side: side, GameTypeId: gameTypeId, ElementPrimary: "fire", ElementSecondary: null,
        DeployMode: "PlantAvatar", Acquisition: new[] { "Summonable" }, Traits: Array.Empty<string>(),
        TargetPreference: "frontline");

    /// <summary>A small synthetic corpus with the SAME shape of imbalance the spec's own audit finding
    /// A7 describes: one aptitude ("Onslaught") massively over-represented, four barely present at
    /// all — deliberately not uniform, so a planner that ignored crowding would fail Phase 3 here too.</summary>
    static List<AnchorRow> SkewedCorpus(int count = 200)
    {
        var species = new List<AnchorRow>();
        for (var i = 0; i < count; i++)
        {
            // ~40% Onslaught, the rest spread thinly across the other eleven (including four that get
            // almost none), matching the real corpus's own measured skew (39.5% / four sharing 2.3%).
            var primary = i switch
            {
                _ when i % 100 < 40 => "Onslaught",
                _ when i % 100 < 42 => "Vigor",
                _ when i % 100 < 44 => "Might",
                _ when i % 100 < 46 => "Composure",
                _ when i % 100 < 48 => "Ferocity",
                _ => new[] { "Fortitude", "Agility", "Pierce", "Focus", "Bulwark", "Retribution", "Precision" }[i % 7]
            };
            species.Add(Anchor($"synth-{i:D4}", primary, pure: true));
        }
        return species;
    }

    [Fact]
    public void Determinism_two_runs_over_the_same_corpus_are_byte_identical()
    {
        var corpus = SkewedCorpus();
        var a = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);
        var b = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);
        Assert.Equal(SpeciesBuildPlanSerializer.Canonical(a.Vectors), SpeciesBuildPlanSerializer.Canonical(b.Vectors));
    }

    [Fact]
    public void Determinism_shuffled_input_order_produces_the_same_plan()
    {
        var corpus = SkewedCorpus();
        var shuffled = corpus.AsEnumerable().Reverse().ToList();
        // A second, differently-shuffled copy, not just reversed-once, so this isn't accidentally
        // insertion-order-preserving by luck of a single swap pattern.
        var rng = new Random(1234);
        var shuffled2 = corpus.OrderBy(_ => rng.Next()).ToList();

        var ordered = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);
        var reversed = SpeciesBuildPlanner.Plan(shuffled, Tuning, NoSignals);
        var random = SpeciesBuildPlanner.Plan(shuffled2, Tuning, NoSignals);

        var canonical = SpeciesBuildPlanSerializer.Canonical(ordered.Vectors);
        Assert.Equal(canonical, SpeciesBuildPlanSerializer.Canonical(reversed.Vectors));
        Assert.Equal(canonical, SpeciesBuildPlanSerializer.Canonical(random.Vectors));
    }

    [Fact]
    public void No_single_primary_every_vector_has_at_least_minAptitudesPerSpecies_non_zero_entries()
    {
        // An all-pure synthetic corpus (decision 3's own stated failure mode) must still satisfy it.
        var corpus = SkewedCorpus();
        var result = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);
        foreach (var v in result.Vectors)
            Assert.True(v.SharePermille.Count >= Tuning.MinAptitudesPerSpecies,
                $"{v.SpeciesId} has only {v.SharePermille.Count} non-zero entries");
    }

    [Fact]
    public void Every_vector_sums_to_exactly_1000_including_awkward_remainders()
    {
        // An odd species count (997) forces non-round crowding/remainder fractions everywhere.
        var corpus = SkewedCorpus(997);
        var result = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);
        foreach (var v in result.Vectors)
            Assert.Equal(1000, v.SharePermille.Values.Sum());
    }

    [Fact]
    public void The_favour_is_never_overridden_every_vectors_top_share_is_its_classified_primary()
    {
        var corpus = SkewedCorpus();
        var byId = corpus.ToDictionary(a => a.SpeciesId, a => a.AptitudePrimary, StringComparer.Ordinal);
        var result = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);
        foreach (var v in result.Vectors)
        {
            var top = v.SharePermille.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
            Assert.Equal(byId[v.SpeciesId], top.Key);
        }
    }

    [Fact]
    public void Pure_anchor_that_echoes_its_primary_as_secondary_never_corrupts_the_vector_sum()
    {
        // Real corpus defect found running the CLI for real (HypnoCattailGirl/ObsidianWallNut):
        // pure=true anchors that still set aptitudeSecondary == aptitudePrimary rather than the
        // "none" sentinel. Trusting AptitudeSecondary alone overwrites vector[primary] with a smaller
        // secondary share via the same dictionary key, corrupting the sum below 1000.
        var corpus = SkewedCorpus();
        corpus.Add(Anchor("echo-secondary", "Vigor", secondary: "Vigor", pure: true));
        var result = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);
        var vector = result.Vectors.Single(v => v.SpeciesId == "echo-secondary");
        Assert.Equal(1000, vector.SharePermille.Values.Sum());
        Assert.Equal("Vigor", vector.SharePermille.OrderByDescending(kv => kv.Value).First().Key);
    }

    [Fact]
    public void Refusal_deliberately_infeasible_tunables_name_the_offending_aptitudes()
    {
        // A ceiling far below what Onslaught's crowding alone forces even at leanMin.
        var infeasible = Tuning with { ParityCeilingPermille = 50 };
        var corpus = SkewedCorpus();
        var ex = Assert.Throws<SpeciesBuildRefusal>(() => SpeciesBuildPlanner.Plan(corpus, infeasible, NoSignals));
        Assert.Contains("Onslaught", ex.OffendingShares.Keys);
        Assert.Contains("Onslaught", ex.Message);
    }

    [Fact]
    public void Crowding_behaves_a_crowded_primary_leans_measurably_less_than_a_rare_one()
    {
        var corpus = SkewedCorpus();
        var result = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);

        var crowdedLean = result.Vectors.First(v => v.SharePermille.ContainsKey("Onslaught")
            && v.SharePermille["Onslaught"] == v.SharePermille.Values.Max()).SharePermille["Onslaught"];
        // Ferocity is one of the four barely-represented primaries (2/100 of the corpus).
        var rareVector = result.Vectors.First(v => corpus.Single(a => a.SpeciesId == v.SpeciesId).AptitudePrimary == "Ferocity");
        var rareLean = rareVector.SharePermille["Ferocity"];

        Assert.True(rareLean > crowdedLean,
            $"rare-primary lean ({rareLean}) should exceed crowded-primary lean ({crowdedLean})");
    }

    [Fact]
    public void Overflow_an_extreme_corpus_throws_rather_than_wraps()
    {
        // Not an extreme species COUNT (that's just slow) but an extreme TUNING value multiplied
        // against a real permille, forcing the widened multiply in Phase 1 past long range.
        var corpus = SkewedCorpus(10);
        var extreme = Tuning with { CrowdingFactor = long.MaxValue / 10 };
        Assert.Throws<OverflowException>(() => SpeciesBuildPlanner.Plan(corpus, extreme, NoSignals));
    }

    [Fact]
    public void Band_is_satisfied_on_the_real_corpus()
    {
        // The acceptance test (spec's own success criterion #2) — pass/fail, not a report. Reads the
        // real classified anchors and the real shipped tuning, exactly as gk-forge/tools/CreatureBuildPlanGen does.
        var repoRoot = RepoRoot();
        var seedRoot = Path.Combine(repoRoot, "data", "seed", "creatures", "species");
        var realTuning = SpeciesBuildTuningLoader.Parse(
            File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "species-build.v6.json")));

        var anchors = new List<AnchorRow>();
        foreach (var file in Directory.GetFiles(seedRoot, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            anchors.AddRange(AnchorRowReader.ReadAll(File.ReadAllText(file)));
        }
        var resolved = anchors.Where(a => SpeciesExpander.UnresolvedFields(a).Count == 0).ToList();
        Assert.NotEmpty(resolved);

        // The same inputs the tool feeds the planner, over the same one population filter — so a
        // refusal here is a refusal of the real pipeline, not of a synthetic corpus.
        var signals = LeanSignals.Compute(
            BuildFavourMeasurer.RosterPopulation(resolved),
            BaseStatDump.Parse(File.ReadAllText(Path.Combine(
                repoRoot, "data", "seed", "creatures", "_dump", "type-base-stats.json"))),
            CreatureThreatTuningLoader.Parse(File.ReadAllText(Path.Combine(
                repoRoot, "data", "tuning", "creature-threat.v2.json"))).Thresholds);

        // Throws SpeciesBuildRefusal (test failure) if the real corpus falls outside the shipped band.
        var result = SpeciesBuildPlanner.Plan(resolved, realTuning, signals);
        // Reads AptitudeCatalog.Count live rather than a second 12 (population-pin SE3.3,
        // 2026-09-19) -- Plan() keys the share table by aptitude id off this same catalog.
        Assert.Equal(FusionRpg.Core.Stats.Aptitudes.AptitudeCatalog.Count, result.CorpusSharePermille.Count);
    }

    // ── EP2.5: the per-species lean and its signal weights ──────────────────────────────────────

    /// <summary>Two species on every aptitude: crowding is even, so every lean sits well inside the
    /// band and a signal can move one species without hitting the clamp.</summary>
    static List<AnchorRow> BalancedCorpus()
    {
        var species = new List<AnchorRow>();
        foreach (var aptitude in AptitudeCatalog.All)
            for (var i = 0; i < 2; i++)
                species.Add(Anchor($"{aptitude.Id}-{i}", aptitude.Id, pure: true));
        return species;
    }

    static LeanSignalInputs SignalsFor(
        IReadOnlyList<AnchorRow> corpus, Func<AnchorRow, long> specialisation) =>
        new(corpus.Select(a => new LeanSignalSet(a.SpeciesId, specialisation(a), Pure: 1000, ThreatRung: 500)).ToArray(),
            Array.Empty<string>());

    [Fact]
    public void Zero_weights_plan_exactly_the_pre_signal_formula_derived_in_this_body()
    {
        var corpus = SkewedCorpus();
        var result = SpeciesBuildPlanner.Plan(corpus, Tuning, NoSignals);

        foreach (var vector in result.Vectors)
        {
            var anchor = corpus.Single(a => a.SpeciesId == vector.SpeciesId);
            // The OLD formula, derived here rather than pasted: one lean per primary from that
            // primary's own crowding. With the penalty at 0 the signal is irrelevant to the number.
            var count = corpus.Count(a => a.AptitudePrimary == anchor.AptitudePrimary);
            long crowdingPermille;
            checked { crowdingPermille = count * 1000L / corpus.Count; }
            long contribution;
            checked { contribution = Tuning.CrowdingFactor * crowdingPermille / 1000; }
            var expectedLean = Math.Clamp(
                Tuning.LeanMaxPermille - contribution, Tuning.LeanMinPermille, Tuning.LeanMaxPermille);

            Assert.Equal(expectedLean, vector.SharePermille[anchor.AptitudePrimary]);
            Assert.Equal(1000, vector.SharePermille.Values.Sum());
        }
    }

    [Fact]
    public void Zero_weights_make_the_plan_byte_identical_whatever_the_signals_say()
    {
        // The refactor's proof: the mechanism ships with every weight at 0, so the signals cannot
        // reach the numbers at all — two opposite signal sets plan the same bytes.
        var corpus = BalancedCorpus();
        var allSpecialists = SpeciesBuildPlanner.Plan(
            corpus, Tuning, SignalsFor(corpus, _ => 1000));
        var allGeneralists = SpeciesBuildPlanner.Plan(
            corpus, Tuning, SignalsFor(corpus, _ => 0));

        Assert.Equal(
            SpeciesBuildPlanSerializer.Canonical(allSpecialists.Vectors),
            SpeciesBuildPlanSerializer.Canonical(allGeneralists.Vectors));
    }

    [Fact]
    public void A_specialist_leans_sharper_than_a_generalist_on_the_first_shared_primary()
    {
        var corpus = BalancedCorpus();
        var onPrimary = corpus.Where(a => a.AptitudePrimary == "Onslaught").ToArray();
        var specialist = onPrimary[0].SpeciesId;
        var weighted = Tuning with { LeanSignalWeights = new LeanSignalWeights(150, 0, 0) };
        var signals = SignalsFor(corpus, a => a.SpeciesId == specialist ? 1000 : 0);

        var result = SpeciesBuildPlanner.Plan(corpus, weighted, signals);
        var byId = result.Vectors.ToDictionary(v => v.SpeciesId);

        // Specialisation 1000 pays nothing; 0 pays the full weight. Same primary, so the crowding
        // term is identical and the difference is exactly the weight.
        Assert.Equal(150, byId[specialist].SharePermille["Onslaught"] - byId[onPrimary[1].SpeciesId].SharePermille["Onslaught"]);
    }

    [Fact]
    public void An_out_of_band_weight_is_still_caught_by_phase_3_not_by_a_new_check()
    {
        // A negative weight is outside the loader's own contract (`leanSignalWeights.*` ≥ 0), so it
        // can only be constructed. It sharpens every lean to leanMax, and the EXISTING Phase 3 band
        // check refuses the plan — the gate stays Phase 3, and the planner grows no ad-hoc check.
        var corpus = SkewedCorpus();
        var sharpening = Tuning with { LeanSignalWeights = new LeanSignalWeights(-200, 0, 0) };

        var ex = Assert.Throws<SpeciesBuildRefusal>(() => SpeciesBuildPlanner.Plan(
            corpus, sharpening, SignalsFor(corpus, _ => 0)));

        Assert.Contains("Onslaught", ex.OffendingShares.Keys);
    }

    [Fact]
    public void Signals_do_not_break_input_order_independence()
    {
        var corpus = BalancedCorpus();
        var weighted = Tuning with { LeanSignalWeights = new LeanSignalWeights(120, 40, 30) };
        var signals = new LeanSignalInputs(
            corpus.Select((a, i) => new LeanSignalSet(a.SpeciesId, i % 1000, 500, 500)).ToArray(),
            Array.Empty<string>());

        var ordered = SpeciesBuildPlanner.Plan(corpus, weighted, signals);
        var reversed = SpeciesBuildPlanner.Plan(
            corpus.AsEnumerable().Reverse().ToArray(), weighted,
            new LeanSignalInputs(signals.Sets.Reverse().ToArray(), Array.Empty<string>()));

        Assert.Equal(
            SpeciesBuildPlanSerializer.Canonical(ordered.Vectors),
            SpeciesBuildPlanSerializer.Canonical(reversed.Vectors));
    }

    // ── EP2.14: Phase 4 — the lead and shape caps (R-Q6) ────────────────────────────────────────

    [Fact]
    public void Phase_4_refuses_a_plan_whose_lead_breaches_the_cap_and_names_it()
    {
        // The skew forces a 400 permille lead on Onslaught; the shape half is loosened so ONLY the lead
        // cap can be the cause. The message names the aptitude, the cap, the tolerance and the sum.
        var capped = Tuning with
        {
            LeadCapPermille = 250, LeadCapTolerancePermille = 50, ShapeCapPermille = 1000,
        };

        var ex = Assert.Throws<SpeciesBuildRefusal>(() => SpeciesBuildPlanner.Plan(
            SkewedCorpus(), capped, NoSignals));

        Assert.Contains("lead cap", ex.Message);
        Assert.Contains("Onslaught", ex.Message);
        Assert.Contains("250", ex.Message);
        Assert.Equal("Onslaught", Assert.Single(ex.OffendingShares).Key);
    }

    [Fact]
    public void Phase_4_refuses_a_plan_whose_largest_shape_breaches_the_cap_and_names_it()
    {
        // `SkewedCorpus` is all-pure, so nearly every species carries the SAME profile: the shape half is
        // the breach. The lead half is loosened so only the shape cap can be the cause.
        var capped = Tuning with
        {
            LeadCapPermille = 1000, LeadCapTolerancePermille = 0, ShapeCapPermille = 200,
        };

        var ex = Assert.Throws<SpeciesBuildRefusal>(() => SpeciesBuildPlanner.Plan(
            SkewedCorpus(), capped, NoSignals));

        Assert.Contains("shape cap", ex.Message);
        Assert.Contains("200", ex.Message);
        // The offending reading is the largest shape's own per-mille share, over the cap it breached.
        Assert.True(Assert.Single(ex.OffendingShares).Value > 200);
    }

    [Fact]
    public void Phase_4_has_no_floor_an_aptitude_led_by_nobody_passes()
    {
        // R-Q6: "No floor on any aptitude" — the gate is a maximum only. Eleven aptitudes are led here
        // and the twelfth (Ferocity) by nobody at all; with both caps satisfied the plan must not refuse.
        var eleven = AptitudeCatalog.All.Select(a => a.Id).Where(id => id != "Ferocity").ToArray();
        var corpus = new List<AnchorRow>();
        for (var i = 0; i < 88; i++)
            corpus.Add(Anchor($"bal-{i:D3}", eleven[i % eleven.Length], pure: i % 2 == 0));
        var capped = Tuning with
        {
            LeadCapPermille = 250, LeadCapTolerancePermille = 50, ShapeCapPermille = 1000,
        };

        var result = SpeciesBuildPlanner.Plan(corpus, capped, NoSignals);

        var measured = BuildFavourMeasurer.Measure(result, corpus, Array.Empty<string>());
        Assert.False(measured.LeadCountByAptitude.ContainsKey("Ferocity"));
        Assert.True(measured.MaxLeadPermille <= 300);   // the gate it did apply
    }

    [Fact]
    public void Phase_4_is_green_on_the_committed_corpus_with_the_shipped_caps()
    {
        // The pair's own acceptance: with v5's caps in the tuning the real corpus plans WITHOUT refusing,
        // and the two numbers the gate reads are the ones the committed measure artifact reports.
        var repoRoot = RepoRoot();
        var realTuning = SpeciesBuildTuningLoader.Parse(
            File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "species-build.v6.json")));
        var anchors = new List<AnchorRow>();
        foreach (var file in Directory.GetFiles(
                     Path.Combine(repoRoot, "data", "seed", "creatures", "species"), "*.json",
                     SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            anchors.AddRange(AnchorRowReader.ReadAll(File.ReadAllText(file)));
        }
        var resolved = anchors.Where(a => SpeciesExpander.UnresolvedFields(a).Count == 0).ToList();
        var signals = LeanSignals.Compute(
            BuildFavourMeasurer.RosterPopulation(resolved),
            BaseStatDump.Parse(File.ReadAllText(Path.Combine(
                repoRoot, "data", "seed", "creatures", "_dump", "type-base-stats.json"))),
            CreatureThreatTuningLoader.Parse(File.ReadAllText(Path.Combine(
                repoRoot, "data", "tuning", "creature-threat.v2.json"))).Thresholds);

        var result = SpeciesBuildPlanner.Plan(resolved, realTuning, signals);

        var measured = BuildFavourMeasurer.Measure(result, resolved, signals.Missing);
        Assert.True(measured.MaxLeadPermille <= realTuning.LeadRefusalPermille,
            $"max lead {measured.MaxLeadPermille} over {realTuning.LeadRefusalPermille}");
        Assert.True(measured.LargestShapePermille <= realTuning.ShapeCapPermille,
            $"largest shape {measured.LargestShapePermille} over {realTuning.ShapeCapPermille}");
    }

    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so the `gk-core/data/tuning` reads below stay valid once the injector source
        // moves to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }
}
