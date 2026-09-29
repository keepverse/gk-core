using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

public class CreatureTraitPoolCurationTests
{
    [Fact]
    public void A_species_still_present_in_the_legacy_compiled_catalog_ports_its_trait_pool_forward_verbatim()
    {
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        var legacy = CreatureSpeciesCatalog.Get("peashooter").TraitPool;

        // Rarity/gameTypeId are deliberately wrong here — the legacy port-forward path keys ONLY on
        // speciesId, so a mismatched rarity/gameTypeId must not change the result.
        var curated = CreatureTraitPoolCuration.PickFor("peashooter", CreatureRarity.Almanac, gameTypeId: -1);

        Assert.Equal(legacy, curated);
        Assert.NotEmpty(curated);
    }

    [Fact]
    public void Species_id_matching_is_case_and_whitespace_insensitive_like_the_rest_of_the_pipeline()
    {
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        var legacy = CreatureSpeciesCatalog.Get("peashooter").TraitPool;

        var curated = CreatureTraitPoolCuration.PickFor(" Peashooter ", CreatureRarity.Cultivated, gameTypeId: 0);

        Assert.Equal(legacy, curated);
    }

    [Fact]
    public void A_species_absent_from_the_legacy_catalog_gets_a_deterministic_non_empty_pick()
    {
        var first = CreatureTraitPoolCuration.PickFor("not-a-real-legacy-species", CreatureRarity.Cultivated, gameTypeId: 4242);
        var second = CreatureTraitPoolCuration.PickFor("not-a-real-legacy-species", CreatureRarity.Cultivated, gameTypeId: 4242);

        Assert.NotEmpty(first);
        Assert.Equal(first, second); // same inputs, same pick — required for --check byte-identical regen
    }

    [Theory]
    [InlineData(CreatureRarity.Chaff)]
    [InlineData(CreatureRarity.Cultivated)]
    [InlineData(CreatureRarity.Heirloom)]
    [InlineData(CreatureRarity.Sunwoven)]
    [InlineData(CreatureRarity.Almanac)]
    public void Every_hash_picked_trait_is_a_known_CreatureTraitCatalog_member(CreatureRarity rarity)
    {
        for (var gameTypeId = 0; gameTypeId < 25; gameTypeId++)
        {
            var picked = CreatureTraitPoolCuration.PickFor($"synthetic-{gameTypeId}", rarity, gameTypeId);
            foreach (var trait in picked)
                Assert.True(CreatureTraitCatalog.IsKnown(trait), $"'{trait}' (rarity={rarity}, gameTypeId={gameTypeId})");
        }
    }

    [Fact]
    public void Rarity_at_least_Heirloom_adds_an_essence_trait_below_Heirloom_does_not()
    {
        var below = CreatureTraitPoolCuration.PickFor("synthetic-below", CreatureRarity.Cultivated, gameTypeId: 7);
        var atHeirloom = CreatureTraitPoolCuration.PickFor("synthetic-at", CreatureRarity.Heirloom, gameTypeId: 7);

        Assert.DoesNotContain("void-touched", below);
        Assert.DoesNotContain("chaos-marked", below);
        Assert.True(atHeirloom.Contains("void-touched") || atHeirloom.Contains("chaos-marked"));
    }

    [Fact]
    public void Only_the_top_rung_rarity_adds_immortal()
    {
        var sunwoven = CreatureTraitPoolCuration.PickFor("synthetic-sw", CreatureRarity.Sunwoven, gameTypeId: 3);
        var almanac = CreatureTraitPoolCuration.PickFor("synthetic-al", CreatureRarity.Almanac, gameTypeId: 3);

        Assert.DoesNotContain("immortal", sunwoven);
        Assert.Contains("immortal", almanac);
    }
}
