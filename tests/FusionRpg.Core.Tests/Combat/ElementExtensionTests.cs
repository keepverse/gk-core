using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// Light/dark extension goldens (spec-element-extension.md): roster-generated matrix,
/// byte-identical 4-element regression, exhaustiveness walk over every hand-maintained surface,
/// and strict-parse rejection.
/// </summary>
public class ElementExtensionTests
{
    [Fact]
    public void Roster_is_six_concrete_elements()
    {
        Assert.Equal(6, ElementRoster.Concrete.Count);
        Assert.Equal(ElementRoster.Concrete.Count, ElementRoster.Concrete.Distinct().Count());
    }

    /// <summary>Independent expected model — never calls the production matrix.</summary>
    static ElementMatchupRelation Expected(ElementTypeId a, ElementTypeId d)
    {
        if (a == d) return ElementMatchupRelation.Same;
        var strong = new[]
        {
            (ElementTypeId.Fire, ElementTypeId.Ice),
            (ElementTypeId.Ice, ElementTypeId.Earth),
            (ElementTypeId.Earth, ElementTypeId.Air),
            (ElementTypeId.Air, ElementTypeId.Fire),
            (ElementTypeId.Light, ElementTypeId.Dark),
            (ElementTypeId.Dark, ElementTypeId.Light)
        };
        if (strong.Contains((a, d))) return ElementMatchupRelation.Strong;
        if (strong.Contains((d, a))) return ElementMatchupRelation.Weak;
        return ElementMatchupRelation.Neutral;
    }

    [Fact]
    public void Full_matrix_matches_expected_model_for_all_roster_pairs()
    {
        foreach (var attacker in ElementRoster.Concrete)
            foreach (var defender in ElementRoster.Concrete)
                Assert.Equal(Expected(attacker, defender), ElementRingMatrix.GetRelation(attacker, defender));
    }

    [Fact]
    public void Light_dark_is_a_mutual_counter_and_neutral_vs_the_ring()
    {
        Assert.Equal(ElementMatchupRelation.Strong, ElementRingMatrix.GetRelation(ElementTypeId.Light, ElementTypeId.Dark));
        Assert.Equal(ElementMatchupRelation.Strong, ElementRingMatrix.GetRelation(ElementTypeId.Dark, ElementTypeId.Light));
        var ring = new[] { ElementTypeId.Fire, ElementTypeId.Ice, ElementTypeId.Air, ElementTypeId.Earth };
        foreach (var r in ring)
        {
            Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(ElementTypeId.Light, r));
            Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(r, ElementTypeId.Light));
            Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(ElementTypeId.Dark, r));
            Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(r, ElementTypeId.Dark));
        }
    }

    [Fact]
    public void Original_four_element_matrix_is_byte_identical()
    {
        // Hardcoded pre-extension relations — the regression lock.
        Assert.Equal(ElementMatchupRelation.Strong, ElementRingMatrix.GetRelation(ElementTypeId.Fire, ElementTypeId.Ice));
        Assert.Equal(ElementMatchupRelation.Weak, ElementRingMatrix.GetRelation(ElementTypeId.Fire, ElementTypeId.Air));
        Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(ElementTypeId.Fire, ElementTypeId.Earth));
        Assert.Equal(ElementMatchupRelation.Strong, ElementRingMatrix.GetRelation(ElementTypeId.Ice, ElementTypeId.Earth));
        Assert.Equal(ElementMatchupRelation.Weak, ElementRingMatrix.GetRelation(ElementTypeId.Ice, ElementTypeId.Fire));
        Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(ElementTypeId.Ice, ElementTypeId.Air));
        Assert.Equal(ElementMatchupRelation.Strong, ElementRingMatrix.GetRelation(ElementTypeId.Earth, ElementTypeId.Air));
        Assert.Equal(ElementMatchupRelation.Weak, ElementRingMatrix.GetRelation(ElementTypeId.Earth, ElementTypeId.Ice));
        Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(ElementTypeId.Earth, ElementTypeId.Fire));
        Assert.Equal(ElementMatchupRelation.Strong, ElementRingMatrix.GetRelation(ElementTypeId.Air, ElementTypeId.Fire));
        Assert.Equal(ElementMatchupRelation.Weak, ElementRingMatrix.GetRelation(ElementTypeId.Air, ElementTypeId.Earth));
        Assert.Equal(ElementMatchupRelation.Neutral, ElementRingMatrix.GetRelation(ElementTypeId.Air, ElementTypeId.Ice));
        foreach (var e in new[] { ElementTypeId.Fire, ElementTypeId.Ice, ElementTypeId.Air, ElementTypeId.Earth })
            Assert.Equal(ElementMatchupRelation.Same, ElementRingMatrix.GetRelation(e, e));
    }

    [Fact]
    public void Exhaustiveness_walk_reader_matrix_palette_never_throw_or_fall_through()
    {
        var snap = ActorDerivedSnapshot.StubNeutral();
        foreach (var e in ElementRoster.Concrete)
        {
            // All 8 CombatDerivedReader families resolve a channel without throwing.
            CombatDerivedReader.Power(snap, e);
            CombatDerivedReader.Defense(snap, e);
            CombatDerivedReader.Accuracy(snap, e);
            CombatDerivedReader.Dodge(snap, e);
            CombatDerivedReader.CritRate(snap, e);
            CombatDerivedReader.CritResist(snap, e);
            CombatDerivedReader.CritDamage(snap, e);
            CombatDerivedReader.CritResistDamage(snap, e);

            // Palette: every roster element has its own color — never the omni white fallback.
            Assert.NotEqual((255, 255, 255), ElementFxPalette.Rgb(e.ToElementId()));

            // Palette Concrete() keeps every roster element.
            var kept = ElementFxPalette.Concrete(new[]
            {
                new ElementPayloadComponentDto { Element = e.ToElementId(), Weight = 1.0 }
            });
            Assert.Single(kept);
        }
    }

    [Fact]
    public void Element_parse_rejects_numeric_strings_and_unknown_names()
    {
        Assert.False(ElementRoster.TryParse("42", out _));
        Assert.False(ElementRoster.TryParse("4", out _));
        Assert.False(ElementRoster.TryParse("5", out _));
        Assert.False(ElementRoster.TryParse("water", out _));
        Assert.False(ElementRoster.TryParse("omni", out _));
        Assert.False(ElementRoster.TryParse(null, out _));
        Assert.Throws<ArgumentException>(() => ActorElementTypes.Parse("4", null));
        Assert.Throws<ArgumentException>(() => OverlayCombatCalculator.ParseComponents(new[]
        {
            new ElementPayloadComponentDto { Element = "5", Weight = 1.0 }
        }));
        Assert.True(ElementRoster.TryParse("LIGHT", out var light));
        Assert.Equal(ElementTypeId.Light, light);
    }

    [Fact]
    public void Dual_type_mutual_counter_composes_multiplicatively()
    {
        // Light attacker vs (dark, fire) defender: STR 1.25 × NEU 1.0 → +0.25 × base share.
        var bonus = ElementHubDualTypeBonus(ElementTypeId.Light, ElementTypeId.Dark, ElementTypeId.Fire);
        Assert.Equal(0.25, bonus, 10);
    }

    static double ElementHubDualTypeBonus(ElementTypeId attacker, ElementTypeId d1, ElementTypeId d2)
    {
        var m1 = 1.0 + ElementRingMatrix.RelationShare(ElementRingMatrix.GetRelation(attacker, d1));
        var m2 = 1.0 + ElementRingMatrix.RelationShare(ElementRingMatrix.GetRelation(attacker, d2));
        return m1 * m2 - 1.0;
    }
}
