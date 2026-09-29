namespace FusionRpg.Core.Effects;

/// <summary>Pure clamp for FA10 current-HP add. Unity Writer calls this; tests do not need IL2CPP.</summary>
public static class ResourceDeltaMath
{
    public const int MailboxCap = 4096;

    /// <summary>
    /// Overflow ceiling for a single FA10 delta — DERIVED from <see cref="Apply"/>'s own arithmetic
    /// (<c>live + delta</c>), not a round literal (spec-caps-reconcile.md §2.1). Both operands are
    /// independently bounded by this cap via <see cref="ExceedsAmountCap"/>, so the worst-case sum is
    /// <c>2 × AmountCap</c>; keeping that under <c>long.MaxValue</c> is the whole derivation.
    /// <c>long.MaxValue / 2</c> is exact — a compile-time constant, unlike <see cref="Combat.Shield.ShieldMath.MaxInput"/>,
    /// whose coefficients are tuning-loaded and can move.
    /// </summary>
    public const long AmountCap = long.MaxValue / 2;

    /// <summary>True when |amount| exceeds AmountCap. long.MinValue cannot Abs safely.</summary>
    public static bool ExceedsAmountCap(long amount) =>
        amount == long.MinValue || Math.Abs(amount) > AmountCap;

    /// <summary>
    /// live + delta, clamped to [0, max]. Throws — never silently proceeds — if either operand alone
    /// already exceeds <see cref="AmountCap"/> (spec-caps-reconcile.md §2.1: derived bound, throws).
    /// Callers on the guarded Funnel path already pre-check with <see cref="ExceedsAmountCap"/> and
    /// skip the mutation entirely (unchanged by this task); this is the backstop for a caller that
    /// doesn't. max &lt; 0 treated as 0.
    /// </summary>
    public static long Apply(long live, long delta, long max)
    {
        if (ExceedsAmountCap(live))
            throw new ArgumentOutOfRangeException(nameof(live), live, $"exceeds AmountCap ({AmountCap})");
        if (ExceedsAmountCap(delta))
            throw new ArgumentOutOfRangeException(nameof(delta), delta, $"exceeds AmountCap ({AmountCap})");
        if (max < 0) max = 0;
        var next = checked(live + delta);
        if (next > max) next = max;
        if (next < 0) next = 0;
        return next;
    }
}
