using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T34b (`species-materials` c, R22 cost side) — the ONE resolver
/// (<see cref="MaterialRecipeCatalog.Resolve"/>) that turns a recipe's authored trophy SCOPE TOKEN
/// (<c>trophy.species.{slot}</c> / <c>trophy.family.{slot}</c>) into the ONE concrete id a spend
/// debits, against real trophy ids the bootstrap injects from the committed
/// `gk-data/packs/fusion/data/seed/items/materials/trophy-registry.json` (species-gear-chain T34's own real run).
///
/// <para>No real recipe authors a trophy scope token yet (T34d's own publish) — every fixture here is
/// a SYNTHETIC tuning (via <see cref="MaterialCorpusTests.Mutated"/>, mutating the REAL shipped
/// `materials.v3.json` JSON tree rather than hand-typing the whole schema) and a synthetic recipe
/// corpus entry, matching this module's own established `Mutated`/`Tuning`/`Catalog` pattern.</para>
/// </summary>
public class MaterialTrophyResolveTests
{
    // A real species with families ["flora", "gourd"], in that family-map.json order (the spec's own
    // named example, § Design 5.2). Both families' slot-1 trophy exists in the real registry
    // (perFamily = 8, every family gets slots 1..8).
    const string MultiFamilySpecies = "bigpumpkin";
    const string FirstFamily = "flora";
    const string SecondFamily = "gourd";

    static MaterialTuning TuningWithTrophyLeg(long coefficient = 1) => MaterialTuning.Parse(
        MaterialCorpusTests.Mutated(root =>
        {
            var trophyLeg = new System.Text.Json.Nodes.JsonObject
            {
                ["coefficient"] = coefficient,
                ["variable"] = "flat",
            };
            root["operations"]!["elevate"]!.AsObject()["trophy"] = trophyLeg;
        }));

    static string SyntheticRecipeCorpus(string recipeId, string trophyScopeToken) => $$"""
        {"entries": [{
            "id": "{{recipeId}}", "operation": "elevate", "outputKind": "mutation", "outputQty": 1,
            "frame": "any", "costLines": [{"material": "{{trophyScopeToken}}", "costBand": "cheap"}]
        }]}
        """;

    static MaterialRecipeCatalog CatalogWith(string trophyScopeToken, string recipeId = "recipe.trophy-test") =>
        MaterialRecipeCatalog.Load(new[] { SyntheticRecipeCorpus(recipeId, trophyScopeToken) }, TuningWithTrophyLeg());

    [Fact]
    public void A_species_scope_leg_resolves_to_the_bound_species_concrete_id()
    {
        var catalog = CatalogWith("trophy.species.1");
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: "abyssswordstar");

        var lines = catalog.Resolve("recipe.trophy-test", ctx);

        var trophyLine = Assert.Single(lines, l => l.Class == MaterialClass.Trophy);
        Assert.Equal("trophy.species.abyssswordstar.1", trophyLine.MaterialId);
        Assert.Equal(1, trophyLine.Qty);
    }

    [Fact]
    public void A_species_scope_leg_without_a_bound_species_refuses_by_name()
    {
        var catalog = CatalogWith("trophy.species.1");
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: null);

        var ex = Assert.Throws<MaterialRecipeRejection>(() => catalog.Resolve("recipe.trophy-test", ctx));
        Assert.Contains("no bound species", ex.Message);
    }

    [Fact]
    public void A_species_scope_leg_for_a_species_with_no_registry_row_refuses_by_name()
    {
        var catalog = CatalogWith("trophy.species.1");
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: "not-a-real-species");

        var ex = Assert.Throws<MaterialRecipeRejection>(() => catalog.Resolve("recipe.trophy-test", ctx));
        Assert.Contains("trophy.species.not-a-real-species.1", ex.Message);
        Assert.Contains("registry", ex.Message);
    }

    [Fact]
    public void Below_the_threshold_the_trophy_leg_is_entirely_absent()
    {
        var catalog = CatalogWith("trophy.species.1");

        // No SpeciesRungIndex at all -- Success criterion 5: below the threshold (or species-less),
        // nothing about the OTHER legs changes, and the trophy leg itself does not appear at all
        // (never priced at a neutral quantity the way the species multiplier is for other classes).
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0, BoundSpeciesId: "abyssswordstar");
        var lines = catalog.Resolve("recipe.trophy-test", ctx);

        Assert.DoesNotContain(lines, l => l.Class == MaterialClass.Trophy);
    }

    [Fact]
    public void A_family_scope_leg_resolves_to_the_first_family_whose_stock_covers_it()
    {
        var catalog = CatalogWith("trophy.family.1");
        var firstId = MaterialCatalog.ComposeTrophyId("family", FirstFamily, 1);
        var secondId = MaterialCatalog.ComposeTrophyId("family", SecondFamily, 1);

        // The FIRST family (flora) has no stock; the SECOND (gourd) has enough -- R22: never split,
        // resolve to the first one that covers the WHOLE leg, in the species' own family-map order.
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: MultiFamilySpecies,
            BoundSpeciesFamilies: new[] { FirstFamily, SecondFamily },
            TrophyStock: StockOf(new Dictionary<string, long> { [secondId] = 5 }));

        var lines = catalog.Resolve("recipe.trophy-test", ctx);
        var trophyLine = Assert.Single(lines, l => l.Class == MaterialClass.Trophy);
        Assert.Equal(secondId, trophyLine.MaterialId);
        Assert.NotEqual(firstId, trophyLine.MaterialId);
    }

    [Fact]
    public void A_family_scope_leg_prefers_the_first_family_when_both_cover_it()
    {
        var catalog = CatalogWith("trophy.family.1");
        var firstId = MaterialCatalog.ComposeTrophyId("family", FirstFamily, 1);
        var secondId = MaterialCatalog.ComposeTrophyId("family", SecondFamily, 1);

        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: MultiFamilySpecies,
            BoundSpeciesFamilies: new[] { FirstFamily, SecondFamily },
            TrophyStock: StockOf(new Dictionary<string, long> { [firstId] = 5, [secondId] = 5 }));

        var lines = catalog.Resolve("recipe.trophy-test", ctx);
        Assert.Equal(firstId, Assert.Single(lines, l => l.Class == MaterialClass.Trophy).MaterialId);
    }

    [Fact]
    public void A_family_scope_leg_with_insufficient_stock_everywhere_refuses_naming_every_candidate()
    {
        var catalog = CatalogWith("trophy.family.1");
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: MultiFamilySpecies,
            BoundSpeciesFamilies: new[] { FirstFamily, SecondFamily },
            TrophyStock: null);

        var ex = Assert.Throws<MaterialRecipeRejection>(() => catalog.Resolve("recipe.trophy-test", ctx));
        Assert.Contains(MaterialCatalog.ComposeTrophyId("family", FirstFamily, 1), ex.Message);
        Assert.Contains(MaterialCatalog.ComposeTrophyId("family", SecondFamily, 1), ex.Message);
    }

    [Fact]
    public void A_family_scope_leg_for_a_species_with_no_family_refuses_by_name()
    {
        var catalog = CatalogWith("trophy.family.1");
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: "abyssswordstar",
            BoundSpeciesFamilies: Array.Empty<string>());

        var ex = Assert.Throws<MaterialRecipeRejection>(() => catalog.Resolve("recipe.trophy-test", ctx));
        Assert.Contains("no family", ex.Message);
    }

    [Fact]
    public void Preview_and_spend_resolve_the_same_concrete_id()
    {
        // "Preview and spend resolve the same id" (spec § Design 4, Strengthen pass item 3) is a
        // property of calling Resolve twice with the identical RecipeContext -- there is only ONE
        // resolver site, so this is determinism, not a second code path to keep in sync.
        var catalog = CatalogWith("trophy.species.1");
        var ctx = new RecipeContext(0, 1, 25, "humanoid", 0,
            SpeciesRungIndex: ThresholdIndex(catalog), BoundSpeciesId: "abyssswordstar");

        var preview = catalog.Resolve("recipe.trophy-test", ctx);
        var spend = catalog.Resolve("recipe.trophy-test", ctx);
        Assert.Equal(preview, spend);
    }

    static int ThresholdIndex(MaterialRecipeCatalog catalog) =>
        FusionRpg.Core.Items.RarityLadder.RungIndexOf(catalog.Tuning.SpeciesCostThresholdRung);

    // species-gear-chain T34d-wire: RecipeContext.TrophyStock is a per-id delegate (never a whole
    // snapshot -- a real caller backs it with one targeted RpgStore.GetMaterialQty read per candidate
    // id). A dictionary is still the natural shape for a FIXTURE; this just adapts it.
    static Func<string, long> StockOf(IReadOnlyDictionary<string, long> stock) =>
        id => stock.TryGetValue(id, out var qty) ? qty : 0;
}
