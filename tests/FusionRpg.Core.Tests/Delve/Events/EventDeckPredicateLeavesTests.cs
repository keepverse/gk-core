using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>
/// D3.7 (spec-event-deck.md §6: "Real gaps — four leaves, each a reviewed code change"): `BandIs`,
/// `HaulAtLeast`, `RoomKindIs`, `PartyDownedCount`. Named `EventDeckPredicateLeaves*` matching
/// `ActionUsabilityHoldsStockTests.cs`'s own precedent of a dedicated file per program-boundary leaf
/// addition, rather than folding into the generic `PredicateCompilerTests.cs`. The cross-implementation
/// (`TypedGraph` vs the real `FlatPredicate` vs a naive reference) proof already lives in
/// `PredicateEquivalenceTests.cs`, extended for these four leaves separately — this file is the
/// leaf-by-leaf red/green and "reads the right fact" documentation the fuzz doesn't spell out by name.
/// </summary>
public class EventDeckPredicateLeavesTests
{
    static AtomRejection Compile(PredicateNode.Leaf leaf, out ICompiledPredicate compiled) =>
        PredicateCompiler.TryCompile(leaf, statusBit: null, out compiled);

    static bool Eval(ICompiledPredicate compiled, EntityFacts self, EntityFacts target)
    {
        var facts = new FactReader(self, target);
        return compiled.Evaluate(ref facts);
    }

    static EntityFacts Facts(int band = 0, int roomKind = 0, int haulCount = 0, int downedCount = 0) =>
        new(Side: 0, TypeId: 0, HpMilli: 1000, ElementId: -1, Row: -1, Col: -1,
            IsMindControlled: false, IsKiller: false, StatusMask: 0,
            Band: band, RoomKind: roomKind, HaulCount: haulCount, DownedCount: downedCount);

    // ---- red/green validation, one theory per leaf -----------------------------------------------

    [Theory]
    [InlineData(LeafId.BandIs)]
    [InlineData(LeafId.HaulAtLeast)]
    [InlineData(LeafId.RoomKindIs)]
    [InlineData(LeafId.PartyDownedCount)]
    public void A_negative_value_is_rejected_red(LeafId id)
    {
        var r = Compile(new PredicateNode.Leaf(id, Subject.Target, Value: -1), out _);
        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Theory]
    [InlineData(LeafId.BandIs)]
    [InlineData(LeafId.HaulAtLeast)]
    [InlineData(LeafId.RoomKindIs)]
    [InlineData(LeafId.PartyDownedCount)]
    public void A_zero_value_compiles_green(LeafId id)
    {
        var r = Compile(new PredicateNode.Leaf(id, Subject.Target, Value: 0), out _);
        Assert.True(r.IsOk);
    }

    [Theory]
    [InlineData(LeafId.BandIs)]
    [InlineData(LeafId.HaulAtLeast)]
    [InlineData(LeafId.RoomKindIs)]
    [InlineData(LeafId.PartyDownedCount)]
    public void A_missing_subject_is_rejected(LeafId id)
    {
        var r = Compile(new PredicateNode.Leaf(id, (Subject)9, Value: 0), out _);
        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.AmbiguousSubject, r.Reason);
    }

    // ---- BandIs: equality on the room's own band, Target side -------------------------------------

    [Fact]
    public void BandIs_matches_only_the_exact_band()
    {
        Compile(new PredicateNode.Leaf(LeafId.BandIs, Subject.Target, Value: 2), out var compiled);
        Assert.True(Eval(compiled, Facts(), Facts(band: 2)));
        Assert.False(Eval(compiled, Facts(), Facts(band: 1)));
        Assert.False(Eval(compiled, Facts(), Facts(band: 3)));
    }

    [Fact]
    public void BandIs_reads_the_declared_subject_not_the_other_one()
    {
        Compile(new PredicateNode.Leaf(LeafId.BandIs, Subject.Self, Value: 2), out var compiled);
        // Self carries band 2, Target carries band 0 -- a Self-subject leaf must read Self, not Target.
        Assert.True(Eval(compiled, Facts(band: 2), Facts(band: 0)));
        Assert.False(Eval(compiled, Facts(band: 0), Facts(band: 2)));
    }

    // ---- RoomKindIs: equality on the room's own kind ordinal, Target side -------------------------

    [Fact]
    public void RoomKindIs_matches_only_the_exact_ordinal()
    {
        Compile(new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: 5), out var compiled);
        Assert.True(Eval(compiled, Facts(), Facts(roomKind: 5)));
        Assert.False(Eval(compiled, Facts(), Facts(roomKind: 4)));
        Assert.False(Eval(compiled, Facts(), Facts(roomKind: 6)));
    }

    // ---- HaulAtLeast: minimum comparison on occupied pack cells, Self side ------------------------

    [Fact]
    public void HaulAtLeast_is_a_minimum_not_an_exact_match()
    {
        Compile(new PredicateNode.Leaf(LeafId.HaulAtLeast, Subject.Self, Value: 3), out var compiled);
        Assert.False(Eval(compiled, Facts(haulCount: 2), Facts()));
        Assert.True(Eval(compiled, Facts(haulCount: 3), Facts()));
        Assert.True(Eval(compiled, Facts(haulCount: 10), Facts()));
    }

    [Fact]
    public void HaulAtLeast_of_zero_is_always_true_at_least_zero_cells_is_trivial()
    {
        Compile(new PredicateNode.Leaf(LeafId.HaulAtLeast, Subject.Self, Value: 0), out var compiled);
        Assert.True(Eval(compiled, Facts(haulCount: 0), Facts()));
    }

    // ---- PartyDownedCount: minimum comparison on downed members, Self side -----------------------

    [Fact]
    public void PartyDownedCount_is_a_minimum_not_an_exact_match()
    {
        Compile(new PredicateNode.Leaf(LeafId.PartyDownedCount, Subject.Self, Value: 2), out var compiled);
        Assert.False(Eval(compiled, Facts(downedCount: 1), Facts()));
        Assert.True(Eval(compiled, Facts(downedCount: 2), Facts()));
        Assert.True(Eval(compiled, Facts(downedCount: 4), Facts()));
    }

    // ---- composition: the four leaves combine through And/Or/Not exactly like every other leaf ----

    [Fact]
    public void The_four_leaves_compose_through_And_like_every_other_leaf()
    {
        var tree = new PredicateNode.And(new PredicateNode[]
        {
            new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: 5),
            new PredicateNode.Leaf(LeafId.BandIs, Subject.Target, Value: 2),
            new PredicateNode.Leaf(LeafId.PartyDownedCount, Subject.Self, Value: 1),
        });
        var r = PredicateCompiler.TryCompile(tree, statusBit: null, out var compiled);
        Assert.True(r.IsOk);

        Assert.True(Eval(compiled, Facts(downedCount: 1), Facts(roomKind: 5, band: 2)));
        Assert.False(Eval(compiled, Facts(downedCount: 0), Facts(roomKind: 5, band: 2))); // downed count fails
        Assert.False(Eval(compiled, Facts(downedCount: 1), Facts(roomKind: 4, band: 2))); // room kind fails
    }
}
