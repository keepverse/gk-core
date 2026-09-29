namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>
/// `P0.3` (spec-power-vector.md, "predicates ARE priced — owner decision, 2026-08-27"): the tree
/// composes its leaves' four-factor chains in per-mille integers —
/// <c>And(a,b) = p(a)×p(b)/1000</c>, <c>Or(a,b) = 1000-(1000-p(a))×(1000-p(b))/1000</c>,
/// <c>Not(a) = 1000-p(a)</c>, <c>Leaf → PowerTables.PredicateFrequencyOf</c> (already floored).
///
/// <para><b>Independence is an approximation, and it is declared rather than hidden</b> (the spec's
/// own words): <c>And(hasStatus(cold), hasStatus(freeze))</c> are correlated, so the product
/// understates. Covered by the same instrument the module already uses for multiplicative pairs —
/// the ±25% override tolerance, with <c>power_note</c> recording why.</para>
/// </summary>
public static class PredicatePricer
{
    /// <summary>No predicate (a null tree) prices at 1000‰ — "unconditional" — matching
    /// <c>Conditionality</c>'s own triggerless short-circuit.</summary>
    public static long PriceTree(PredicateNode? tree, PowerTables tables, int floorMilli) =>
        tree is null ? PowerMath.One : PriceNode(tree, tables, floorMilli);

    static long PriceNode(PredicateNode node, PowerTables tables, int floorMilli) => node switch
    {
        PredicateNode.And a => PriceAnd(a.Children, tables, floorMilli),
        PredicateNode.Or o => PriceOr(o.Children, tables, floorMilli),
        PredicateNode.Not n => PowerMath.One - PriceNode(n.Child, tables, floorMilli),
        PredicateNode.Leaf l => tables.PredicateFrequencyOf(l.Id, ArgKeyOf(l), floorMilli),
        _ => PowerMath.One,
    };

    static long PriceAnd(IReadOnlyList<PredicateNode> children, PowerTables tables, int floorMilli)
    {
        var acc = PowerMath.One;
        foreach (var child in children)
            acc = PowerMath.CombineMilli(acc, PriceNode(child, tables, floorMilli));
        return acc;
    }

    static long PriceOr(IReadOnlyList<PredicateNode> children, PowerTables tables, int floorMilli)
    {
        var acc = 0L; // "guaranteed false" -- the fold identity for Or, so the first child passes through unchanged
        foreach (var child in children)
            acc = PowerMath.One - PowerMath.CombineMilli(PowerMath.One - acc, PowerMath.One - PriceNode(child, tables, floorMilli));
        return acc;
    }

    /// <summary>The leaf's own arg key — its text (a status id, an element) when it has one, else its
    /// scalar value as a string (a threshold, a row/col). Never the <see cref="Subject"/>: reachability/
    /// susceptibility/coincidence/uptime are properties of WHAT is being checked, not of self-vs-target.</summary>
    internal static string ArgKeyOf(PredicateNode.Leaf l) => l.Text ?? l.Value.ToString();
}
