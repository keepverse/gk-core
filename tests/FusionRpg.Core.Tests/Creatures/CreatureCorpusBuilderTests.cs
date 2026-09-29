using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>spec-creature-corpus-emit.md §6 — the pure builder, no filesystem, no DAL.</summary>
public class CreatureCorpusBuilderTests
{
    static CreatureSpeciesDef Species(string id, string side, int gameTypeId, string name = "Test Creature") => new()
    {
        SpeciesId = id,
        Name = name,
        Side = side,
        GameTypeId = gameTypeId,
        CreatureTypeId = side == "zombie" ? 10_000 + gameTypeId : 60_000 + gameTypeId,
        ElementPrimary = ElementTypeId.Fire,
        BaseRarity = CreatureRarity.Chaff,
        DeployMode = CreatureDeployMode.PlantAvatar,
        Acquisition = CreatureAcquisition.Summonable,
        Variants = new[] { "normal" },
        TraitPool = new[] { "swift" }
    };

    [Fact]
    public void Species_with_no_almanac_row_emits_fully_absent_coverage()
    {
        var species = new[] { Species("no-flavor-creature", "zombie", 1) };
        var entries = CreatureCorpusBuilder.Build(species, Array.Empty<AlmanacSeedRow>(), Array.Empty<RecipeItem>());

        var e = Assert.Single(entries);
        Assert.Null(e.FlavorInfo);
        Assert.Null(e.FlavorIntroduce);
        Assert.Null(e.Hp);
        Assert.Null(e.Attack);
        Assert.Null(e.Armor);
        Assert.Equal("absent", e.Coverage.Cost);
        Assert.Equal("unobserved", e.Coverage.Stats);
        Assert.Equal("absent", e.Coverage.Flavor);
    }

    [Fact]
    public void Unobserved_stats_render_null_never_zero()
    {
        var species = new[] { Species("unobserved-creature", "zombie", 2) };
        var almanac = new[]
        {
            new AlmanacSeedRow("zombie", 2, "flavor text", null, null, null, "absent",
                Hp: null, Attack: null, Armor: null, ArmorMax: null, StatsObserved: false)
        };
        var entries = CreatureCorpusBuilder.Build(species, almanac, Array.Empty<RecipeItem>());

        var e = Assert.Single(entries);
        Assert.Null(e.Hp);
        Assert.Null(e.Attack);
        Assert.Null(e.Armor);
        Assert.Equal("unobserved", e.Coverage.Stats);
    }

    [Fact]
    public void Observed_stats_carry_through_as_long_values()
    {
        var species = new[] { Species("observed-creature", "zombie", 3) };
        var almanac = new[]
        {
            new AlmanacSeedRow("zombie", 3, "flavor", null, null, null, "absent",
                Hp: 500, Attack: 40, Armor: 10, ArmorMax: 10, StatsObserved: true)
        };
        var entries = CreatureCorpusBuilder.Build(species, almanac, Array.Empty<RecipeItem>());

        var e = Assert.Single(entries);
        Assert.Equal(500L, e.Hp);
        Assert.Equal(40L, e.Attack);
        Assert.Equal(10L, e.Armor);
        Assert.Equal("observed", e.Coverage.Stats);
    }

    [Fact]
    public void Cost_status_unparsed_stays_distinct_from_absent()
    {
        var species = new[] { Species("unparsed-cost", "zombie", 4) };
        var almanac = new[]
        {
            new AlmanacSeedRow("zombie", 4, null, null, SunCost: null, CooldownSec: null,
                CostStatus: "unparsed", Hp: null, Attack: null, Armor: null, ArmorMax: null, StatsObserved: false)
        };
        var entries = CreatureCorpusBuilder.Build(species, almanac, Array.Empty<RecipeItem>());

        Assert.Equal("unparsed", Assert.Single(entries).Coverage.Cost);
    }

    [Fact]
    public void Fusion_rows_populate_lineage_and_families_are_never_emitted()
    {
        var species = new[]
        {
            Species("parent-a", "plant", 12), Species("parent-b", "plant", 19), Species("child", "plant", 30)
        };
        var recipes = new[]
        {
            new RecipeItem { ParentA = 12, ParentB = 19, Result = 30 }
        };
        var entries = CreatureCorpusBuilder.Build(species, Array.Empty<AlmanacSeedRow>(), recipes);

        var child = entries.Single(e => e.Id == "child");
        Assert.Equal(new[] { 12, 19 }, child.Lineage.Parents);
        Assert.Empty(child.Lineage.Children);

        var parentA = entries.Single(e => e.Id == "parent-a");
        Assert.Empty(parentA.Lineage.Parents);
        Assert.Equal(new[] { 30 }, parentA.Lineage.Children);

        // §2.4 — no property on the emitted shape can ever restate "families"; a structural
        // guarantee via the record's own declared members, not a runtime filter that could rot.
        var props = typeof(CreatureCorpusEntry).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(props, n => n.Contains("Famil", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Zombie_side_creatures_never_get_lineage_even_if_a_recipe_id_numerically_collides()
    {
        // Measured fact (2026-08-31): recipes are a plant-fusion mechanic. A zombie sharing a raw
        // type id with a plant recipe participant must NOT inherit that plant's lineage.
        var species = new[] { Species("zombie-7", "zombie", 7), Species("plant-7", "plant", 7), Species("result-8", "plant", 8) };
        var recipes = new[] { new RecipeItem { ParentA = 7, ParentB = 7, Result = 8 } };
        var entries = CreatureCorpusBuilder.Build(species, Array.Empty<AlmanacSeedRow>(), recipes);

        Assert.Empty(entries.Single(e => e.Id == "zombie-7").Lineage.Children);
        Assert.Equal(new[] { 8 }, entries.Single(e => e.Id == "plant-7").Lineage.Children);
    }

    [Fact]
    public void Catalog_only_fields_never_appear_on_the_emitted_shape()
    {
        // §2.1/§7: element, rarity, creatureTypeId, deployMode, acquisition, variants and traitPool
        // live ONLY in CreatureSpeciesCatalog — restating them here would create a second source of
        // truth. Asserted structurally so the rule cannot erode by someone adding a field back.
        var props = typeof(CreatureCorpusEntry).GetProperties().Select(p => p.Name).ToList();
        foreach (var forbidden in new[] { "Element", "Rarity", "CreatureTypeId", "DeployMode", "Acquisition", "Variants", "TraitPool" })
            Assert.DoesNotContain(props, n => n.Equals(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Same_inputs_produce_equal_entries_on_repeat_calls()
    {
        var species = new[] { Species("a", "zombie", 1), Species("b", "plant", 2) };
        var almanac = new[]
        {
            new AlmanacSeedRow("zombie", 1, "x", null, 25, 7.5, "parsed", 100, 10, 5, 5, true)
        };
        var recipes = new[] { new RecipeItem { ParentA = 1, ParentB = 2, Result = 2 } };

        var first = CreatureCorpusBuilder.Build(species, almanac, recipes);
        var second = CreatureCorpusBuilder.Build(species, almanac, recipes);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Entries_are_sorted_by_species_id_regardless_of_input_order()
    {
        var species = new[] { Species("zzz", "zombie", 9), Species("aaa", "zombie", 10) };
        var entries = CreatureCorpusBuilder.Build(species, Array.Empty<AlmanacSeedRow>(), Array.Empty<RecipeItem>());

        Assert.Equal(new[] { "aaa", "zzz" }, entries.Select(e => e.Id));
    }
}
