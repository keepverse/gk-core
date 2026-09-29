namespace FusionRpg.Core.PassiveTree.Resolve;

/// <summary>
/// D8/D39's concentration index (spec-tree-resolve.md §5.1-5.2, task B6). `H` reads the FINAL
/// ALLOCATION the actor holds — self-spent node counts and soul levels, never the sequence they
/// were bought in — so two players following the same build guide end with the same `F`, in any
/// order. Empty denominators read zero, never a uniform default (`Σn = 0` gives `H_nodes = 0`, not
/// `1/n`) — the same rule `AptitudeAllocation` already states for itself.
///
/// <para>Every quantity here is per-mille INTEGER arithmetic, never `float`/`double` — `F` multiplies
/// every tree-derived contribution downstream, so it carries the same overflow/precision discipline
/// as every other magnitude-adjacent number in this program.</para>
/// </summary>
public static class Concentration
{
    /// <summary>`round_half_away(Σ(n_i²)·1000, (Σn)²)` — the Herfindahl index over per-tree node
    /// counts, in per-mille. Zero when `Σn = 0` (no `1/n` fallback).</summary>
    public static long HerfindahlMilli(IReadOnlyList<long> perGroupCounts)
    {
        long sum = 0, sumSquares = 0;
        foreach (var n in perGroupCounts)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(perGroupCounts), n, "counts must be >= 0");
            checked { sum += n; sumSquares += n * n; }
        }
        if (sum == 0) return 0;
        checked { return RoundHalfAwayFromZero(sumSquares * 1000, sum * sum); }
    }

    /// <summary>`H = w·H_nodes + (1-w)·H_souls`, all per-mille integers — `w` itself is
    /// `concentration.wMilli`, so `H_milli = (wMilli·hNodesMilli + (1000-wMilli)·hSoulsMilli) / 1000`.
    /// </summary>
    public static long BlendMilli(long hNodesMilli, long hSoulsMilli, long wMilli)
    {
        if (wMilli is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(wMilli), wMilli, "must be 0..1000");
        checked
        {
            var num = wMilli * hNodesMilli + (1000 - wMilli) * hSoulsMilli;
            return RoundHalfAwayFromZero(num, 1000);
        }
    }

    /// <summary>`F = 1 + (Fmax - 1)·H`, in per-mille — `Fmilli = 1000 + (fmaxMilli - 1000)·hMilli/1000`.
    /// Provably in `[1000, fmaxMilli]` for any `hMilli` in `[0, 1000]`, since `H` is a convex
    /// combination of two Herfindahl indices each in `[0, 1]`.</summary>
    public static long FmaxAppliedMilli(long hMilli, long fmaxMilli)
    {
        if (hMilli is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(hMilli), hMilli, "must be 0..1000");
        checked
        {
            var delta = fmaxMilli - 1000;
            return 1000 + RoundHalfAwayFromZero(delta * hMilli, 1000);
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
