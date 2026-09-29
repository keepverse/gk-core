namespace FusionRpg.Core.PassiveTree.State;

/// <summary>
/// D25/D36's rising unlock cost (spec-tree-state.md §2, task B5). The Nth node an actor owns costs
/// `first + (N-1)*step`; the cumulative cost of owning N nodes is `N*first + step*N*(N-1)/2`.
/// **Store the owned node SET; derive both budget and spend on read.** There is no stored balance,
/// which is what makes a respec a set-clear (nothing to refund) and re-buying the same build cost
/// exactly what it cost before (`cumulative` is a pure function of the count, restarting at 0).
///
/// <para><b>The order-independence lemma is the property that makes derive-on-read safe</b>:
/// `cumulative(N)` depends only on `N`, so the cost of a SET of N nodes is independent of the order
/// they were bought in — `Σ(first + (i-1)·step)` has no order term. Without it, derive-on-read would
/// disagree with pay-as-you-go and the store would have to remember a price after all.</para>
/// </summary>
public static class TreeUnlockCost
{
    /// <summary>The price of the Nth node (1-indexed) — `first + (N-1)*step`.</summary>
    public static long PriceOfNth(long n, long first, long step)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), n, "node ordinal must be >= 1");
        checked { return first + (n - 1) * step; }
    }

    /// <summary>The total cost of owning `count` nodes — `count*first + step*count*(count-1)/2`.
    /// Widened `long` throughout, `checked` so an overflow throws rather than wraps (docs/architecture/numeric-types.md rule
    /// 5) — at the full 35,280-node corpus (D51, 2026-09-06: 24 statuses, not 21 — was 35,160) the
    /// cumulative reaches ~1.24e9, comfortably inside `long` but the type this codebase's own
    /// overflow table names as "the default for everything here" (spec-tree-state.md §7).</summary>
    public static long Cumulative(long count, long first, long step)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "count must be >= 0");
        checked { return count * first + step * count * (count - 1) / 2; }
    }

    /// <summary>`budget - spend`, both `long` (spec-tree-state.md §7: comparing a `long` budget
    /// against an `int` price is a narrowing defect once the budget itself is `long`-sized at high
    /// `Θ`). Never clamped to zero here — a caller that has somehow overspent sees a negative
    /// available and decides what that means; this function only subtracts.</summary>
    public static long Available(long budget, long ownedCount, long first, long step)
    {
        checked { return budget - Cumulative(ownedCount, first, step); }
    }
}
