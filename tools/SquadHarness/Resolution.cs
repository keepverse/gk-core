namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §9.2 -- "The noise floor is measured, not assumed." The half-width is the
/// normal approximation to a binomial-proportion confidence interval, evaluated at the WORST-CASE
/// Bernoulli variance (p = 0.5) rather than the sample proportion: the harness does not know the true
/// win share in advance, and reporting a narrower interval by plugging in the observed proportion would
/// understate its own uncertainty on exactly the cells closest to a coin flip -- the ones this module
/// cares most about resolving correctly.
///
/// <para><b>Checked against the spec's own worked table</b> (§9.2): <c>HalfWidthMilli(3000)</c> = 1.8pp,
/// <c>HalfWidthMilli(9600)</c> ≈ 1.0pp, <c>HalfWidthMilli(38400)</c> ≈ 0.5pp -- <c>1.96 * sqrt(0.25/n)</c>
/// reproduces all three, and <c>AggregationTests</c> pins them.</para>
///
/// <para><b>Not a balance dial.</b> <see cref="Z95"/> and <see cref="MaxBernoulliVariance"/> are named
/// constants of the normal approximation to a binomial proportion -- moving either would not change how
/// the game feels, it would change what "95% confidence" means. Structural, per AGENTS.md's
/// magic-number test ("would a balance pass ever want to change this?" -- no, it would stop being a 95%
/// interval).</para>
///
/// <para><b>double is deliberate here</b> (spec §7's numeric-types table: "F, H, share are double...
/// reported, non-persisted, non-hashed model value"). A half-width is exactly that class of value: it
/// is never written into <see cref="PairResult"/>/<see cref="HarnessRun"/> (the hashed types), only into
/// the F2 report DTOs that wrap them for JSON. The public surface still returns <c>long</c> per-mille,
/// rounded up (never down -- reporting a narrower interval than was actually achieved is the failure
/// mode this whole module exists to avoid), so a caller who DID want to fold a half-width into a hashed
/// shape could do so without reopening <c>No_double_reaches_the_hash_input</c>.</para>
/// </summary>
public static class Resolution
{
    /// <summary>The two-sided 95% normal quantile. Structural -- the definition of "95%", not a tunable
    /// (AGENTS.md's magic-number test: changing it changes what the number MEANS, not how the game
    /// feels).</summary>
    public const double Z95 = 1.96;

    /// <summary>p(1-p) maximised at p = 0.5 -- the worst-case Bernoulli variance. Used because the
    /// harness does not assume the true win share when sizing its own uncertainty. Structural for the
    /// same reason as <see cref="Z95"/>.</summary>
    public const double MaxBernoulliVariance = 0.25;

    /// <summary>
    /// The 95% half-width, in per-mille, of a win-share estimate decided over <paramref name="decided"/>
    /// trials (victories + defeats -- spec §8 excludes stalemates from the denominator, so a stalemate
    /// never narrows this). Rounds UP (<see cref="Math.Ceiling(double)"/>): a half-width that undersold
    /// its own uncertainty by rounding down would let a gap that does not actually clear the bar report
    /// as resolved.
    /// </summary>
    public static long HalfWidthMilli(long decided)
    {
        // Zero decided trials: nothing was decided, so nothing is resolved -- the maximally wide
        // interval (the full 0..1000 per-mille range) rather than a divide-by-zero.
        if (decided <= 0) return 1000;
        var halfWidth = Z95 * Math.Sqrt(MaxBernoulliVariance / decided);
        return (long)Math.Ceiling(halfWidth * 1000.0);
    }

    /// <summary>
    /// The half-width of a DIFFERENCE (or, equivalently here, a gap being compared for separation)
    /// between two independent estimates is the root-sum-square of their own half-widths, never their
    /// sum (which double-counts) and never the larger alone (which ignores the smaller's own
    /// uncertainty). This is the standard propagation-of-error rule for a linear combination of two
    /// independent estimates, applied to per-mille half-widths.
    /// </summary>
    public static long CombinedHalfWidthMilli(long a, long b) =>
        (long)Math.Ceiling(Math.Sqrt((double)a * a + (double)b * b));

    /// <summary>
    /// spec §9.2: "cells whose confidence interval is still too wide to call a winner." A decided cell
    /// cannot call a winner when its win share's own half-width interval still reaches across the 500‰
    /// (coin-flip) line -- i.e. the gap between the observed share and 500‰ does not exceed the
    /// half-width. This is the per-CELL screening test the <c>--refine</c> pass uses to decide which of
    /// the 8,190/506 pairs earn the extra 40,000 trials (§9.2's two-stage design): a cell that already
    /// resolved cleanly at the screening trial count is never re-run.
    /// </summary>
    public static bool CannotCallWinner(long winShareMilli, long decided) =>
        decided <= 0 || Math.Abs(winShareMilli - 500L) <= HalfWidthMilli(decided);

    /// <summary>
    /// spec §5/§9.2: "transfers is false whenever an ordering rests on a gap inside its own half-width."
    /// Applied to two build-class means (not individual pair cells): the gap between them must EXCEED
    /// their combined half-width to count as a resolved separation -- resting exactly on the boundary is
    /// still inside it (never resolved by a coin-flip rounding rule).
    /// </summary>
    public static bool GapIsInsideHalfWidth(long meanAMilli, long halfWidthAMilli, long meanBMilli, long halfWidthBMilli)
    {
        var gap = Math.Abs(meanAMilli - meanBMilli);
        var combined = CombinedHalfWidthMilli(halfWidthAMilli, halfWidthBMilli);
        return gap <= combined;
    }

    /// <summary>
    /// The exact wording <see cref="TransferReport"/>'s <c>VerdictFor</c> already uses for a gap that
    /// does not clear its own combined half-width, factored out so a second caller (spec-squad-
    /// harness.md §11 S3's Θ≈300 crossover check) reuses the SAME message shape rather than
    /// re-inventing it -- both the "cannot separate" phrase and the gap/half-width numbers it reports
    /// are computed once, here, from <see cref="GapIsInsideHalfWidth"/>'s own inputs.
    /// </summary>
    public static string CannotSeparateMessage(
        string a, long meanAMilli, long halfWidthAMilli,
        string b, long meanBMilli, long halfWidthBMilli, string where)
    {
        var gap = Math.Abs(meanAMilli - meanBMilli);
        var combined = CombinedHalfWidthMilli(halfWidthAMilli, halfWidthBMilli);
        return $"cannot separate {a} and {b} at {where} (gap {gap}pm <= half-width {combined}pm)";
    }
}
