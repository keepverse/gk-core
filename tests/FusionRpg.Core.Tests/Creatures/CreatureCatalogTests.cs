using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

[Trait("VerificationId", "core.creature-catalog-generator")]
public class CreatureCatalogTests
{
    [Fact]
    public void Generated_roster_meets_the_v1_floor()
    {
        var all = CreatureSpeciesCatalog.All; // touching All runs Validate()
        Assert.True(all.Count >= 16, $"V1 floor is ≥16 species; got {all.Count}");
        Assert.Contains(all, s => s.ElementPrimary == ElementTypeId.Light);
        Assert.Contains(all, s => s.ElementPrimary == ElementTypeId.Dark);
        Assert.Contains(all, s => s.DeployMode == CreatureDeployMode.HypnoAlly);
        Assert.Contains(all, s => s.BaseRarity == CreatureRarity.Sunwoven);

        // Guardrails: ≤15% capture-only, never legendary.
        var captureOnly = all.Where(s => s.Acquisition == CreatureAcquisition.CaptureOnly).ToList();
        Assert.True(captureOnly.Count <= Math.Max(1, all.Count * 15 / 100));
        Assert.DoesNotContain(captureOnly, s => s.BaseRarity == CreatureRarity.Sunwoven);
    }

    static List<CapturedTypeSeed> FixedSeeds() => Enumerable.Range(0, 20)
        .Select(i => new CapturedTypeSeed("zombie", i, $"TestZombie{i}", $"Zombie {i}", 100 + i * 37))
        .ToList();

    [Fact]
    public void Generator_is_deterministic()
    {
        var a = CreatureSpeciesGenerator.Generate(FixedSeeds());
        var b = CreatureSpeciesGenerator.Generate(FixedSeeds());
        Assert.Equal(CreatureSpeciesGenerator.EmitCSharp(a), CreatureSpeciesGenerator.EmitCSharp(b));
    }

    [Fact]
    public void Generator_output_validates_and_covers_light_and_dark()
    {
        var species = CreatureSpeciesGenerator.Generate(FixedSeeds());
        CreatureSpeciesCatalog.Validate(species);
        Assert.Contains(species, s => s.ElementPrimary == ElementTypeId.Light);
        Assert.Contains(species, s => s.ElementPrimary == ElementTypeId.Dark);
        Assert.Equal(2, species.Count(s => s.DeployMode == CreatureDeployMode.HypnoAlly));
    }

    static CreatureSpeciesDef Valid() => new()
    {
        SpeciesId = "test-creature",
        Name = "Test Creature",
        Side = "zombie",
        GameTypeId = 1,
        CreatureTypeId = 10001,
        ElementPrimary = ElementTypeId.Fire,
        BaseRarity = CreatureRarity.Chaff,
        DeployMode = CreatureDeployMode.PlantAvatar,
        Acquisition = CreatureAcquisition.Summonable,
        Variants = new[] { "normal" },
        TraitPool = new[] { "swift" }
    };

    [Fact]
    public void Validation_rejects_bad_species()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CreatureSpeciesCatalog.Validate(new[] { Valid(), Valid() })); // duplicate id
        Assert.Throws<InvalidOperationException>(() =>
            CreatureSpeciesCatalog.Validate(new[] { Valid() with { CreatureTypeId = 5 } })); // below floor
        Assert.Throws<InvalidOperationException>(() =>
            CreatureSpeciesCatalog.Validate(new[] { Valid() with { Acquisition = CreatureAcquisition.None } }));
        Assert.Throws<InvalidOperationException>(() =>
            CreatureSpeciesCatalog.Validate(new[] { Valid() with { TraitPool = new[] { "nonsense" } } }));
        Assert.Throws<InvalidOperationException>(() =>
            CreatureSpeciesCatalog.Validate(new[] { Valid() with { ElementSecondary = ElementTypeId.Fire } }));
        Assert.Throws<InvalidOperationException>(() =>
            CreatureSpeciesCatalog.Validate(new[] { Valid() with { Variants = new[] { "holographic" } } }));
        Assert.Throws<InvalidOperationException>(() =>
            CreatureSpeciesCatalog.Validate(new[] { Valid() with { Side = "mower" } }));
    }

    [Fact]
    public void Generator_invariants_hold_across_many_input_shapes()
    {
        // Property-style regression for the ensure-pass bug (review I2): whatever the hash
        // distribution of the input, the roster must end with ≥1 light AND ≥1 dark primary,
        // never primary==secondary, and always validate.
        for (var offset = 0; offset < 20; offset++)
        {
            var seeds = Enumerable.Range(0, 18)
                .Select(i => new CapturedTypeSeed("zombie", offset * 137 + i, $"Z{offset}_{i}", null, 90 + i * 13))
                .ToList();
            var roster = CreatureSpeciesGenerator.Generate(seeds);
            CreatureSpeciesCatalog.Validate(roster);
            Assert.Contains(roster, s => s.ElementPrimary == ElementTypeId.Light);
            Assert.Contains(roster, s => s.ElementPrimary == ElementTypeId.Dark);
            Assert.DoesNotContain(roster, s => s.ElementSecondary == s.ElementPrimary);
        }
    }

    [Fact]
    public void Creature_type_ids_never_collide_across_sides()
    {
        // Review S5: zombie 10000+t vs plant 60000+t — a mixed roster with overlapping raw
        // type ids must stay collision-free.
        var seeds = Enumerable.Range(0, 10)
            .SelectMany(i => new[]
            {
                new CapturedTypeSeed("zombie", i, $"Zed{i}", null, 200 + i),
                new CapturedTypeSeed("plant", i, $"Plant{i}", null, 100 + i)
            })
            .ToList();
        var roster = CreatureSpeciesGenerator.Generate(seeds);
        Assert.Equal(roster.Count, roster.Select(s => s.CreatureTypeId).Distinct().Count());
    }

    [Fact]
    public void Trait_catalog_ids_are_stable_kebab_and_unique()
    {
        Assert.Equal(CreatureTraitCatalog.All.Count, CreatureTraitCatalog.All.Select(t => t.TraitId).Distinct().Count());
        foreach (var t in CreatureTraitCatalog.All)
        {
            Assert.Equal(t.TraitId, t.TraitId.Trim().ToLowerInvariant());
            Assert.False(string.IsNullOrWhiteSpace(t.GrantTemplateId));
        }
    }

    // ---- rank (spec-species-rank.md §1/§4, creature-seed Task 7) --------------------------------------

    [Fact]
    public void The_mapper_carries_rank_and_a_skipped_rank_survives_Validate_untouched()
    {
        // ConcreteSpeciesMapper is the ONE funnel both hosts share (the Server's store rows and the
        // Injector's committed-tree rows). Rank must ride it WITHOUT being defaulted: null means the
        // pipeline skipped it, and Validate has nothing to refuse — a closed enum already parsed at
        // every boundary that produces one.
        var concrete = new ConcreteSpecies
        {
            SpeciesId = "test.ranked", Rarity = CreatureRarity.Heirloom, Rank = CreatureRank.Heirloom,
            Theta = 13, PTheta = 452, AttackIntervalMs = 1500, AttackIntervalSource = "classified",
            RangeCells = 5, VariantCount = 2, Side = "plant", GameTypeId = 7,
            ElementPrimary = ElementTypeId.Earth, DeployMode = CreatureDeployMode.PlantAvatar,
            Acquisition = CreatureAcquisition.Summonable,
            Variants = new[] { "normal" }, TraitPool = Array.Empty<string>(),
            Magnitudes = new Dictionary<string, long> { ["resource.max.hp"] = 2712 },
        };

        var ranked = ConcreteSpeciesMapper.ToCreatureSpeciesDef(concrete);
        Assert.Equal(CreatureRank.Heirloom, ranked.Rank);
        CreatureSpeciesCatalog.Validate(new[] { ranked }); // must not throw

        var skipped = ConcreteSpeciesMapper.ToCreatureSpeciesDef(concrete with { Rank = null });
        Assert.Null(skipped.Rank);
        CreatureSpeciesCatalog.Validate(new[] { skipped }); // a skipped rank is legal, not a refusal
    }
}
