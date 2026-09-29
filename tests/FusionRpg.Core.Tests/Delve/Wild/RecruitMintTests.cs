using FusionRpg.Contracts;
using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>D4.5 (spec-wild-room.md §4) — `RecruitMint.Build`: a mint test asserting `Origin` and a
/// test pinning today's level-1 behaviour so `CreatureMintSpec.Level` landing becomes visible (the
/// todo's own two Verify lines).</summary>
public class RecruitMintTests
{
    static ConcreteSpecies Species(string speciesId = "creature.ember-imp") => new()
    {
        SpeciesId = speciesId,
        Side = "zombie",
        GameTypeId = 77,
        Rarity = CreatureRarity.Cultivated,
        ElementPrimary = ElementTypeId.Fire,
        ElementSecondary = ElementTypeId.Dark,
    };

    static readonly IReadOnlyList<string> Traits = new[] { "trait.brute", "trait.swift" };

    [Fact]
    public void Build_copies_identity_fields_from_the_species_row()
    {
        var species = Species();
        var spec = RecruitMint.Build(species, Traits, "delve", thetaEnemy: 83);

        Assert.Equal(species.SpeciesId, spec.SpeciesId);
        Assert.Equal(species.Side, spec.Side);
        Assert.Equal(species.GameTypeId, spec.GameTypeId);
        Assert.Equal(species.Rarity.ToId(), spec.Rarity);
        Assert.Equal(species.ElementPrimary.ToElementId(), spec.ElementPrimary);
        Assert.Equal(species.ElementSecondary?.ToElementId(), spec.ElementSecondary);
    }

    [Fact]
    public void Build_sets_Origin_to_the_callers_own_value_a_mint_test_asserting_Origin()
    {
        var spec = RecruitMint.Build(Species(), Traits, "delve", thetaEnemy: 83);
        Assert.Equal("delve", spec.Origin);
    }

    [Fact]
    public void Build_is_reusable_for_section_5_capture_with_its_own_origin()
    {
        // Spec §5, verbatim: a capture "mints the same way as §4" with Origin = "capture" -- this
        // function must not hardcode "delve" internally, or the capture path could not reuse it.
        var spec = RecruitMint.Build(Species(), Traits, "capture", thetaEnemy: 83);
        Assert.Equal("capture", spec.Origin);
    }

    [Fact]
    public void Build_carries_the_callers_own_traitIds_verbatim()
    {
        var spec = RecruitMint.Build(Species(), Traits, "delve", thetaEnemy: 83);
        Assert.Equal(Traits, spec.TraitIds);
    }

    [Fact]
    public void Build_leaves_Variant_at_the_DTOs_own_default_nothing_is_invented()
    {
        var spec = RecruitMint.Build(Species(), Traits, "delve", thetaEnemy: 83);
        Assert.Equal("normal", spec.Variant); // CreatureMintSpec's own default, never set by this function
    }

    [Fact]
    public void Build_refuses_a_null_species_or_traitIds_or_empty_origin()
    {
        Assert.Throws<ArgumentNullException>(() => RecruitMint.Build(null!, Traits, "delve", 83));
        Assert.Throws<ArgumentNullException>(() => RecruitMint.Build(Species(), null!, "delve", 83));
        Assert.Throws<ArgumentException>(() => RecruitMint.Build(Species(), Traits, "", 83));
    }

    // ---- Pins today's level-1 behaviour so CreatureMintSpec.Level landing becomes visible ----

    [Fact]
    public void Two_different_thetaEnemy_values_produce_field_identical_specs_today_pins_the_named_gap()
    {
        // Spec: "Level = θ_enemy" is what SHOULD happen; CreatureMintSpec has no Level field yet, so
        // thetaEnemy cannot possibly affect the returned spec today. Once CreatureMintSpec.Level lands
        // and this function's BODY is updated to thread thetaEnemy through it, THIS assertion must
        // start failing -- that failure is the signal the fix landed, per this file's own comment.
        var species = Species();
        var atLowTheta = RecruitMint.Build(species, Traits, "delve", thetaEnemy: 20);
        var atHighTheta = RecruitMint.Build(species, Traits, "delve", thetaEnemy: 200);

        Assert.Equal(atLowTheta.SpeciesId, atHighTheta.SpeciesId);
        Assert.Equal(atLowTheta.Side, atHighTheta.Side);
        Assert.Equal(atLowTheta.GameTypeId, atHighTheta.GameTypeId);
        Assert.Equal(atLowTheta.Rarity, atHighTheta.Rarity);
        Assert.Equal(atLowTheta.Variant, atHighTheta.Variant);
        Assert.Equal(atLowTheta.ElementPrimary, atHighTheta.ElementPrimary);
        Assert.Equal(atLowTheta.ElementSecondary, atHighTheta.ElementSecondary);
        Assert.Equal(atLowTheta.TraitIds, atHighTheta.TraitIds);
        Assert.Equal(atLowTheta.Origin, atHighTheta.Origin);
        Assert.Equal(atLowTheta.Nickname, atHighTheta.Nickname);
    }

    [Fact]
    public void CreatureMintSpec_genuinely_has_no_Level_property_yet_the_gap_is_proven_not_assumed()
    {
        // The load-bearing form of "the fix is visible when it lands": if CreatureMintSpec.Level is ever
        // added, this reflection check starts failing immediately, forcing RecruitMint.Build's own
        // body to be updated in the same change rather than silently leaving thetaEnemy unused.
        var property = typeof(CreatureMintSpec).GetProperty("Level");
        Assert.Null(property);
    }
}
