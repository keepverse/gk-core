using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// T4.5 (`species-generator`, `--check`/`--explain`, `spec-species-generator.md` §7-9):
/// <see cref="ConcreteSpeciesSerializer.Canonical"/> is the property the `--check` gate is built on —
/// the same species must serialise to the byte-identical string every time, regardless of dictionary
/// insertion order.
/// </summary>
public class ConcreteSpeciesSerializerTests
{
    static ConcreteSpecies Species(IReadOnlyDictionary<string, long> magnitudes) => new()
    {
        SpeciesId = "test.species", Rarity = CreatureRarity.Cultivated, Theta = 13, PTheta = 452,
        AttackIntervalMs = 1500, AttackIntervalSource = "classified", RangeCells = 5, VariantCount = 2,
        Magnitudes = magnitudes,
    };

    [Fact]
    public void Regenerating_the_same_species_is_byte_identical()
    {
        var species = Species(new Dictionary<string, long> { ["combat.power.omni"] = 362, ["resource.max.hp"] = 2712 });

        var first = ConcreteSpeciesSerializer.Canonical(species);
        var second = ConcreteSpeciesSerializer.Canonical(species);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Key_order_does_not_change_the_serialised_bytes()
    {
        // The exact same magnitudes, inserted in the opposite order — a dictionary-insertion-order
        // regression would silently produce a "regeneration" that isn't byte-identical.
        var forward = new Dictionary<string, long> { ["combat.power.omni"] = 362, ["resource.max.hp"] = 2712 };
        var backward = new Dictionary<string, long> { ["resource.max.hp"] = 2712, ["combat.power.omni"] = 362 };

        Assert.Equal(
            ConcreteSpeciesSerializer.Canonical(Species(forward)),
            ConcreteSpeciesSerializer.Canonical(Species(backward)));
    }

    [Fact]
    public void A_real_value_change_moves_the_serialised_bytes()
    {
        var before = Species(new Dictionary<string, long> { ["combat.power.omni"] = 362 });
        var after = Species(new Dictionary<string, long> { ["combat.power.omni"] = 363 });

        Assert.NotEqual(
            ConcreteSpeciesSerializer.Canonical(before),
            ConcreteSpeciesSerializer.Canonical(after));
    }

    [Fact]
    public void Adding_a_new_derived_column_touches_no_anchor_and_still_serialises()
    {
        // spec §8's own property: a new derived field reads the SAME anchor fields already on disk —
        // simulated here by adding a channel no earlier version of this species carried, proving the
        // serializer (and by extension the whole pipeline) tolerates a wider magnitudes map without
        // any change to AnchorRow or the seed file it came from.
        var before = Species(new Dictionary<string, long> { ["combat.power.omni"] = 362 });
        var widened = Species(new Dictionary<string, long>
        {
            ["combat.power.omni"] = 362, ["status.power.dot"] = 91, // a channel that didn't exist before
        });

        var beforeJson = ConcreteSpeciesSerializer.Canonical(before);
        var widenedJson = ConcreteSpeciesSerializer.Canonical(widened);

        Assert.Contains("combat.power.omni", beforeJson);
        Assert.DoesNotContain("status.power.dot", beforeJson);
        Assert.Contains("status.power.dot", widenedJson);
    }

    // ---- rank (spec-species-rank.md §1/§4, creature-seed Task 6) --------------------------------------

    [Fact]
    public void Rank_is_written_when_the_species_has_one()
    {
        var json = ConcreteSpeciesSerializer.Canonical(
            Species(new Dictionary<string, long> { ["resource.max.hp"] = 2712 })
                with { Rank = CreatureRank.Chimeric });

        Assert.Contains("\"rank\": \"Chimeric\"", json);
    }

    [Fact]
    public void A_skipped_rank_leaves_the_key_absent_never_a_bottom_rung()
    {
        // Assumption 4's shape at the write boundary: the serializer omits the key entirely rather
        // than fabricating `Chaff`, and the reader's own optional-read turns that into null.
        var json = ConcreteSpeciesSerializer.Canonical(
            Species(new Dictionary<string, long> { ["resource.max.hp"] = 2712 }));

        Assert.DoesNotContain("\"rank\"", json);
    }
}
