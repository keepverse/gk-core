using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Tests.Delve.Encounter;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>species-gear-chain T19: the 102-way `CreatureTypeId` collision stays fixed.
/// Pins the contract (uniqueness), not a count: no test here asserts how many species exist.
/// The floor (10_000) and plant offset (50_000) are pinned as literals with reason — they are a
/// persisted structural id space (tunables-ssot), not balance numbers a pass would change.</summary>
public class CreatureTypeIdTests
{
    [Fact]
    public void Plant_and_zombie_sharing_a_gameTypeId_get_different_ids()
    {
        // gameTypeId 7 is one of the 102 measured shared values; the plant branch is the one
        // SlotFilter used to drop.
        Assert.Equal(60_007, CreatureSpeciesCatalog.CreatureTypeIdFor("plant", 7));
        Assert.Equal(10_007, CreatureSpeciesCatalog.CreatureTypeIdFor("zombie", 7));
        Assert.NotEqual(
            CreatureSpeciesCatalog.CreatureTypeIdFor("plant", 7),
            CreatureSpeciesCatalog.CreatureTypeIdFor("zombie", 7));
    }

    [Fact]
    public void SeedReader_and_Generator_agree_through_the_shared_function()
    {
        const string json = """
        {
          "speciesId": "ProbePlant",
          "rarity": "Cultivated",
          "theta": 13,
          "pTheta": 452,
          "attackIntervalMs": 1500,
          "attackIntervalSource": "classified",
          "rangeCells": 5,
          "variantCount": 2,
          "side": "plant",
          "gameTypeId": 7,
          "elementPrimary": "Earth",
          "elementSecondary": null,
          "deployMode": "PlantAvatar",
          "acquisition": "Summonable",
          "variants": ["normal"],
          "traitPool": [],
          "magnitudes": {}
        }
        """;
        var viaReader = ConcreteSpeciesMapper.ToCreatureSpeciesDef(ConcreteSpeciesSeedReader.Parse(json));

        var viaGenerator = CreatureSpeciesGenerator.Generate(new[]
        {
            new CapturedTypeSeed("plant", 7, "ProbePlant", "Probe Plant", 100),
            new CapturedTypeSeed("zombie", 7, "ProbeZombie", "Probe Zombie", 100),
        });

        Assert.Equal(60_007, viaReader.CreatureTypeId);
        Assert.Equal(
            new[] { 10_007, 60_007 },
            viaGenerator.Select(s => s.CreatureTypeId).OrderBy(id => id));
    }

    [Fact]
    public void SlotFilter_anchor_carries_side_into_the_id()
    {
        // The previously-colliding value 10_007 for a plant anchor is gone: the anchor carries its
        // side into From(), and the id applies the offset.
        static ConcreteAnchor Join(string side)
        {
            var anchor = new AnchorRow(
                SpeciesId: "x", Rarity: "common", ThreatBand: null, AptitudePrimary: "Might",
                AptitudeSecondary: null, Pure: true, AttackTempo: "steady", Reach: "short",
                Variants: Array.Empty<string>(), Side: side, GameTypeId: 7, ElementPrimary: "fire",
                ElementSecondary: null, DeployMode: "PlantAvatar", Acquisition: Array.Empty<string>(),
                Traits: Array.Empty<string>(), TargetPreference: "frontline");
            var species = new ConcreteSpecies { SpeciesId = "x", Side = side, GameTypeId = 7 };
            return ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning);
        }

        Assert.Equal(60_007, Join("plant").CreatureTypeId);
        Assert.Equal(10_007, Join("zombie").CreatureTypeId);
    }

    [Fact]
    public void CreatureTypeId_is_unique_across_the_full_catalog()
    {
        using (CreatureSpeciesCatalog.UseScoped(Fusion.RealCorpusFixture.Snapshot))
        {
            var ids = CreatureSpeciesCatalog.All.Select(s => s.CreatureTypeId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }
}
