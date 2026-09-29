using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Bench;

/// <summary>
/// The content the benchmark runs against: ~200 predicates spread across leaf kinds, depths 1–4 and
/// mixed shapes.
///
/// <para><b>Not six clones.</b> The earlier scratch benchmark reused one tree, which is unrealistically
/// kind to branch prediction and to the instruction cache — it flatters whichever form has the
/// tightest inner loop and tells you nothing about a real board. These are generated from a fixed
/// seed so the corpus is identical run to run, but every tree differs in shape, depth and leaf mix.</para>
/// </summary>
public static class Corpus
{
    public const int Size = 200;

    static readonly string[] Sides = { "plant", "zombie", "bullet" };
    static readonly string[] Statuses = { "chilled", "burning", "wet", "buttered" };
    static readonly string[] Elements = { "fire", "ice", "air", "earth", "light", "dark" };

    /// <summary>Deterministic interning stands in for E18's roster and the status catalog.</summary>
    public static int StatusBit(string id) => Array.IndexOf(Statuses, id);
    public static int ElementId(string id) => Array.IndexOf(Elements, id);

    /// <summary>The trees, in a fixed order. Same corpus for every candidate, every run.</summary>
    public static List<PredicateNode> Trees()
    {
        var rng = new Random(13_2026);
        var trees = new List<PredicateNode>(Size);

        // A quarter bare leaves, a quarter shallow, half at depth 3-4: roughly what authored content
        // looks like, where most atoms are simple and a few carry a real condition.
        for (var i = 0; i < Size; i++)
        {
            var depth = i % 4 switch { 0 => 1, 1 => 2, 2 => 3, _ => 4 };
            trees.Add(Build(rng, depth));
        }

        return trees;
    }

    static PredicateNode Build(Random rng, int remaining)
    {
        if (remaining <= 1) return Leaf(rng);

        return rng.Next(4) switch
        {
            0 => new PredicateNode.Not(Build(rng, remaining - 1)),
            1 => new PredicateNode.Or(Children(rng, remaining)),
            _ => new PredicateNode.And(Children(rng, remaining)),
        };
    }

    static PredicateNode[] Children(Random rng, int remaining)
    {
        var n = 1 + rng.Next(3);
        var kids = new PredicateNode[n];
        for (var i = 0; i < n; i++) kids[i] = Build(rng, remaining - 1);
        return kids;
    }

    static PredicateNode Leaf(Random rng)
    {
        var subject = rng.Next(2) == 0 ? Subject.Self : Subject.Target;

        return rng.Next(11) switch
        {
            0 => new PredicateNode.Leaf(LeafId.SideIs, subject, Text: Sides[rng.Next(Sides.Length)]),
            1 => new PredicateNode.Leaf(LeafId.TypeIdIs, subject, Value: rng.Next(300)),
            2 => new PredicateNode.Leaf(LeafId.TypeIdIn, subject,
                     Values: Enumerable.Range(0, 1 + rng.Next(4)).Select(_ => rng.Next(300)).ToArray()),
            3 => new PredicateNode.Leaf(LeafId.ActorIsKiller, subject, Value: rng.Next(2)),
            4 => new PredicateNode.Leaf(LeafId.HasStatus, subject, Text: Statuses[rng.Next(Statuses.Length)]),
            5 => new PredicateNode.Leaf(LeafId.HpBelowMilli, subject, Value: rng.Next(1001)),
            6 => new PredicateNode.Leaf(LeafId.HpAboveMilli, subject, Value: rng.Next(1001)),
            7 => new PredicateNode.Leaf(LeafId.ElementIs, subject, Text: Elements[rng.Next(Elements.Length)]),
            8 => new PredicateNode.Leaf(LeafId.RowIs, subject, Value: rng.Next(6)),
            9 => new PredicateNode.Leaf(LeafId.ColIs, subject, Value: rng.Next(10)),
            _ => new PredicateNode.Leaf(LeafId.IsMindControlled, subject, Value: rng.Next(2)),
        };
    }

    /// <summary>
    /// Fact sets to evaluate against — one per owner, so the walk crosses owners rather than hammering
    /// a single hot struct.
    /// </summary>
    public static FactReader[] Owners(int count)
    {
        var rng = new Random(99);
        var owners = new FactReader[count];

        for (var i = 0; i < count; i++)
            owners[i] = new FactReader(One(rng), One(rng));

        return owners;

        EntityFacts One(Random r) => new(
            Side: r.Next(3), TypeId: r.Next(300), HpMilli: r.Next(1001),
            ElementId: r.Next(-1, 6), Row: r.Next(-1, 6), Col: r.Next(-1, 10),
            IsMindControlled: r.Next(2) == 0, IsKiller: r.Next(2) == 0,
            StatusMask: (ulong)r.Next(0, 16));
    }
}
