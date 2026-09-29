using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures.Fusion;

/// <summary>
/// T8.3 (`creature-seed` module 17 `fusion-recipe-reconcile`, spec-fusion-recipe-generator.md §3) —
/// the C#-side seam `gk-forge/tools/CreatureRecipeReconcileInput` exposes to
/// `seedsmith/adapters/creatures/fusion/reconcile.py`: <see cref="CreatureRecipeCatalog.EligibleOutputs"/>,
/// <see cref="CreatureRecipeCatalog.UnresolvedOutputs"/>, <see cref="CreatureRecipeCatalog.CandidatePoolBelow"/>.
/// The PYTHON-side reconciliation logic (voting integration, validation refusals, freeze-on-commit,
/// `--check`) has its own tests in `gk-forge/tools/seedsmith/tests/test_fusion_recipe.py` — this file proves
/// only the real data these Python functions consume is correct, using the real corpus
/// (<see cref="RealCorpusFixture"/>) and small hand-built fixtures (<see cref="SyntheticSpecies"/>).
/// </summary>
public class FusionRecipeReconcileTests
{
    [Fact]
    public void EligibleOutputs_on_the_real_corpus_matches_the_sum_of_every_rungs_output_count()
    {
        using (CreatureSpeciesCatalog.UseScoped(RealCorpusFixture.Snapshot))
        {
            var eligible = CreatureRecipeCatalog.EligibleOutputs();

            // Cross-checked against an INDEPENDENT recomputation (the same predicate
            // FusionRecipeDistributionIndex.Compute() sums per rung) rather than a bare literal —
            // a real future corpus change should move both sides together, never just one.
            var expected = CreatureSpeciesCatalog.All.Count(s =>
                CreatureRarityLadder.AtLeast(s.BaseRarity, CreatureRecipeCatalog.OutputEligibilityFloor)
                && s.Acquisition != CreatureAcquisition.CaptureOnly);
            // The scale (775 today, a growing species population -- 840 -> 903 -> 904 generatable
            // species, 713 -> 774 -> 775 eligible outputs) is fully covered by the independent
            // recomputation above; never pinned separately (population-pin SE3.3, 2026-09-19).
            Assert.Equal(expected, eligible.Count);

            Assert.All(eligible, s => Assert.True(CreatureRarityLadder.AtLeast(s.BaseRarity, CreatureRecipeCatalog.OutputEligibilityFloor)));
            Assert.All(eligible, s => Assert.NotEqual(CreatureAcquisition.CaptureOnly, s.Acquisition));
        }
    }

    [Fact]
    public void UnresolvedOutputs_on_the_real_corpus_is_exactly_the_16_known_Almanac_deficits()
    {
        using (CreatureSpeciesCatalog.UseScoped(RealCorpusFixture.Snapshot))
        {
            var unresolved = CreatureRecipeCatalog.UnresolvedOutputs();

            // The scale (16 today, a growing species population -- 14 -> 16 with the full 903-species
            // corpus) is fully covered by the independent recomputation below; never pinned separately
            // (population-pin SE3.3, 2026-09-19).
            Assert.All(unresolved, s => Assert.Equal(CreatureRarity.Almanac, s.BaseRarity));

            // Independently recomputed (eligible minus covered), not just the count — proves
            // UnresolvedOutputs() is genuinely "EligibleOutputs() minus a fresh Build()'s own
            // coverage", not a hardcoded Almanac special case. Uses BuildForTest() here too, not the
            // cached All — see UnresolvedOutputs()'s own doc comment on why a cached read is unsafe
            // to trust inside a test process that scopes multiple rosters.
            var covered = CreatureRecipeCatalog.BuildDeterministicOnly().Select(r => r.OutputSpeciesId).ToHashSet(StringComparer.Ordinal);
            var expected = CreatureRecipeCatalog.EligibleOutputs().Where(o => !covered.Contains(o.SpeciesId))
                .Select(o => o.SpeciesId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, unresolved.Select(o => o.SpeciesId).ToList());

            // Pinned to SpeciesId ordinal (EligibleOutputs()'s own order) — a reproducible fact, not
            // an accident of set enumeration order.
            Assert.Equal(unresolved.Select(o => o.SpeciesId).OrderBy(x => x, StringComparer.Ordinal), unresolved.Select(o => o.SpeciesId));
        }
    }

    [Fact]
    public void UnresolvedOutputs_on_a_synthetic_shortfall_fixture_names_exactly_the_uncovered_outputs()
    {
        // 2 inputs support only C(2,2)=1 pair; 3 outputs need one each. Build() processes outputs in
        // SpeciesId order, so "top-a" (first) claims the one available pair and "top-b"/"top-c" are
        // left uncovered — the exact scenario reconcile.py's deficit discovery must name correctly.
        var roster = new[]
        {
            SyntheticSpecies.Make("below-a", CreatureRarity.Grafted, CreatureAcquisition.Summonable, 10_001),
            SyntheticSpecies.Make("below-b", CreatureRarity.Grafted, CreatureAcquisition.Summonable, 10_002),
            SyntheticSpecies.Make("top-a", CreatureRarity.Cultivated, CreatureAcquisition.Summonable, 10_003),
            SyntheticSpecies.Make("top-b", CreatureRarity.Cultivated, CreatureAcquisition.Summonable, 10_004),
            SyntheticSpecies.Make("top-c", CreatureRarity.Cultivated, CreatureAcquisition.Summonable, 10_005),
        };

        using (CreatureSpeciesCatalog.UseScoped(roster))
        {
            var unresolved = CreatureRecipeCatalog.UnresolvedOutputs();
            Assert.Equal(new[] { "top-b", "top-c" }, unresolved.Select(o => o.SpeciesId));
        }
    }

    [Fact]
    public void CandidatePoolBelow_on_the_real_Almanac_case_matches_the_known_pool_shape()
    {
        using (CreatureSpeciesCatalog.UseScoped(RealCorpusFixture.Snapshot))
        {
            var pool = CreatureRecipeCatalog.CandidatePoolBelow(CreatureRarity.Almanac, maxPopulatedRungs: 3);

            var distance1 = pool.Count(p => p.RungDistance == 1);
            var distance2 = pool.Count(p => p.RungDistance == 2);
            var distance3 = pool.Count(p => p.RungDistance == 3);
            Assert.Equal(4, distance1);
            Assert.Equal(4, distance2);
            Assert.Equal(45, distance3);
            // Formula, not a literal (population-pin SE3.3, 2026-09-19): the scale (53 today, a
            // growing species population -- verified live 2026-09-07 against the full 903-species
            // corpus) is the sum of the three distance buckets above, and maxPopulatedRungs: 3 makes
            // that partition exhaustive -- so this also proves no pool member falls outside it.
            Assert.Equal(distance1 + distance2 + distance3, pool.Count);

            Assert.All(pool.Where(p => p.RungDistance == 1), p => Assert.Equal(CreatureRarity.Sunwoven, p.Species.BaseRarity));
            Assert.All(pool.Where(p => p.RungDistance == 2), p => Assert.Equal(CreatureRarity.Firstseed, p.Species.BaseRarity));
            Assert.All(pool.Where(p => p.RungDistance == 3), p => Assert.Equal(CreatureRarity.Heirloom, p.Species.BaseRarity));

            // Distance-1 is exactly what NearestPopulatedRungBelow alone returns — the two seams
            // must never disagree about "the" nearest rung.
            var direct = CreatureRecipeCatalog.NearestPopulatedRungBelow(CreatureRarity.Almanac);
            Assert.Equal(direct.Select(s => s.SpeciesId).OrderBy(x => x, StringComparer.Ordinal),
                pool.Where(p => p.RungDistance == 1).Select(p => p.Species.SpeciesId).OrderBy(x => x, StringComparer.Ordinal));

            Assert.All(pool, p => Assert.NotEqual(CreatureAcquisition.CaptureOnly, p.Species.Acquisition));
        }
    }

    [Fact]
    public void CandidatePoolBelow_never_shows_more_than_the_requested_number_of_populated_rungs()
    {
        // Four consecutive populated rungs below the output (Cultivated/Grafted/Sprout/Chaff, one
        // species each) — with maxPopulatedRungs=3 the 4th (Chaff) must never appear, proving the
        // bound is enforced by the walk itself, not just by a caller's own restraint.
        var roster = new[]
        {
            SyntheticSpecies.Make("chaff-1", CreatureRarity.Chaff, CreatureAcquisition.Summonable, 10_001),
            SyntheticSpecies.Make("sprout-1", CreatureRarity.Sprout, CreatureAcquisition.Summonable, 10_002),
            SyntheticSpecies.Make("grafted-1", CreatureRarity.Grafted, CreatureAcquisition.Summonable, 10_003),
            SyntheticSpecies.Make("cultivated-1", CreatureRarity.Cultivated, CreatureAcquisition.Summonable, 10_004),
            SyntheticSpecies.Make("fused-out", CreatureRarity.Fused, CreatureAcquisition.Summonable, 10_005),
        };

        using (CreatureSpeciesCatalog.UseScoped(roster))
        {
            var pool = CreatureRecipeCatalog.CandidatePoolBelow(CreatureRarity.Fused, maxPopulatedRungs: 3);

            Assert.Equal(3, pool.Count);
            Assert.DoesNotContain(pool, p => p.Species.SpeciesId == "chaff-1");
            Assert.Equal(new[] { "cultivated-1", "grafted-1", "sprout-1" },
                pool.OrderBy(p => p.RungDistance).Select(p => p.Species.SpeciesId));
        }
    }

    [Fact]
    public void CandidatePoolBelow_stops_at_the_bottom_rung_even_short_of_the_requested_max()
    {
        // Only Chaff is populated below Cultivated (Grafted/Sprout both empty) — the walk must stop
        // at the ladder's bottom rather than looping or throwing when max=3 is never satisfied.
        var roster = new[]
        {
            SyntheticSpecies.Make("chaff-1", CreatureRarity.Chaff, CreatureAcquisition.Summonable, 10_001),
            SyntheticSpecies.Make("cultivated-out", CreatureRarity.Cultivated, CreatureAcquisition.Summonable, 10_002),
        };

        using (CreatureSpeciesCatalog.UseScoped(roster))
        {
            var pool = CreatureRecipeCatalog.CandidatePoolBelow(CreatureRarity.Cultivated, maxPopulatedRungs: 3);

            var single = Assert.Single(pool);
            Assert.Equal("chaff-1", single.Species.SpeciesId);
            Assert.Equal(1, single.RungDistance);
        }
    }
}
