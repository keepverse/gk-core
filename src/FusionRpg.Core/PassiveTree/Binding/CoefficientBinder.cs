namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>
/// Budget share to stored coefficient (spec-tree-binder.md §3.1-3.4, task B4). One division, at the
/// end. **The plan distributes; this module reads.** Its source contains no `tierWeight`, no
/// `weightTotal`, no `w[t]` — R4 deleted both from the formula: `nodes[].budgetShareMilli` is the
/// plan's own authoritative per-node number, read as given and never recomputed from a tier weight
/// or reconstructed from the width vector.
/// </summary>
public static class CoefficientBinder
{
    public sealed class CoefficientOverflow : Exception
    {
        public CoefficientOverflow(Exception inner) : base("kMicro computation overflowed", inner) { }
    }

    /// <summary>
    /// `kMicro = round_half_away(treeShareMilli · treeBudgetMilli · budgetShareMilli · channelAnchorMilli,
    /// branches · 1_000_000)` — spec-tree-binder.md §3.3. All four numerator inputs are per-mille;
    /// exactly one division, as docs/architecture/numeric-types.md rule 4 requires. Widened to `long` throughout and `checked`
    /// so an overflow throws rather than wraps (rule 5, rule 3).
    /// </summary>
    public static long Bind(long treeShareMilli, long treeBudgetMilli, long budgetShareMilli,
                            long channelAnchorMilli, long branches)
    {
        try
        {
            checked
            {
                var num = treeShareMilli * treeBudgetMilli * budgetShareMilli * channelAnchorMilli;
                var denom = branches * 1_000_000L;
                return RoundHalfAwayFromZero(num, denom);
            }
        }
        catch (OverflowException ex)
        {
            throw new CoefficientOverflow(ex);
        }
    }

    static long RoundHalfAwayFromZero(long numerator, long denominator)
    {
        var q = numerator / denominator;
        var r = numerator % denominator;
        if (r == 0) return q;
        var twiceR = checked(Math.Abs(r) * 2);
        return numerator >= 0
            ? (twiceR >= denominator ? q + 1 : q)
            : (-twiceR >= denominator ? q - 1 : q);
    }
}
