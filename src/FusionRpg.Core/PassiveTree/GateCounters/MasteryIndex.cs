using FusionRpg.Core.PassiveTree.State;

namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// A gate counter is an ACCUMULATION; a tier gate reads an INDEX. This is the one place the two are
/// converted, and it is the same arithmetic ladder <c>RpgXpCurve</c> already uses to turn accumulated
/// XP into a level (<c>Progression/RpgProgression.cs:80,99</c>) — so the index is the square root of
/// the count.
///
/// <para>That is not a stylistic echo. <c>progression.v3.json</c>'s awards are paid PER EVENT
/// (<c>kill = 12</c>), so a cumulative event count and cumulative XP are the same shape by
/// construction; reusing the shape is what makes one <c>req(t)</c> threshold mean one Θ across every
/// tree category (spec-gate-counters.md §3.2). It is also not a second power ladder — it is row 6's
/// shape reading its own <c>(first, step)</c> pair, which is exactly the precedent
/// <c>ssot-power-scale.md</c> §10 row 26 set for <c>SpeciesXpCurve</c>.</para>
///
/// <para><b>This repo has already paid for getting it wrong once.</b> <c>PointBudget.cs:20-26</c>: an
/// accumulation passed where an index was expected inverted the locked scope ordering by 176× at
/// ordinary play levels. <see cref="Index"/> is this module's <c>CreatureTypeSourceFromLevel</c>.</para>
///
/// <para><b>Naming note.</b> Spec-gate-counters.md §9's code sample names the tuning parameter type
/// <c>GateCounterTuning</c>; task G2 shipped the same record as <see cref="GateCountersTuning"/> (plural
/// "Counters") inside <c>PassiveTree/State/PassiveTreeTuning.cs</c>, alongside the rest of this
/// program's one tunable file. This class reads that shipped type rather than declaring a second,
/// duplicate one — the whole point of task G4 attaching to G2/G3's existing seams instead of building
/// parallel ones.</para>
/// </summary>
public static class MasteryIndex
{
    /// <summary>Cumulative qualifying events needed to REACH <paramref name="index"/> — the triangular
    /// sum of <c>first + (m−1)·step</c>, identical in shape to <c>RpgXpCurve.TotalToReach</c>. `long`
    /// and `checked`: a lifetime counter is a magnitude (docs/architecture/numeric-types.md), so a sum that would overflow
    /// throws rather than wrapping a player's progress back to zero.</summary>
    public static long CountToReach(long index, GateCountersTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (index <= 1) return 0;
        var n = index - 1;
        // n·(2·first + (n−1)·step) is always even -- if n is odd then (n−1) is even so (n−1)·step is
        // even, and 2·first is even regardless -- so the halving is exact and there is no rounding
        // decision to get wrong, for any (first, step) pair, not only the shipped c=23.
        //
        // Found by this module's own long.MaxValue stress test, corrected from the spec's literal
        // "n * (...) / 2" sample: multiplying the FULL product before halving can overflow even when
        // the true (halved) result fits in `long` -- for a count near long.MaxValue the unshifted
        // product is close to DOUBLE the final answer. Halving whichever factor is even FIRST (the
        // same technique <see cref="Reached"/> already uses for its own division-based comparison)
        // keeps every intermediate at or below the final magnitude, never above it.
        checked
        {
            var q = 2 * tuning.MasteryCurveFirstCount + (n - 1) * tuning.MasteryCurveStepCount;
            return (q & 1) == 0 ? n * (q / 2) : (n / 2) * q;
        }
    }

    /// <summary>
    /// The index a raw count has reached. <b>Integer binary search, never a standard-library
    /// square-root call</b> — <c>spec-tree-resolve.md</c> §3.1 ruled that a fractional-precision type
    /// has no place on a gate that decides whether content exists, and the quantity the gate is
    /// measured against gets the same rule. About 63 comparisons, exact at every magnitude.
    ///
    /// <para><b>No cap.</b> The upper bound is found by doubling, not declared — a constant ceiling
    /// here would be a progression cap (AGENTS.md). <see cref="Reached"/> compares by DIVISION rather
    /// than multiplying, so the search itself can never overflow even when the count approaches
    /// <c>long.MaxValue</c>.</para>
    /// </summary>
    public static long Index(long count, GateCountersTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "a gate counter cannot be negative");

        long lo = 1, hi = 2;
        while (Reached(hi, count, tuning))
        {
            lo = hi;
            if (hi > long.MaxValue / 2) { hi = long.MaxValue; break; }
            hi *= 2;
        }
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (Reached(mid, count, tuning)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>`CountToReach(index) &lt;= count`, decided WITHOUT multiplying. For non-negative
    /// integers `a·b &lt;= k` is exactly `b &lt;= k / a` under floor division, so the comparison is
    /// exact and the search never has to evaluate a product that would overflow before it
    /// terminates.</summary>
    static bool Reached(long index, long count, GateCountersTuning tuning)
    {
        if (index <= 1) return true;
        var n = index - 1;
        long q;
        checked { q = 2 * tuning.MasteryCurveFirstCount + (n - 1) * tuning.MasteryCurveStepCount; }
        // Put the /2 on whichever factor is even, so the halved product is still exact.
        var (a, b) = (q & 1) == 0 ? (n, q / 2) : (n / 2, q);
        return a == 0 || b <= count / a;
    }

    /// <summary>Aptitude-point-EQUIVALENTS — the only unit `tree-resolve` ever sees (spec §5.2).
    /// <c>index − 1</c>, never <c>index</c>, mirroring <c>PointBudget.CreatureTypeSourceFromLevel</c>
    /// (<c>:40</c>) and for a sharper version of its reason: index 1 is what every existing save
    /// carries on day one, and a non-zero value there would open tier 1 on all 27 trees for free.
    /// Both operands are already `long`, so the multiply is widened before it happens rather than
    /// after (AGENTS.md rule 3).</summary>
    public static long Equivalents(long count, long ratePoints, GateCountersTuning tuning)
    {
        checked { return (Index(count, tuning) - 1) * ratePoints; }
    }
}
