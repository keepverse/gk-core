using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E3 acceptance (spec-predicate-tree.md). The theme of every case: an unknown or malformed thing is
/// a typed <b>rejection</b>, never a silently-false leaf — that is the whole reason this module owns
/// the `when` condition instead of a filter dictionary.
/// </summary>
public class PredicateCompilerTests
{
    static PredicateNode.Leaf Side(string side, Subject s = Subject.Self) =>
        new(LeafId.SideIs, s, Text: side);

    static FactReader Facts(int selfSide = 0, int targetSide = 1, int selfHpMilli = 1000) =>
        new(new EntityFacts(selfSide, 100, selfHpMilli, 0, 2, 3, false, false, 0UL),
            new EntityFacts(targetSide, 200, 500, 1, 4, 5, false, true, 0b110UL));

    static (AtomRejection R, ICompiledPredicate C) Compile(PredicateNode? tree)
    {
        var r = PredicateCompiler.TryCompile(tree, StatusBit, out var c);
        return (r, c);
    }

    // Deterministic interning stand-in: E18 owns the real table.
    static int StatusBit(string id) => id switch { "chilled" => 1, "burning" => 2, _ => -1 };

    // ---- absent and empty ---------------------------------------------------------------------

    [Fact]
    public void An_absent_predicate_means_always()
    {
        var (r, c) = Compile(null);
        var f = Facts();

        Assert.True(r.IsOk);
        Assert.True(c.Evaluate(ref f));
    }

    [Fact]
    public void An_And_with_no_children_is_rejected_not_treated_as_always()
    {
        // Absent means "always" and is legal. And() would quietly mean the same thing while looking
        // like a real condition — that ambiguity is the bug.
        var (r, _) = Compile(new PredicateNode.And(Array.Empty<PredicateNode>()));
        Assert.Equal(AtomRejectionReason.EmptyNode, r.Reason);
    }

    [Fact]
    public void An_Or_with_no_children_is_rejected()
    {
        var (r, _) = Compile(new PredicateNode.Or(Array.Empty<PredicateNode>()));
        Assert.Equal(AtomRejectionReason.EmptyNode, r.Reason);
    }

    // ---- the closed list ----------------------------------------------------------------------

    [Fact]
    public void An_unknown_leaf_id_is_rejected_never_ignored()
    {
        var (r, _) = Compile(new PredicateNode.Leaf((LeafId)99, Subject.Self));
        Assert.Equal(AtomRejectionReason.UnknownLeaf, r.Reason);
    }

    [Fact]
    public void A_leaf_without_a_subject_is_rejected()
    {
        // OnDamageDealt inverts side and typeId, so a defaulted subject silently means the wrong
        // entity. There is no default; an out-of-range value is how a JSON omission arrives.
        var (r, _) = Compile(new PredicateNode.Leaf(LeafId.SideIs, (Subject)7, Text: "zombie"));
        Assert.Equal(AtomRejectionReason.AmbiguousSubject, r.Reason);
    }

    [Theory]
    [InlineData(LeafId.SideIs)]
    [InlineData(LeafId.HasStatus)]
    [InlineData(LeafId.ElementIs)]
    public void Text_leaves_need_their_text_arg(LeafId id)
    {
        var (r, _) = Compile(new PredicateNode.Leaf(id, Subject.Self, Text: null));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void TypeIdIn_needs_a_non_empty_set()
    {
        var (r, _) = Compile(new PredicateNode.Leaf(LeafId.TypeIdIn, Subject.Self, Values: Array.Empty<int>()));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Hp_leaves_take_per_mille_in_range(int milli)
    {
        var (r, _) = Compile(new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Self, Value: milli));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    // ---- limits -------------------------------------------------------------------------------

    [Fact]
    public void Depth_four_is_allowed_and_depth_five_is_rejected()
    {
        // A bare leaf is depth 1, so And(And(And(leaf))) is depth 4.
        PredicateNode d4 = new PredicateNode.And(new PredicateNode[]
        { new PredicateNode.And(new PredicateNode[] { new PredicateNode.And(new PredicateNode[] { Side("zombie") }) }) });
        Assert.True(Compile(d4).R.IsOk);

        PredicateNode d5 = new PredicateNode.And(new PredicateNode[] { d4 });
        Assert.Equal(AtomRejectionReason.DepthExceeded, Compile(d5).R.Reason);
    }

    [Fact]
    public void Seventeen_nodes_is_rejected_even_at_a_legal_depth()
    {
        // The node cap is the second bound: a wide flat tree evades the depth limit entirely.
        var wide = Enumerable.Range(0, 16).Select(_ => (PredicateNode)Side("zombie")).ToArray();
        var (r, _) = Compile(new PredicateNode.And(wide)); // 16 leaves + the And = 17

        Assert.Equal(AtomRejectionReason.NodeCountExceeded, r.Reason);
    }

    [Fact]
    public void Sixteen_nodes_is_allowed()
    {
        var wide = Enumerable.Range(0, 15).Select(_ => (PredicateNode)Side("zombie")).ToArray();
        Assert.True(Compile(new PredicateNode.And(wide)).R.IsOk);
    }

    // ---- evaluation ---------------------------------------------------------------------------

    [Fact]
    public void Subject_selects_which_entity_the_leaf_reads()
    {
        var self = Compile(Side("plant", Subject.Self)).C;
        var target = Compile(Side("plant", Subject.Target)).C;
        var f = Facts(selfSide: 0, targetSide: 1);

        Assert.True(self.Evaluate(ref f));    // self is a plant
        Assert.False(target.Evaluate(ref f)); // target is a zombie
    }

    [Fact]
    public void Not_inverts()
    {
        var c = Compile(new PredicateNode.Not(Side("zombie"))).C;
        var f = Facts(selfSide: 0);
        Assert.True(c.Evaluate(ref f));
    }

    [Fact]
    public void An_unknown_status_id_never_matches_rather_than_matching_everything()
    {
        var c = Compile(new PredicateNode.Leaf(LeafId.HasStatus, Subject.Target, Text: "no-such-status")).C;
        var f = Facts();
        Assert.False(c.Evaluate(ref f));
    }

    [Fact]
    public void Status_is_tested_by_interned_bit()
    {
        var chilled = Compile(new PredicateNode.Leaf(LeafId.HasStatus, Subject.Target, Text: "chilled")).C;
        var burning = Compile(new PredicateNode.Leaf(LeafId.HasStatus, Subject.Target, Text: "burning")).C;
        var f = Facts(); // target mask 0b110 -> bits 1 and 2 set

        Assert.True(chilled.Evaluate(ref f));
        Assert.True(burning.Evaluate(ref f));
    }

    // ---- short-circuit and allocation ---------------------------------------------------------

    [Fact]
    public void And_short_circuits_and_never_reads_the_second_leaf()
    {
        var c = Compile(new PredicateNode.And(new PredicateNode[]
        {
            Side("bullet"),          // self is a plant -> false
            Side("plant"),           // must never be reached
        })).C;

        var f = Facts(selfSide: 0);
        Assert.False(c.Evaluate(ref f));
        Assert.Equal(1, f.Reads);
    }

    [Fact]
    public void Or_short_circuits_on_the_first_true()
    {
        var c = Compile(new PredicateNode.Or(new PredicateNode[] { Side("plant"), Side("zombie") })).C;

        var f = Facts(selfSide: 0);
        Assert.True(c.Evaluate(ref f));
        Assert.Equal(1, f.Reads);
    }

    [Fact]
    public void Evaluating_allocates_nothing()
    {
        var c = Compile(new PredicateNode.And(new PredicateNode[]
        {
            Side("plant"),
            new PredicateNode.Not(new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 250)),
            new PredicateNode.Leaf(LeafId.TypeIdIn, Subject.Target, Values: new[] { 1, 2, 200 }),
        })).C;

        var warm = Facts();
        for (var i = 0; i < 1000; i++) c.Evaluate(ref warm);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var f = Facts();
        for (var i = 0; i < 100_000; i++) c.Evaluate(ref f);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
