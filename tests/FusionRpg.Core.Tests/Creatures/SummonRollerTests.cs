using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

public class SummonRollerTests
{
    static SummonBannerDef Standard => SummonBannerCatalog.TryGet(SummonBannerCatalog.StandardRift)!;
    static SummonBannerDef Focus => SummonBannerCatalog.TryGet(SummonBannerCatalog.ElementFocus)!;

    static SeededRng Rng(ulong seed) => SeededRng.DeriveStream(seed, "gacha");

    [Fact]
    public void Roller_is_deterministic()
    {
        var (a, pa) = SummonRoller.Roll(Standard, null, 10, PityState.Fresh, Rng(7));
        var (b, pb) = SummonRoller.Roll(Standard, null, 10, PityState.Fresh, Rng(7));
        Assert.Equal(pa, pb);
        Assert.Equal(a.Select(x => (x.SpeciesId, x.Rarity, x.Variant, string.Join(',', x.TraitIds))),
                     b.Select(x => (x.SpeciesId, x.Rarity, x.Variant, string.Join(',', x.TraitIds))));
    }

    [Fact]
    public void Heirloom_hard_pity_fires_on_pull_25()
    {
        // 24 pulls without heirloom+ (ordinal 70) → the 25th must be heirloom-or-better regardless of the roll.
        var pity = new PityState(PullsSinceHeirloom: 24, PullsSinceSunwoven: 24);
        for (ulong seed = 0; seed < 20; seed++)
        {
            var (results, _) = SummonRoller.Roll(Standard, null, 1, pity, Rng(seed));
            Assert.True(CreatureRarityLadder.AtLeast(results[0].Rarity, CreatureRarity.Heirloom), $"seed {seed} gave {results[0].Rarity}");
        }
    }

    [Fact]
    public void Sunwoven_hard_pity_fires_on_pull_55()
    {
        var pity = new PityState(PullsSinceHeirloom: 0, PullsSinceSunwoven: 54);
        for (ulong seed = 0; seed < 20; seed++)
        {
            var (results, newPity) = SummonRoller.Roll(Standard, null, 1, pity, Rng(seed));
            Assert.Equal(CreatureRarity.Sunwoven, results[0].Rarity);
            Assert.Equal(0, newPity.PullsSinceSunwoven);
            Assert.Equal(0, newPity.PullsSinceHeirloom); // sunwoven (>= heirloom) resets both counters
        }
    }

    [Fact]
    public void Sunwoven_soft_ramp_raises_odds_after_pull_40()
    {
        // At counter 50 (pull 51): 8 + 11×60 = 668‰. Over many seeds most pulls are sunwoven.
        var ramped = new PityState(0, 50);
        var hits = 0;
        for (ulong seed = 0; seed < 200; seed++)
            if (SummonRoller.Roll(Standard, null, 1, ramped, Rng(seed)).Results[0].Rarity == CreatureRarity.Sunwoven)
                hits++;
        Assert.InRange(hits, 110, 200); // ~66.8% expected; far above the 0.8% base

        var fresh = PityState.Fresh;
        var freshHits = 0;
        for (ulong seed = 0; seed < 200; seed++)
            if (SummonRoller.Roll(Standard, null, 1, fresh, Rng(seed)).Results[0].Rarity == CreatureRarity.Sunwoven)
                freshHits++;
        Assert.InRange(freshHits, 0, 15); // ~0.8%
    }

    [Fact]
    public void Ten_pull_guarantees_cultivated_or_better()
    {
        // FloorTarget is Cultivated — Rare's own migration target (ssot-rarity.md §4.3),
        // preserving the OLD floor's actual GUARANTEE LEVEL (seed-to-concrete T4.1). A floor
        // targeting one of today's six EMPTY rungs (e.g. Sprout) would silently collapse to
        // Chaff via BandWithFallback's own empty-band search — found by this test failing
        // against the real (sparse) catalog before FloorTarget was corrected.
        const int rollCount = 10;
        for (ulong seed = 0; seed < 100; seed++)
        {
            var (results, _) = SummonRoller.Roll(Standard, null, rollCount, PityState.Fresh, Rng(seed));
            // Self-referential (population-pin SE3.3, 2026-09-19): this test asked for rollCount
            // rolls, so it proves none were lost, not a fact about shipped content.
            Assert.Equal(rollCount, results.Count);
            Assert.Contains(results, r => CreatureRarityLadder.AtLeast(r.Rarity, CreatureRarity.Cultivated));
        }
    }

    [Fact]
    public void Rarity_distribution_is_roughly_on_spec()
    {
        // Redesigned for the ten-rung ladder (seed-to-concrete T4.1). This measures the
        // EFFECTIVE, catalog-adjusted distribution, not the nominal probability table in
        // summoning.v1.json directly — only four of the ten rungs are populated today (Chaff,
        // Cultivated, Heirloom, Sunwoven, the four legacy-mapped ones), so BandWithFallback
        // redirects every empty rung's probability mass down to the nearest populated rung
        // below it. That is CORRECT behaviour (a species that doesn't exist cannot be handed
        // out), not a bug — but it means Sprout+Grafted's 400‰ folds into Chaff, Fused+
        // Chimeric's 100‰ folds into Cultivated, and Firstseed's 15‰ folds into Heirloom.
        // This test will need updating once species-generator (T4.4+) populates the other six
        // rungs — at that point it should assert against the nominal table directly instead.
        var counts = new Dictionary<CreatureRarity, int>();
        foreach (var r in CreatureRarityLadder.All) counts[r] = 0;
        var total = 0;
        var rng = Rng(123);
        for (var batch = 0; batch < 300; batch++)
        {
            // PityState.Fresh each batch so hard pity doesn't skew the base-rate measurement.
            var (results, _) = SummonRoller.Roll(Standard, null, 10, PityState.Fresh, rng);
            foreach (var r in results)
            {
                total++;
                counts[r.Rarity]++;
            }
        }

        // Effective per-mille AFTER the empty-rung fallback collapses onto today's four
        // populated rungs: chaff = 350(own) + 250(sprout) + 150(grafted) = 750;
        // cultivated = 100(own) + 60(fused) + 40(chimeric) = 200; heirloom = 25(own) + 15(firstseed) = 40;
        // sunwoven = 8(own, unaffected — nothing below it before heirloom folds in); almanac = 2(own).
        var expectedPerMille = new Dictionary<CreatureRarity, int>
        {
            [CreatureRarity.Chaff] = 750, [CreatureRarity.Cultivated] = 200,
            [CreatureRarity.Heirloom] = 40, [CreatureRarity.Sunwoven] = 8, [CreatureRarity.Almanac] = 2,
        };
        foreach (var (rarity, expected) in expectedPerMille)
        {
            var observed = counts[rarity] / (double)total * 1000;
            var tolerance = Math.Max(8, expected * 0.35);
            Assert.InRange(observed, Math.Max(0, expected - tolerance), expected + tolerance + 60);
        }
        // The six currently-empty rungs must never appear as a DELIVERED result — confirms the
        // fallback is doing its job, not silently handing out a species from an empty band.
        foreach (var empty in new[] { CreatureRarity.Sprout, CreatureRarity.Grafted, CreatureRarity.Fused,
                                      CreatureRarity.Chimeric, CreatureRarity.Firstseed })
            Assert.Equal(0, counts[empty]);
    }

    [Fact]
    public void Focus_banner_triples_focus_element_share_within_band()
    {
        var focusElement = ElementTypeId.Fire;
        var focusCount = 0; var total = 0;
        var rng = Rng(55);
        for (var i = 0; i < 400; i++)
        {
            var (results, _) = SummonRoller.Roll(Focus, focusElement, 1, PityState.Fresh, rng);
            if (results[0].Rarity != CreatureRarity.Chaff) continue; // measure within one band
            total++;
            var species = CreatureSpeciesCatalog.Get(results[0].SpeciesId);
            if (species.ElementPrimary == focusElement) focusCount++;
        }

        var commons = CreatureSpeciesCatalog.All.Count(s => s.BaseRarity == CreatureRarity.Chaff && s.Acquisition.HasFlag(CreatureAcquisition.Summonable));
        var fireCommons = CreatureSpeciesCatalog.All.Count(s => s.BaseRarity == CreatureRarity.Chaff && s.Acquisition.HasFlag(CreatureAcquisition.Summonable) && s.ElementPrimary == focusElement);
        Assert.True(fireCommons >= 1, "test needs at least one fire common in the catalog");
        var expected = fireCommons * 3.0 / (fireCommons * 3.0 + (commons - fireCommons));
        Assert.InRange(focusCount / (double)total, expected - 0.12, expected + 0.12);
    }

    [Fact]
    public void Capture_only_species_never_appear_in_pulls()
    {
        var rng = Rng(9);
        for (var i = 0; i < 100; i++)
        {
            var (results, _) = SummonRoller.Roll(Standard, null, 10, PityState.Fresh, rng);
            foreach (var r in results)
            {
                var s = CreatureSpeciesCatalog.Get(r.SpeciesId);
                Assert.True(s.Acquisition.HasFlag(CreatureAcquisition.Summonable), $"{s.SpeciesId} is not summonable");
            }
        }
    }

    [Fact]
    public void Trait_counts_follow_rarity()
    {
        var rng = Rng(77);
        for (var i = 0; i < 50; i++)
        {
            var (results, _) = SummonRoller.Roll(Standard, null, 10, PityState.Fresh, rng);
            foreach (var r in results)
            {
                // Read the real tuning-driven function directly (seed-to-concrete T4.1 moved this
                // off a hardcoded switch) rather than mirroring its values here a second time —
                // a stale mirror is exactly the defect this migration's own audit exists to catch.
                var expected = FusionRpg.Core.Creatures.Fusion.FusionRoller.SlotsFor(r.Rarity);
                var poolSize = CreatureSpeciesCatalog.Get(r.SpeciesId).TraitPool.Count;
                Assert.Equal(Math.Min(expected, poolSize), r.TraitIds.Count);
                Assert.Equal(r.TraitIds.Count, r.TraitIds.Distinct().Count());
            }
        }
    }

    [Fact]
    public void Focus_rotation_is_a_pure_function_of_date()
    {
        var d = new DateOnly(2026, 8, 21);
        Assert.Equal(SummonBannerCatalog.FocusFor(d), SummonBannerCatalog.FocusFor(d));
        // Rotation changes across weeks and cycles the full roster.
        var seen = new HashSet<ElementTypeId>();
        for (var w = 0; w < 6; w++)
            seen.Add(SummonBannerCatalog.FocusFor(d.AddDays(w * 7)));
        Assert.Equal(6, seen.Count);
    }
}
