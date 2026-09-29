using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Binding;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Binding;

/// <summary>
/// 2026-09-06 real-run finding + owner decision (spec-tree-binder.md's own filed note):
/// `AffixComposer` needs one `AffixRow` per family, but the item program's own
/// `family-expand.&lt;stem&gt;.json` output only ever carries per-tier `AtomRow`s — no `kind: "affix"`
/// wrapper exists anywhere in the real seed data. The owner chose: synthesize one canonical,
/// tier-independent `AffixRow` per family (the numeric band is irrelevant to a passive-tree bind,
/// which prices from `budgetShareMilli`, never from an atom's own amount range).
/// </summary>
public class AffixFamilySynthesisTests
{
    static AtomRow Atom(string family, int tier, string atomId) => new()
    {
        AtomId = atomId, KindId = "stat.modify", FamilyId = family, Tier = tier,
        Name = $"{family} T{tier}", ParamsJson = "{}", WhenJson = "{}", TagsJson = "{}",
    };

    [Fact]
    public void A_family_with_no_authored_affix_row_gets_one_synthesized()
    {
        var atomsById = new Dictionary<string, AtomRow>
        {
            ["atom.might.t1"] = Atom("atom.might", tier: 1, "atom.might.t1"),
        };

        var affixesById = AffixFamilySynthesis.WithSynthesizedFamilyAffixes(
            new Dictionary<string, AffixRow>(), atomsById);

        Assert.True(affixesById.TryGetValue("atom.might", out var affix));
        var reference = Assert.Single(affix!.Refs);
        Assert.Equal("atom.might.t1", reference.AtomId);
    }

    [Fact]
    public void The_lowest_tier_present_is_the_canonical_shape_regardless_of_dictionary_order()
    {
        var atomsById = new Dictionary<string, AtomRow>
        {
            ["atom.might.t5"] = Atom("atom.might", tier: 5, "atom.might.t5"),
            ["atom.might.t1"] = Atom("atom.might", tier: 1, "atom.might.t1"),
            ["atom.might.t3"] = Atom("atom.might", tier: 3, "atom.might.t3"),
        };

        var affixesById = AffixFamilySynthesis.WithSynthesizedFamilyAffixes(
            new Dictionary<string, AffixRow>(), atomsById);

        Assert.Equal("atom.might.t1", affixesById["atom.might"].Refs.Single().AtomId);
    }

    [Fact]
    public void An_explicit_real_affix_row_is_never_overwritten_by_a_synthesized_one()
    {
        var atomsById = new Dictionary<string, AtomRow>
        {
            ["atom.might.t1"] = Atom("atom.might", tier: 1, "atom.might.t1"),
        };
        var handAuthored = new AffixRow("atom.might", Class: null,
            Refs: new[] { new AffixRefRow(Seq: 0, AtomId: "some.other.hand-picked.atom") });

        var affixesById = AffixFamilySynthesis.WithSynthesizedFamilyAffixes(
            new Dictionary<string, AffixRow> { ["atom.might"] = handAuthored }, atomsById);

        Assert.Same(handAuthored, affixesById["atom.might"]);
    }

    [Fact]
    public void Multiple_distinct_families_each_get_their_own_synthesized_row()
    {
        var atomsById = new Dictionary<string, AtomRow>
        {
            ["atom.might.t1"] = Atom("atom.might", tier: 1, "atom.might.t1"),
            ["atom.ferocity.t1"] = Atom("atom.ferocity", tier: 1, "atom.ferocity.t1"),
        };

        var affixesById = AffixFamilySynthesis.WithSynthesizedFamilyAffixes(
            new Dictionary<string, AffixRow>(), atomsById);

        Assert.Equal(2, affixesById.Count);
        Assert.Equal("atom.might.t1", affixesById["atom.might"].Refs.Single().AtomId);
        Assert.Equal("atom.ferocity.t1", affixesById["atom.ferocity"].Refs.Single().AtomId);
    }

    [Fact]
    public void An_atom_with_no_family_id_is_never_synthesized_into_an_affix()
    {
        var atomsById = new Dictionary<string, AtomRow>
        {
            ["board.action.clear"] = Atom(family: "", tier: 1, "board.action.clear"),
        };

        var affixesById = AffixFamilySynthesis.WithSynthesizedFamilyAffixes(
            new Dictionary<string, AffixRow>(), atomsById);

        Assert.Empty(affixesById);
    }
}
