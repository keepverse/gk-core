using System.Numerics;

namespace FusionRpg.Core.Actions.Unlock;

/// <summary>
/// T19 (action-todo.md, spec-unlock-ladder.md §1): the ratchet. Pure functions of
/// <c>earnCount</c> — never a slot, never occupancy (spec §1: "an earlier draft keyed the rung on
/// occupancy; that was retracted, because a slot that remembers its rung freezes an unlucky early
/// roll permanently — a progression ceiling wearing a different hat, which PS-8 refuses").
/// </summary>
public static class UnlockLadder
{
    /// <summary>
    /// <c>chance(n) = max(floor, p1 * delta^n)</c> — <paramref name="earnCount"/> is <c>n</c>, the
    /// count of successful earns SO FAR (0 for the very first roll, matching the spec table's "earn
    /// 1" reading <c>delta^0</c>).
    ///
    /// <para>Rounds ONCE, at the end — the same rule <c>CurveTable</c>'s own curve interpolation
    /// follows (definitions.md §2), and the reason this is not a per-step <c>CurveTable.ApplyMilli</c>
    /// loop: rounding at every one of up to ~50 steps compounds error and gives a wrong answer at
    /// higher earn counts (verified against the spec's own table, not assumed — a per-step version
    /// read earn 50 as 4‰, not the floor). <see cref="BigInteger"/> tracks the exact fraction
    /// <c>p1 × deltaᶦ / 1000ᶦ</c> with no intermediate rounding at all — an exact fraction, which a
    /// <c>double</c> product would only approximate (a precision choice for matching the spec table
    /// exactly, not a floating-point ban; that ban was removed 2026-09-15).</para>
    ///
    /// <para>Terminates in a bounded number of steps for any <paramref name="earnCount"/>, however
    /// large: the sequence is monotonically decreasing (<c>0 &lt; deltaMilli &lt; 1000</c>, enforced
    /// at load), so once the running fraction is provably <c>&lt;= floor</c> it never rises again —
    /// the loop below exits the moment that is true rather than iterating a `long` number of times
    /// or growing the <see cref="BigInteger"/> without bound.</para>
    /// </summary>
    public static int ChanceMilli(long earnCount, UnlockTuning tuning)
    {
        if (earnCount < 0) throw new ArgumentOutOfRangeException(nameof(earnCount), earnCount, "earnCount is never negative");
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        BigInteger numerator = tuning.P1Milli;
        var denominatorPow = BigInteger.One;

        for (long i = 0; i < earnCount; i++)
        {
            numerator *= tuning.DeltaMilli;
            denominatorPow *= 1000;

            // numerator/denominatorPow <= floor, cross-multiplied to avoid a division.
            if (numerator <= (BigInteger)tuning.FloorMilli * denominatorPow)
                return tuning.FloorMilli;
        }

        // Round half away from zero, exactly once: (2n + d) / (2d) for positive n, d.
        var rounded = (int)((numerator * 2 + denominatorPow) / (denominatorPow * 2));
        return Math.Max(rounded, tuning.FloorMilli);
    }

    /// <summary><c>effectiveRung(n) = min(earnCount, rungCap)</c> — the ONLY input is
    /// <paramref name="earnCount"/>. No slot, no held-set position, nothing else: two callers passing
    /// the same <c>earnCount</c> always get the same rung, which is what makes a held unlock's rung
    /// derivable forever from the single number recorded at the moment it was accepted, rather than a
    /// value that must itself be stored (spec's testing strategy: "no column stores a resolved rung
    /// value").
    ///
    /// <para><b>A-U1 (spec-rung-semantics.md §3.1, 2026-09-03):</b> this is the HOLDER-derived rung,
    /// distinctly named and distinctly typed (<see cref="EffectiveRung"/>) from
    /// <see cref="ActionRow.Rung"/>, the AUTHORED value <see cref="StructureBudgetGuard"/> reads.
    /// Clamping this value never reaches that guard — it fixes magnitude and cost for THIS holder,
    /// never structure, which is a property of the content, not of who holds it. Renamed from
    /// <c>Rung</c> so the two cannot silently re-merge into "the rung" the way the specs' own
    /// pre-correction prose did.</para>
    /// <para><b>ST2 (spec-holder-rung-pricing.md contract 1):</b> an optional window bound narrows the
    /// result to <c>min(earnCount, rungCap, window.Ceiling)</c>. The window is a bound on the one
    /// curve, never a second curve. A <c>null</c> window (an action authored before A-E1,
    /// <see cref="ActionRow.RungBand"/>) is unbounded, byte-identical to the two-argument form.
    /// The window's <c>Floor</c> is never read here (contract 3).</para>
    /// </summary>
    public static EffectiveRung EffectiveRung(long earnCount, UnlockTuning tuning)
    {
        if (earnCount < 0) throw new ArgumentOutOfRangeException(nameof(earnCount), earnCount, "earnCount is never negative");
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        return new EffectiveRung((int)Math.Min(earnCount, tuning.RungCap));
    }

    /// <summary><c>effectiveRung(n) = min(earnCount, rungCap, window.Ceiling)</c>, or
    /// <c>min(earnCount, rungCap)</c> when <paramref name="window"/> is <c>null</c>.</summary>
    public static EffectiveRung EffectiveRung(long earnCount, UnlockTuning tuning, RungBand? window)
    {
        if (earnCount < 0) throw new ArgumentOutOfRangeException(nameof(earnCount), earnCount, "earnCount is never negative");
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var ceiling = window is { } w ? Math.Min(tuning.RungCap, w.Ceiling) : tuning.RungCap;
        return new EffectiveRung((int)Math.Min(earnCount, ceiling));
    }
}
