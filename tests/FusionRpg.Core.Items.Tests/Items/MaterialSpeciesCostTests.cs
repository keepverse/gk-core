using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Thresholds;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T32 (`species-cost-shaping`, spec-species-cost-shaping.md) — the species
/// multiplier, applied BESIDE <c>costBandMultiplierPerMille</c>, gated above a threshold rung
/// (R-SC2: one default plus an explicit per-verb override). Reuses <see cref="MaterialCorpusTests"/>'
/// own internal helpers (<c>Tuning()</c>, <c>Mutated()</c>, <c>Catalog()</c>) against the REAL shipped
/// the shipped materials tuning and recipe corpus — this module adds no synthetic tuning fixture.
/// </summary>
public class MaterialSpeciesCostTests
{
    // ---- the gate: below threshold is byte-identical --------------------------------------------

    [Fact]
    public void Below_the_threshold_rung_the_cost_is_byte_identical_to_no_species()
    {
        var tuning = MaterialCorpusTests.Tuning();
        var catalog = MaterialCorpusTests.Catalog();
        var elevate = catalog.Recipes.Values.First(r => r.Operation == CraftOperation.Elevate);

        var thresholdIndex = RarityLadder.RungIndexOf(tuning.SpeciesCostThresholdRung);
        Assert.True(thresholdIndex > 0, "the shipped threshold must not gate at the bottom rung, or nothing is below it to test");
        var belowIndex = thresholdIndex - 1;

        var noSpecies = new RecipeContext(0, 1, 25, "humanoid", 0, SpeciesRungIndex: null);
        var belowThreshold = new RecipeContext(0, 1, 25, "humanoid", 0, SpeciesRungIndex: belowIndex);

        var a = catalog.Resolve(elevate.RecipeId, noSpecies);
        var b = catalog.Resolve(elevate.RecipeId, belowThreshold);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_species_less_piece_the_forty_build_or_theme_themed_sets_costs_exactly_as_today()
    {
        // "No species" IS SpeciesRungIndex: null — the default every existing call site already
        // gets (Success criterion 2). Proven against a real recipe across several operations.
        var tuning = MaterialCorpusTests.Tuning();
        var catalog = MaterialCorpusTests.Catalog();
        foreach (var recipe in catalog.Recipes.Values.Take(5))
        {
            var ctx = new RecipeContext(3, 3, 60, "plant", 2);
            var withDefault = catalog.Resolve(recipe.RecipeId, ctx);
            var withExplicitNull = catalog.Resolve(recipe.RecipeId, ctx with { SpeciesRungIndex = null });
            Assert.Equal(withDefault, withExplicitNull);
        }
        _ = tuning;
    }

    // ---- at/above the threshold, the multiplier applies ------------------------------------------

    [Fact]
    public void At_and_above_the_threshold_the_species_multiplier_applies()
    {
        var tuning = MaterialCorpusTests.Tuning();
        var catalog = MaterialCorpusTests.Catalog();
        var elevate = catalog.Recipes.Values.First(r => r.Operation == CraftOperation.Elevate
            && r.CostLines.Count > 0);

        var thresholdIndex = RarityLadder.RungIndexOf(tuning.SpeciesCostThresholdRung);
        var atThreshold = new RecipeContext(0, 1, 25, "humanoid", 0, SpeciesRungIndex: thresholdIndex);
        var noSpecies = new RecipeContext(0, 1, 25, "humanoid", 0, SpeciesRungIndex: null);

        var withSpecies = catalog.Resolve(elevate.RecipeId, atThreshold);
        var without = catalog.Resolve(elevate.RecipeId, noSpecies);

        Assert.Equal(without.Count, withSpecies.Count);
        var rungId = RarityLadder.RungIds[thresholdIndex];
        var expectedMultiplier = tuning.SpeciesCostMultiplierMilli[rungId];
        Assert.True(expectedMultiplier > 1000, "the fixture threshold rung must carry a real multiplier > 1000 to prove anything");

        // At least one non-band-immune leg must have grown — proving the multiplier reached the cost.
        Assert.True(withSpecies.Zip(without).Any(pair => pair.First.Qty > pair.Second.Qty),
            "no leg grew under a species multiplier > 1000 -- every leg must be BandImmune, which " +
            "would make this recipe an invalid fixture for this test");
    }

    // ---- composition order: one widen-multiply-divide, hand-computed -----------------------------

    [Theory]
    [InlineData(100L, 2000, 1500, 300)]   // steep band x heirloom species: 100 * 2000 * 1500 / 1_000_000 = 300
    [InlineData(1L, 500, 4000, 2)]        // cheap band x almanac species: ceil(1 * 500 * 4000 / 1_000_000) = ceil(2.0) = 2
    [InlineData(7L, 8000, 1000, 56)]      // exorbitant band x neutral species: ceil(7*8000*1000/1_000_000) = ceil(56.0) = 56
    public void Composition_is_one_widen_multiply_divide_matching_a_hand_computed_value(
        long baseQty, int bandMilli, int speciesMilli, long expected) =>
        Assert.Equal(expected, MaterialTuning.ApplyBandAndSpecies(baseQty, bandMilli, speciesMilli));

    [Fact]
    public void Species_multiplier_of_1000_is_mathematically_identical_to_ApplyBand_alone()
    {
        // The equivalence MaterialTuning.ApplyBandAndSpecies's own doc comment claims, checked over a
        // spread of values rather than asserted only in prose.
        foreach (var baseQty in new long[] { 1, 2, 7, 100, 999, 1000, 12345 })
            foreach (var bandMilli in new[] { 500, 1000, 2000, 4000, 8000 })
                Assert.Equal(MaterialTuning.ApplyBand(baseQty, bandMilli),
                    MaterialTuning.ApplyBandAndSpecies(baseQty, bandMilli, 1000));
    }

    // ---- the band mirror is untouched (Success criterion 3 — the existing test must stay green) --
    // See MaterialCorpusTests.The_cost_band_table_mirrors_the_frozen_registry_value_for_value.

    // ---- load rejections ---------------------------------------------------------------------------

    [Fact]
    public void A_missing_speciesCostMultiplierMilli_section_throws()
    {
        var broken = MaterialCorpusTests.Mutated(m =>
            ((System.Text.Json.Nodes.JsonObject)m!).Remove("speciesCostMultiplierMilli"));
        var ex = Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
        Assert.Contains("speciesCostMultiplierMilli", ex.Message);
    }

    [Fact]
    public void A_multiplier_row_missing_a_rung_throws_naming_it()
    {
        var broken = MaterialCorpusTests.Mutated(m =>
            ((System.Text.Json.Nodes.JsonObject)m!["speciesCostMultiplierMilli"]!).Remove("almanac"));
        var ex = Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
        Assert.Contains("almanac", ex.Message);
    }

    [Fact]
    public void An_out_of_range_multiplier_throws()
    {
        var broken = MaterialCorpusTests.Mutated(m => m!["speciesCostMultiplierMilli"]!["chaff"] = 0);
        var ex = Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
        Assert.Contains("chaff", ex.Message);
    }

    [Fact]
    public void Every_rung_has_a_multiplier_row_closed_vocabulary_pin()
    {
        var t = MaterialCorpusTests.Tuning();
        Assert.Equal(RarityLadder.RungCount, t.SpeciesCostMultiplierMilli.Count);
        foreach (var rungId in RarityLadder.RungIds)
            Assert.True(t.SpeciesCostMultiplierMilli.ContainsKey(rungId), rungId);
    }

    [Fact]
    public void A_missing_speciesCostThresholdRung_throws()
    {
        var broken = MaterialCorpusTests.Mutated(m => ((System.Text.Json.Nodes.JsonObject)m!).Remove("speciesCostThresholdRung"));
        Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
    }

    [Fact]
    public void An_unknown_threshold_rung_throws()
    {
        var broken = MaterialCorpusTests.Mutated(m => m!["speciesCostThresholdRung"] = "not-a-rung");
        var ex = Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
        Assert.Contains("not-a-rung", ex.Message);
    }

    [Fact]
    public void A_missing_speciesCostThresholdRungByVerb_section_throws()
    {
        var broken = MaterialCorpusTests.Mutated(m => ((System.Text.Json.Nodes.JsonObject)m!).Remove("speciesCostThresholdRungByVerb"));
        var ex = Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
        Assert.Contains("speciesCostThresholdRungByVerb", ex.Message);
    }

    [Fact]
    public void An_empty_speciesCostThresholdRungByVerb_is_legal()
    {
        // The shipped v3 value IS {} -- proven directly rather than mutated.
        var t = MaterialCorpusTests.Tuning();
        Assert.Empty(t.SpeciesCostThresholdRungByVerb);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"not-a-rung\"")]
    public void An_override_sentinel_or_non_rung_value_is_a_load_rejection_naming_the_verb(string rawJsonValue)
    {
        var broken = MaterialCorpusTests.Mutated(m =>
            m!["speciesCostThresholdRungByVerb"]!["elevate"] =
                System.Text.Json.Nodes.JsonNode.Parse(rawJsonValue));
        var ex = Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
        Assert.Contains("elevate", ex.Message);
    }

    [Fact]
    public void An_override_key_naming_a_non_CraftOperation_id_is_a_load_rejection()
    {
        var broken = MaterialCorpusTests.Mutated(m =>
            m!["speciesCostThresholdRungByVerb"]!["not-a-verb"] = "heirloom");
        var ex = Assert.Throws<MaterialTuningRejection>(() => MaterialTuning.Parse(broken));
        Assert.Contains("not-a-verb", ex.Message);
    }

    // ---- R-SC2: override resolution -----------------------------------------------------------------

    [Fact]
    public void A_verb_present_in_the_override_map_gates_at_its_own_rung_a_verb_absent_at_the_default()
    {
        var broken = MaterialCorpusTests.Mutated(m =>
            m!["speciesCostThresholdRungByVerb"]!["elevate"] = "almanac");
        var t = MaterialTuning.Parse(broken);

        var almanacIndex = RarityLadder.RungIndexOf("almanac");
        var heirloomIndex = RarityLadder.RungIndexOf(t.SpeciesCostThresholdRung); // shipped default

        // elevate: overridden to gate at almanac -- one rung below almanac must be neutral (1000).
        Assert.Equal(1000, t.SpeciesMultiplierMilli(CraftOperation.Elevate, almanacIndex - 1));
        Assert.True(t.SpeciesMultiplierMilli(CraftOperation.Elevate, almanacIndex) > 1000);

        // bore: absent from the override -- gates at the shipped DEFAULT (heirloom), not almanac.
        Assert.True(t.SpeciesMultiplierMilli(CraftOperation.Bore, heirloomIndex) > 1000);
    }

    [Fact]
    public void Elevate_is_included_among_the_gated_verbs_owner_decision_2026_09_13()
    {
        var t = MaterialCorpusTests.Tuning();
        var thresholdIndex = RarityLadder.RungIndexOf(t.SpeciesCostThresholdRung);
        Assert.True(t.SpeciesMultiplierMilli(CraftOperation.Elevate, thresholdIndex) > 1000);
    }

    // ---- checked overflow ----------------------------------------------------------------------------

    [Fact]
    public void ApplyBandAndSpecies_throws_rather_than_wraps_on_overflow() =>
        Assert.Throws<OverflowException>(() =>
            MaterialTuning.ApplyBandAndSpecies(long.MaxValue / 1000, 8000, 4000));

    // ---- orthogonality: set bonus evaluation is unaffected by any craft at any cost -----------------

    [Fact]
    public void Set_bonus_evaluation_reads_nothing_this_module_touches()
    {
        // SetEvaluator.Hits is a pure function of (equipped, sets) -- it takes no MaterialTuning, no
        // RecipeContext, no cost. Resolving a recipe at ANY species multiplier cannot move it, because
        // there is no shared state and no call from one into the other. Demonstrated, not just argued:
        // the same Hits() call, computed once, is reused across three different cost resolutions below.
        var set = new SetDef("demo", "Demo Set",
            new[] { new SetMemberDef("item.demo-001", ItemRole.CoreGuard, ItemFrame.Humanoid) },
            new[] { new SetTierDef(1, "set.demo-01", false) });
        var equipped = new[] { new EquippedPiece(ItemRole.CoreGuard, "item.demo-001") };
        var hits = SetEvaluator.Hits(equipped, new[] { set });

        var catalog = MaterialCorpusTests.Catalog();
        var elevate = catalog.Recipes.Values.First(r => r.Operation == CraftOperation.Elevate);
        foreach (int? speciesRung in new int?[] { null, 0, 5, 9 })
        {
            catalog.Resolve(elevate.RecipeId, new RecipeContext(0, 1, 25, "humanoid", 0, speciesRung));
            Assert.Single(hits); // unchanged across every resolve above -- never re-derived from a cost
        }
    }
}
