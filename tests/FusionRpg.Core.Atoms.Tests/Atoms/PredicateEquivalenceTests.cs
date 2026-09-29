using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// The fuzz that makes E13 safe. E13 will benchmark candidate encodings and may replace the compiled
/// form entirely; this asserts the compiled result matches a deliberately naive reference interpreter
/// over 10⁴ random trees × random facts. If a future encoding disagrees anywhere, this goes red.
///
/// <para>The reference below is written for obviousness, not speed — that is the point of it.</para>
/// </summary>
public class PredicateEquivalenceTests
{
    const int Trees = 10_000;

    /// <summary>The candidate encodings E13 measures. Both must agree with the reference.</summary>
    public enum Form { TypedGraph, Flat }

    static ICompiledPredicate Compile(Form form, PredicateNode tree, out AtomRejection r)
    {
        r = PredicateCompiler.TryCompile(tree, StatusBit, out var typed, ElementId, StockBit);
        if (!r.IsOk) return typed;

        return form == Form.TypedGraph
            ? typed
            : FlatPredicate.Build(tree, StatusBit, ElementId, StockBit);
    }

    [Theory]
    [InlineData(Form.TypedGraph)]
    [InlineData(Form.Flat)]
    public void Every_candidate_encoding_matches_the_reference_interpreter(Form form)
    {
        // E13 may swap the compiled form. This is what makes that safe: a candidate that disagrees
        // with the naive interpreter anywhere is not a faster encoding, it is a different one.
        var rng = new Random(20260822);
        var mismatches = 0;
        var evaluated = 0;

        for (var i = 0; i < Trees; i++)
        {
            var tree = RandomNode(rng, depth: 1);
            var compiled = Compile(form, tree, out var r);
            if (!r.IsOk) continue;

            for (var k = 0; k < 4; k++)
            {
                var facts = RandomFacts(rng);
                var f = new FactReader(facts.Self, facts.Target);

                if (compiled.Evaluate(ref f) != Reference(tree, facts)) mismatches++;
                evaluated++;
            }
        }

        Assert.True(evaluated > 20_000, $"fuzz only evaluated {evaluated} cases");
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void Compiled_matches_the_reference_interpreter_over_ten_thousand_trees()
    {
        var rng = new Random(20260822); // fixed seed: a failure must be reproducible
        var mismatches = 0;
        var evaluated = 0;

        for (var i = 0; i < Trees; i++)
        {
            var tree = RandomNode(rng, depth: 1);

            var r = PredicateCompiler.TryCompile(tree, StatusBit, out var compiled, ElementId, StockBit);
            if (!r.IsOk) continue; // rejected trees are the other tests' business

            for (var k = 0; k < 4; k++)
            {
                var facts = RandomFacts(rng);
                var f = new FactReader(facts.Self, facts.Target);

                var actual = compiled.Evaluate(ref f);
                var expected = Reference(tree, facts);

                evaluated++;
                if (actual != expected) mismatches++;
            }
        }

        Assert.True(evaluated > 20_000, $"fuzz only evaluated {evaluated} cases");
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void The_fuzz_generator_produces_trees_the_compiler_rejects_too()
    {
        // A generator that only ever produces valid trees would make the equivalence claim vacuous
        // on the rejection paths. Assert it exercises both sides.
        var rng = new Random(7);
        int ok = 0, rejected = 0;

        for (var i = 0; i < 2000; i++)
        {
            var tree = RandomNode(rng, depth: 1, allowInvalid: true);
            if (PredicateCompiler.TryCompile(tree, StatusBit, out _, ElementId, StockBit).IsOk) ok++; else rejected++;
        }

        Assert.True(ok > 0 && rejected > 0, $"ok={ok} rejected={rejected}");
    }

    // ---- reference interpreter: naive on purpose ----------------------------------------------

    static bool Reference(PredicateNode node, (EntityFacts Self, EntityFacts Target) f) => node switch
    {
        PredicateNode.And a => a.Children.All(c => Reference(c, f)),
        PredicateNode.Or o => o.Children.Any(c => Reference(c, f)),
        PredicateNode.Not n => !Reference(n.Child, f),
        PredicateNode.Leaf l => Leaf(l, l.Subject == Subject.Self ? f.Self : f.Target),
        _ => true,
    };

    static bool Leaf(PredicateNode.Leaf l, EntityFacts e) => l.Id switch
    {
        LeafId.SideIs => e.Side == SideOrdinal(l.Text),
        LeafId.TypeIdIs => e.TypeId == l.Value,
        LeafId.TypeIdIn => l.Values!.Contains(e.TypeId),
        LeafId.ActorIsKiller => e.IsKiller == (l.Value != 0),
        LeafId.HasStatus => Bit(e.StatusMask, StatusBit(l.Text!)),
        LeafId.HpBelowMilli => e.HpMilli < l.Value,
        LeafId.HpAboveMilli => e.HpMilli > l.Value,
        LeafId.ElementIs => e.ElementId == ElementId(l.Text!),
        LeafId.RowIs => e.Row == l.Value,
        LeafId.ColIs => e.Col == l.Value,
        LeafId.IsMindControlled => e.IsMindControlled == (l.Value != 0),
        LeafId.HoldsStock => StockQtyRef(e, StockBit(l.Text!)) >= l.Value,
        LeafId.BandIs => e.Band == l.Value,
        LeafId.HaulAtLeast => e.HaulCount >= l.Value,
        LeafId.RoomKindIs => e.RoomKind == l.Value,
        LeafId.PartyDownedCount => e.DownedCount >= l.Value,
        _ => true,
    };

    static bool Bit(ulong mask, int bit) => bit >= 0 && bit < 64 && (mask & (1UL << bit)) != 0;

    static int StockQtyRef(EntityFacts e, int stockIndex) => stockIndex switch
    {
        0 => e.Stock0Qty, 1 => e.Stock1Qty, 2 => e.Stock2Qty, 3 => e.Stock3Qty, _ => 0,
    };

    static int SideOrdinal(string? s) => s switch { "plant" => 0, "zombie" => 1, "bullet" => 2, _ => -1 };

    static int StatusBit(string id) => id switch { "chilled" => 1, "burning" => 2, "wet" => 3, _ => -1 };

    // Deliberately DIFFERENT numbers from StatusBit for the same names: if the compiler ever routes
    // an element through the status resolver again, the fuzz diverges instead of agreeing.
    static int ElementId(string id) => id switch { "chilled" => 3, "burning" => 0, "wet" => 2, _ => -1 };

    // Deliberately DIFFERENT numbers again, and only 4 slots (0-3) so the "unknown-stock" name and an
    // out-of-range index are both exercised, matching StockQty's own "reads as 0" contract.
    static int StockBit(string id) => id switch { "potion" => 2, "bandage" => 0, "elixir" => 3, _ => -1 };

    // ---- generators ---------------------------------------------------------------------------

    static readonly string[] Sides = { "plant", "zombie", "bullet" };
    static readonly string[] Statuses = { "chilled", "burning", "wet", "unknown-status" };
    static readonly string[] StockIds = { "potion", "bandage", "elixir", "unknown-stock" };

    static PredicateNode RandomNode(Random rng, int depth, bool allowInvalid = false)
    {
        var atLimit = depth >= PredicateCompiler.MaxDepth;
        var roll = rng.Next(atLimit ? 6 : 10);

        return roll switch
        {
            < 6 => RandomLeaf(rng, allowInvalid),
            6 or 7 => new PredicateNode.And(Children(rng, depth, allowInvalid)),
            8 => new PredicateNode.Or(Children(rng, depth, allowInvalid)),
            _ => new PredicateNode.Not(RandomNode(rng, depth + 1, allowInvalid)),
        };
    }

    static PredicateNode[] Children(Random rng, int depth, bool allowInvalid)
    {
        var n = allowInvalid && rng.Next(20) == 0 ? 0 : 1 + rng.Next(3);
        return Enumerable.Range(0, n).Select(_ => RandomNode(rng, depth + 1, allowInvalid)).ToArray();
    }

    static PredicateNode RandomLeaf(Random rng, bool allowInvalid)
    {
        var subject = rng.Next(2) == 0 ? Subject.Self : Subject.Target;
        if (allowInvalid && rng.Next(25) == 0) subject = (Subject)9; // omitted-subject shape

        return rng.Next(16) switch
        {
            0 => new PredicateNode.Leaf(LeafId.SideIs, subject, Text: Sides[rng.Next(Sides.Length)]),
            1 => new PredicateNode.Leaf(LeafId.TypeIdIs, subject, Value: rng.Next(300)),
            2 => new PredicateNode.Leaf(LeafId.TypeIdIn, subject,
                     Values: Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => rng.Next(300)).ToArray()),
            3 => new PredicateNode.Leaf(LeafId.ActorIsKiller, subject, Value: rng.Next(2)),
            4 => new PredicateNode.Leaf(LeafId.HasStatus, subject, Text: Statuses[rng.Next(Statuses.Length)]),
            5 => new PredicateNode.Leaf(LeafId.HpBelowMilli, subject, Value: rng.Next(1001)),
            6 => new PredicateNode.Leaf(LeafId.HpAboveMilli, subject, Value: rng.Next(1001)),
            7 => new PredicateNode.Leaf(LeafId.ElementIs, subject, Text: Statuses[rng.Next(Statuses.Length)]),
            8 => new PredicateNode.Leaf(LeafId.RowIs, subject, Value: rng.Next(6)),
            9 => new PredicateNode.Leaf(LeafId.ColIs, subject, Value: rng.Next(10)),
            10 => new PredicateNode.Leaf(LeafId.IsMindControlled, subject, Value: rng.Next(2)),
            11 => new PredicateNode.Leaf(LeafId.HoldsStock, subject,
                     Value: allowInvalid && rng.Next(10) == 0 ? 0 : 1 + rng.Next(5), // occasionally invalid (minQty < 1)
                     Text: StockIds[rng.Next(StockIds.Length)]),
            // D3.7 (event-deck): four plain-Value leaves, occasionally invalid (negative) the same way HoldsStock is above.
            12 => new PredicateNode.Leaf(LeafId.BandIs, subject, Value: allowInvalid && rng.Next(10) == 0 ? -1 - rng.Next(3) : rng.Next(4)),
            13 => new PredicateNode.Leaf(LeafId.HaulAtLeast, subject, Value: allowInvalid && rng.Next(10) == 0 ? -1 - rng.Next(3) : rng.Next(6)),
            14 => new PredicateNode.Leaf(LeafId.RoomKindIs, subject, Value: allowInvalid && rng.Next(10) == 0 ? -1 - rng.Next(3) : rng.Next(11)),
            _ => new PredicateNode.Leaf(LeafId.PartyDownedCount, subject, Value: allowInvalid && rng.Next(10) == 0 ? -1 - rng.Next(3) : rng.Next(5)),
        };
    }

    static (EntityFacts Self, EntityFacts Target) RandomFacts(Random rng)
    {
        EntityFacts One() => new(
            Side: rng.Next(3),
            TypeId: rng.Next(300),
            HpMilli: rng.Next(1001),
            ElementId: rng.Next(-1, 4),
            Row: rng.Next(-1, 6),
            Col: rng.Next(-1, 10),
            IsMindControlled: rng.Next(2) == 0,
            IsKiller: rng.Next(2) == 0,
            StatusMask: (ulong)rng.Next(0, 16),
            Stock0Qty: rng.Next(0, 6),
            Stock1Qty: rng.Next(0, 6),
            Stock2Qty: rng.Next(0, 6),
            Stock3Qty: rng.Next(0, 6),
            Band: rng.Next(0, 4),
            RoomKind: rng.Next(0, 11),
            HaulCount: rng.Next(0, 6),
            DownedCount: rng.Next(0, 5));

        return (One(), One());
    }
}
