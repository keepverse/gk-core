using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E18: the element roster and both matchup matrices as rows (spec-element-roster-data.md).
///
/// <para>The headline is that a seventh element costs rows plus regeneration rather than an edit
/// across five files — and the guard on that is the <b>ordinal</b>, which names every generated
/// channel. Move one and <c>combat.power.fire</c> quietly becomes something else.</para>
/// </summary>
public class ElementRosterDataTests
{

    // ---- ordinals ----------------------------------------------------------------------------

    [Theory]
    [InlineData("fire", 0)]
    [InlineData("ice", 1)]
    [InlineData("air", 2)]
    [InlineData("earth", 3)]
    [InlineData("light", 4)]
    [InlineData("dark", 5)]
    public void The_six_shipped_ordinals_are_pinned(string elementId, int ordinal)
    {
        // A reorder must fail here loudly rather than move the generated channel set silently.
        Assert.Equal(ordinal, ElementTable.Shipped().Find(elementId)!.Ordinal);
    }

    [Fact]
    public void The_enum_and_the_roster_agree_on_order()
    {
        // While ElementTypeId is still code it is a mirror of the rows, and a mirror that has drifted
        // is worse than no mirror: every typed call site would read a different element.
        Assert.Equal(
            ElementRoster.Concrete.Select(ElementTable.IdOf),
            ElementTable.Shipped().Elements.Select(e => e.ElementId));
    }

    // ---- the matrices ------------------------------------------------------------------------

    [Fact]
    public void The_combat_matrix_from_rows_matches_the_shipped_switch_for_all_thirty_six_pairs()
    {
        // The relations below are the shipped switch, written out. Comparing the table against
        // itself would agree about nothing.
        foreach (var (a, d, want) in ShippedRing())
            Assert.Equal(want, ElementRingMatrix.GetRelation(a, d));
    }

    [Fact]
    public void The_shield_matrix_from_rows_matches_for_all_thirty_six_pairs()
    {
        foreach (var (a, d, relation) in ShippedRing())
        {
            var want = relation switch
            {
                ElementMatchupRelation.Strong => 1,
                ElementMatchupRelation.Weak => -1,
                _ => 0,
            };
            Assert.Equal(want, ShieldElementMatrix.RelationUnit(a, d));
        }
    }

    [Fact]
    public void The_two_matrices_are_identical_today_which_is_not_what_the_spec_said()
    {
        // spec-element-roster-data.md warned they "genuinely differ", citing light/dark. They do not:
        // light and dark are mutually strong in both, and every one of the 36 pairs agrees. Recorded
        // as a test so the claim cannot drift back into a warning nobody checked.
        foreach (var a in ElementRoster.Concrete)
        foreach (var d in ElementRoster.Concrete)
        {
            if (a == d) continue; // combat says Same, shield collapses that to 0 — a real difference
            var ring = ElementRingMatrix.GetRelation(a, d) switch
            {
                ElementMatchupRelation.Strong => 1,
                ElementMatchupRelation.Weak => -1,
                _ => 0,
            };
            Assert.Equal(ring, ShieldElementMatrix.RelationUnit(a, d));
        }
    }

    [Fact]
    public void Light_and_dark_are_mutually_strong_in_both()
    {
        Assert.Equal(ElementMatchupRelation.Strong,
            ElementRingMatrix.GetRelation(ElementTypeId.Light, ElementTypeId.Dark));
        Assert.Equal(ElementMatchupRelation.Strong,
            ElementRingMatrix.GetRelation(ElementTypeId.Dark, ElementTypeId.Light));
        Assert.Equal(1, ShieldElementMatrix.RelationUnit(ElementTypeId.Light, ElementTypeId.Dark));
        Assert.Equal(1, ShieldElementMatrix.RelationUnit(ElementTypeId.Dark, ElementTypeId.Light));
    }

    [Fact]
    public void Editing_one_matrix_leaves_the_other_alone()
    {
        // The reason there are two tables. One shared table would make divergence inexpressible and
        // a future shield rebalance would silently move combat.
        var shipped = ElementTable.Shipped();
        var combat = shipped.CombatRows.ToList();
        var shield = shipped.ShieldRows
            .Select(r => r.Attacker == "fire" && r.Defender == "ice" ? r with { Unit = -1 } : r)
            .ToList();

        using var _ = ElementTable.UseScoped(new ElementTable(shipped.Elements, combat, shield));

        Assert.Equal(ElementMatchupRelation.Strong,
            ElementRingMatrix.GetRelation(ElementTypeId.Fire, ElementTypeId.Ice));
        Assert.Equal(-1, ShieldElementMatrix.RelationUnit(ElementTypeId.Fire, ElementTypeId.Ice));
    }

    // ---- the seventh element -------------------------------------------------------------------

    [Fact]
    public void A_seventh_element_generates_its_twenty_eight_channels_with_no_code_change()
    {
        // The whole claim. No new enum member, no new constant, no new family — a row.
        var before = DerivedStatChannels.AllCombatChannelIds.Count;
        var shipped = ElementTable.Shipped();
        var withVoid = shipped.Elements.Append(new ElementRow("void", "Void", 6)).ToList();

        using var _ = ElementTable.UseScoped(new ElementTable(withVoid, shipped.CombatRows, shipped.ShieldRows));

        var after = DerivedStatChannels.AllCombatChannelIds;
        Assert.Equal(before + DerivedStatChannels.CombatChannelFamilies.Count, after.Count);
        Assert.Contains("combat.power.void", after);
        Assert.Contains("combat.shield.capacity.void", after);
    }

    [Fact]
    public void The_channel_count_is_the_formula_not_a_literal()
    {
        // 28 × (6 + omni) = 196 today (was 12 × 7 = 84 before H.1's 16 new families), and it must
        // track the roster rather than contradict it.
        Assert.Equal(
            DerivedStatChannels.CombatChannelFamilies.Count * (ElementTable.Shipped().Elements.Count + 1),
            DerivedStatChannels.AllCombatChannelIds.Count);
    }

    [Fact]
    public void A_disabled_element_generates_no_channels()
    {
        var shipped = ElementTable.Shipped();
        var retired = shipped.Elements
            .Select(e => e.ElementId == "dark" ? e with { Enabled = false } : e)
            .ToList();

        using var _ = ElementTable.UseScoped(new ElementTable(retired, shipped.CombatRows, shipped.ShieldRows));

        Assert.DoesNotContain("combat.power.dark", DerivedStatChannels.AllCombatChannelIds);
        Assert.Contains("combat.power.fire", DerivedStatChannels.AllCombatChannelIds);
    }

    // ---- parsing stays strict --------------------------------------------------------------------

    [Fact]
    public void Numeric_element_ids_are_still_rejected()
    {
        // Enum.TryParse would accept "3" and, once the roster grows, silently remap "4"/"5".
        Assert.False(ElementRoster.TryParse("3", out _));
        Assert.False(ElementRoster.TryParse("0", out _));
        Assert.True(ElementRoster.TryParse("fire", out _));
    }

    [Fact]
    public void Omni_is_still_not_a_legal_actor_element()
    {
        Assert.False(ElementRoster.TryParse(ElementRoster.OmniId, out _));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>The shipped ring, written independently of the table so the comparison means something.</summary>
    static IEnumerable<(ElementTypeId A, ElementTypeId D, ElementMatchupRelation R)> ShippedRing()
    {
        var strong = new HashSet<(ElementTypeId, ElementTypeId)>
        {
            (ElementTypeId.Light, ElementTypeId.Dark), (ElementTypeId.Dark, ElementTypeId.Light),
            (ElementTypeId.Fire, ElementTypeId.Ice), (ElementTypeId.Ice, ElementTypeId.Earth),
            (ElementTypeId.Earth, ElementTypeId.Air), (ElementTypeId.Air, ElementTypeId.Fire),
        };
        var weak = new HashSet<(ElementTypeId, ElementTypeId)>
        {
            (ElementTypeId.Fire, ElementTypeId.Air), (ElementTypeId.Ice, ElementTypeId.Fire),
            (ElementTypeId.Earth, ElementTypeId.Ice), (ElementTypeId.Air, ElementTypeId.Earth),
        };

        foreach (var a in ElementRoster.Concrete)
        foreach (var d in ElementRoster.Concrete)
        {
            var r = a == d ? ElementMatchupRelation.Same
                : strong.Contains((a, d)) ? ElementMatchupRelation.Strong
                : weak.Contains((a, d)) ? ElementMatchupRelation.Weak
                : ElementMatchupRelation.Neutral;
            yield return (a, d, r);
        }
    }
}
