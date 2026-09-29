namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// A validated, compiled predicate. <b>The interface is the contract; the encoding is not.</b>
/// E13 benchmarks candidate encodings against real content and may replace the implementation —
/// the equivalence fuzz in this module is what makes that safe.
/// </summary>
public interface ICompiledPredicate
{
    /// <summary>Evaluate against one event. Allocation-free; no clock, no RNG, no I/O.</summary>
    bool Evaluate(ref FactReader facts);
}

/// <summary>
/// Validates a predicate tree and compiles it.
///
/// <para>This is the single place in the schema that could quietly become a programming language.
/// Everything here is about preventing that: a closed leaf list, hard depth and node limits,
/// no syntax to parse, and rejection — never silent ignoring — of anything unknown.</para>
/// </summary>
public static class PredicateCompiler
{
    public const int MaxDepth = 4;
    public const int MaxNodes = 16;

    /// <summary>An absent predicate is legal and means "always". The common case, and free.</summary>
    public static ICompiledPredicate Always { get; } = new AlwaysNode();

    /// <summary>
    /// Validate and compile. <paramref name="statusBit"/> interns a status id to a bit so the hot
    /// path never compares strings; it returns -1 for an id the runtime does not know, which is a
    /// rejection rather than a silently-false leaf.
    /// </summary>
    public static AtomRejection TryCompile(
        PredicateNode? tree,
        Func<string, int>? statusBit,
        out ICompiledPredicate compiled,
        Func<string, int>? elementId = null,
        Func<string, int>? stockBit = null)
    {
        compiled = Always;
        if (tree is null) return AtomRejection.Ok;

        var nodes = 0;
        var check = Validate(tree, depth: 1, ref nodes);
        if (!check.IsOk) return check;

        // E13's winner (2026-08-22): the flattened non-recursive form, chosen on a cold-cache median
        // over a 200-predicate corpus. The typed graph below stays as the reference implementation the
        // equivalence fuzz checks against, and as a candidate the benchmark can re-run.
        compiled = FlatPredicate.Build(tree, statusBit, elementId, stockBit);
        return AtomRejection.Ok;
    }

    // ---- validation ---------------------------------------------------------------------------

    static AtomRejection Validate(PredicateNode node, int depth, ref int nodes)
    {
        if (depth > MaxDepth)
            return AtomRejection.Fail(AtomRejectionReason.DepthExceeded,
                $"depth {depth} exceeds {MaxDepth}");

        if (++nodes > MaxNodes)
            return AtomRejection.Fail(AtomRejectionReason.NodeCountExceeded,
                $"more than {MaxNodes} nodes");

        switch (node)
        {
            case PredicateNode.And a:
                return ValidateChildren(a.Children, "And", depth, ref nodes);

            case PredicateNode.Or o:
                return ValidateChildren(o.Children, "Or", depth, ref nodes);

            case PredicateNode.Not n:
                // A Not with no child would be silently true or silently false — the whole class of
                // bug this module exists to refuse. The type gives us exactly one child; guard null.
                if (n.Child is null)
                    return AtomRejection.Fail(AtomRejectionReason.EmptyNode, "Not with no child");
                return Validate(n.Child, depth + 1, ref nodes);

            case PredicateNode.Leaf leaf:
                return ValidateLeaf(leaf);

            default:
                return AtomRejection.Fail(AtomRejectionReason.UnknownLeaf,
                    node?.GetType().Name ?? "(null)");
        }
    }

    static AtomRejection ValidateChildren(
        IReadOnlyList<PredicateNode>? children, string what, int depth, ref int nodes)
    {
        // Zero children is distinct from an ABSENT predicate: absent means "always" and is legal,
        // while And() would quietly mean true and Or() quietly mean false.
        if (children is null || children.Count == 0)
            return AtomRejection.Fail(AtomRejectionReason.EmptyNode, $"{what} with no children");

        foreach (var child in children)
        {
            if (child is null)
                return AtomRejection.Fail(AtomRejectionReason.EmptyNode, $"{what} with a null child");

            var r = Validate(child, depth + 1, ref nodes);
            if (!r.IsOk) return r;
        }
        return AtomRejection.Ok;
    }

    static AtomRejection ValidateLeaf(PredicateNode.Leaf leaf)
    {
        if (!Enum.IsDefined(typeof(LeafId), leaf.Id))
            return AtomRejection.Fail(AtomRejectionReason.UnknownLeaf, $"leaf id {(int)leaf.Id}");

        // Subject is an enum with no "unset" member, so an out-of-range value is the only way a
        // caller can have failed to choose one — e.g. deserialising JSON that omitted the key.
        if (!Enum.IsDefined(typeof(Subject), leaf.Subject))
            return AtomRejection.Fail(AtomRejectionReason.AmbiguousSubject,
                $"{leaf.Id} has no subject; every leaf declares one (OnDamageDealt inverts side)");

        return leaf.Id switch
        {
            LeafId.SideIs or LeafId.HasStatus or LeafId.ElementIs when string.IsNullOrWhiteSpace(leaf.Text)
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"{leaf.Id} needs a text arg"),

            LeafId.TypeIdIn when leaf.Values is null || leaf.Values.Count == 0
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue, "typeIdIn needs a non-empty set"),

            LeafId.HpBelowMilli or LeafId.HpAboveMilli when leaf.Value is < 0 or > 1000
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"{leaf.Id} takes per-mille in [0, 1000], got {leaf.Value}"),

            LeafId.HoldsStock when string.IsNullOrWhiteSpace(leaf.Text)
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue, "holdsStock needs a stockId text arg"),

            LeafId.HoldsStock when leaf.Value < 1
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"holdsStock minQty must be >= 1, got {leaf.Value} -- \"do I hold ANY\" is minQty 1, never 0"),

            // D3.7: structural non-negativity only -- an ordinal/count can never be negative, but what
            // it MEANS (which band, which room kind) is a domain registry's own vocabulary this generic
            // compiler never imports, matching TypeIdIs's own "any non-negative ordinal" posture.
            LeafId.BandIs when leaf.Value < 0
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"bandIs takes a non-negative ordinal, got {leaf.Value}"),

            LeafId.HaulAtLeast when leaf.Value < 0
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"haulAtLeast takes a non-negative count, got {leaf.Value}"),

            LeafId.RoomKindIs when leaf.Value < 0
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"roomKindIs takes a non-negative ordinal, got {leaf.Value}"),

            LeafId.PartyDownedCount when leaf.Value < 0
                => AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"partyDownedCount takes a non-negative count, got {leaf.Value}"),

            _ => AtomRejection.Ok,
        };
    }

    // ---- the typed object graph: reference implementation ---------------------------------------
    //
    // Held the crown until E13 measured properly. The earlier scratch benchmark had it at 7 ns against
    // a flat walker's 47 ns — but over six IDENTICAL trees, which flatters whichever form has the
    // tightest inner loop, and against a naive `ref int pc` interpreter rather than a real flattened
    // encoding. Re-run over 200 varied predicates with interleaved measurement, the flat form wins
    // cold by 15-20%.
    //
    // Kept, not deleted: it is what the equivalence fuzz checks the shipped form against, and the
    // benchmark needs a second candidate to have a comparison at all.

    internal static ICompiledPredicate BuildTypedGraph(
        PredicateNode? tree, Func<string, int>? statusBit, Func<string, int>? elementId, Func<string, int>? stockBit = null) =>
        tree is null ? Always : Build(tree, statusBit, elementId, stockBit);

    static ICompiledPredicate Build(
        PredicateNode node, Func<string, int>? statusBit, Func<string, int>? elementId, Func<string, int>? stockBit) => node switch
    {
        PredicateNode.And a => new AndNode(a.Children.Select(c => Build(c, statusBit, elementId, stockBit)).ToArray()),
        PredicateNode.Or o => new OrNode(o.Children.Select(c => Build(c, statusBit, elementId, stockBit)).ToArray()),
        PredicateNode.Not n => new NotNode(Build(n.Child, statusBit, elementId, stockBit)),
        PredicateNode.Leaf l => BuildLeaf(l, statusBit, elementId, stockBit),
        _ => Always,
    };

    static ICompiledPredicate BuildLeaf(
        PredicateNode.Leaf l, Func<string, int>? statusBit, Func<string, int>? elementId, Func<string, int>? stockBit) => l.Id switch
    {
        // Strings are interned here, at compile time, so the hot path compares ints.
        LeafId.SideIs => new SideNode(l.Subject, SideOrdinal(l.Text)),
        // Elements and statuses are DIFFERENT namespaces. An earlier cut interned element names
        // through the status resolver, and the equivalence fuzz hid it by making its reference do
        // the same thing — the test agreed with the code instead of with the spec.
        LeafId.ElementIs => new ElementNode(l.Subject, elementId?.Invoke(l.Text!) ?? -1),
        LeafId.HasStatus => new StatusNode(l.Subject, statusBit?.Invoke(l.Text!) ?? -1),
        LeafId.TypeIdIs => new TypeIdNode(l.Subject, l.Value),
        LeafId.TypeIdIn => new TypeIdInNode(l.Subject, l.Values!.ToArray()),
        LeafId.ActorIsKiller => new KillerNode(l.Subject, l.Value != 0),
        LeafId.HpBelowMilli => new HpBelowNode(l.Subject, l.Value),
        LeafId.HpAboveMilli => new HpAboveNode(l.Subject, l.Value),
        LeafId.RowIs => new RowNode(l.Subject, l.Value),
        LeafId.ColIs => new ColNode(l.Subject, l.Value),
        LeafId.IsMindControlled => new CharmNode(l.Subject, l.Value != 0),
        // stockId interned to a slot (0-3) here, same as HasStatus; minQty stays a plain runtime int.
        LeafId.HoldsStock => new StockNode(l.Subject, stockBit?.Invoke(l.Text!) ?? -1, l.Value),
        // D3.7: plain raw ordinals/counts, no interning needed -- the importer already resolved them.
        LeafId.BandIs => new BandNode(l.Subject, l.Value),
        LeafId.HaulAtLeast => new HaulAtLeastNode(l.Subject, l.Value),
        LeafId.RoomKindIs => new RoomKindNode(l.Subject, l.Value),
        LeafId.PartyDownedCount => new PartyDownedCountNode(l.Subject, l.Value),
        _ => Always,
    };

    internal static int SideOrdinal(string? side) => side?.ToLowerInvariant() switch
    {
        "plant" => 0,
        "zombie" => 1,
        "bullet" => 2,
        _ => -1,
    };

    // ---- nodes --------------------------------------------------------------------------------

    sealed class AlwaysNode : ICompiledPredicate
    {
        public bool Evaluate(ref FactReader f) => true;
    }

    sealed class AndNode : ICompiledPredicate
    {
        readonly ICompiledPredicate[] _children;
        public AndNode(ICompiledPredicate[] children) { _children = children; }

        public bool Evaluate(ref FactReader f)
        {
            // Short-circuits: a false child stops the walk, so the rest read no facts at all.
            foreach (var c in _children)
                if (!c.Evaluate(ref f)) return false;
            return true;
        }
    }

    sealed class OrNode : ICompiledPredicate
    {
        readonly ICompiledPredicate[] _children;
        public OrNode(ICompiledPredicate[] children) { _children = children; }

        public bool Evaluate(ref FactReader f)
        {
            foreach (var c in _children)
                if (c.Evaluate(ref f)) return true;
            return false;
        }
    }

    sealed class NotNode : ICompiledPredicate
    {
        readonly ICompiledPredicate _child;
        public NotNode(ICompiledPredicate child) { _child = child; }
        public bool Evaluate(ref FactReader f) => !_child.Evaluate(ref f);
    }

    sealed class SideNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public SideNode(Subject s, int side) { _s = s; _v = side; }
        public bool Evaluate(ref FactReader f) => f.Side(_s) == _v;
    }

    sealed class TypeIdNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public TypeIdNode(Subject s, int typeId) { _s = s; _v = typeId; }
        public bool Evaluate(ref FactReader f) => f.TypeId(_s) == _v;
    }

    sealed class HpBelowNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public HpBelowNode(Subject s, int milli) { _s = s; _v = milli; }
        public bool Evaluate(ref FactReader f) => f.HpMilli(_s) < _v;
    }

    sealed class HpAboveNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public HpAboveNode(Subject s, int milli) { _s = s; _v = milli; }
        public bool Evaluate(ref FactReader f) => f.HpMilli(_s) > _v;
    }

    sealed class ElementNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public ElementNode(Subject s, int element) { _s = s; _v = element; }
        public bool Evaluate(ref FactReader f) => f.ElementId(_s) == _v;
    }

    sealed class RowNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public RowNode(Subject s, int row) { _s = s; _v = row; }
        public bool Evaluate(ref FactReader f) => f.Row(_s) == _v;
    }

    sealed class ColNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public ColNode(Subject s, int col) { _s = s; _v = col; }
        public bool Evaluate(ref FactReader f) => f.Col(_s) == _v;
    }

    sealed class StatusNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public StatusNode(Subject s, int bit) { _s = s; _v = bit; }
        public bool Evaluate(ref FactReader f) => f.HasStatusBit(_s, _v);
    }

    sealed class StockNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _stockIndex; readonly int _minQty;
        public StockNode(Subject s, int stockIndex, int minQty) { _s = s; _stockIndex = stockIndex; _minQty = minQty; }
        public bool Evaluate(ref FactReader f) => f.StockQty(_s, _stockIndex) >= _minQty;
    }

    sealed class BandNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public BandNode(Subject s, int band) { _s = s; _v = band; }
        public bool Evaluate(ref FactReader f) => f.Band(_s) == _v;
    }

    sealed class HaulAtLeastNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public HaulAtLeastNode(Subject s, int minCells) { _s = s; _v = minCells; }
        public bool Evaluate(ref FactReader f) => f.HaulCount(_s) >= _v;
    }

    sealed class RoomKindNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public RoomKindNode(Subject s, int roomKind) { _s = s; _v = roomKind; }
        public bool Evaluate(ref FactReader f) => f.RoomKind(_s) == _v;
    }

    sealed class PartyDownedCountNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int _v;
        public PartyDownedCountNode(Subject s, int minDowned) { _s = s; _v = minDowned; }
        public bool Evaluate(ref FactReader f) => f.DownedCount(_s) >= _v;
    }

    sealed class KillerNode : ICompiledPredicate
    {
        readonly Subject _s; readonly bool _want;
        public KillerNode(Subject s, bool want) { _s = s; _want = want; }
        public bool Evaluate(ref FactReader f) => f.IsKiller(_s) == _want;
    }

    sealed class CharmNode : ICompiledPredicate
    {
        readonly Subject _s; readonly bool _want;
        public CharmNode(Subject s, bool want) { _s = s; _want = want; }
        public bool Evaluate(ref FactReader f) => f.IsMindControlled(_s) == _want;
    }

    sealed class TypeIdInNode : ICompiledPredicate
    {
        readonly Subject _s; readonly int[] _set;
        public TypeIdInNode(Subject s, int[] set) { _s = s; _set = set; }

        public bool Evaluate(ref FactReader f)
        {
            var v = f.TypeId(_s);
            foreach (var t in _set) if (t == v) return true;
            return false;
        }
    }
}
