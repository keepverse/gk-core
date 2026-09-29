namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// Delta-driven time advance: the clock follows <b>measured elapsed wall time</b>, offered in whole
/// microseconds, with the sub-millisecond remainder carried.
///
/// <para><b>Why this exists next to <see cref="FixedIncrementAdvance"/>.</b> That policy advances by a
/// fixed ticks-per-frame ratio, which is right for a battle simulated at a nominal rate and wrong for
/// the injector drive. The two injector grids this module replaces both accumulate Unity's
/// <c>unscaledDeltaTime</c> — real measured time — so a nominal-60 drive would run every DoT pulse and
/// shield tick fast or slow by exactly the frame-rate error, on precisely the weak machines the frame
/// budget exists for. See <c>docs/architecture/battle/spec-injector-kernel-drive.md</c> §3.2.</para>
///
/// <para><b>Microseconds, integer from here down.</b> Unity hands the host a <c>float</c> seconds
/// value; the host converts it once, at its own boundary, and everything from here down is integer
/// ticks — <see cref="SimulationClock"/> is an integer-tick clock by design. (This is a design
/// choice, not an enforced floating-point ban: the kernel purity scan no longer bans floating point
/// since the 2026-09-15 owner ruling.)</para>
///
/// <para><b>The <c>frames</c> argument is deliberately ignored</b>, exactly as
/// <see cref="NextEventAdvance"/> ignores it: a frame count means nothing to a policy driven by
/// measured time. Callers offer microseconds via <see cref="Offer"/> and then advance the clock.</para>
///
/// <para><b>Stateful, and must not be shared between clocks</b> — the same warning
/// <see cref="FixedIncrementAdvance"/> carries, for the same reason: two clocks driven from one
/// instance would interleave their remainders and both would drift, neither looking wrong on its
/// own.</para>
/// </summary>
public sealed class DeltaTickAdvance : ITimeAdvance
{
    /// <summary>
    /// Structural, not tunable: 1 tick = 1 ms is locked by <c>decisions.md</c>'s Battle time model
    /// row, and 1000 is the microseconds-per-millisecond conversion, not a balance number.
    /// </summary>
    const long MicrosPerTick = 1000;

    long _pendingMicros;

    /// <summary>Microseconds offered but not yet converted into whole ticks. Never negative.</summary>
    public long PendingMicros => _pendingMicros;

    /// <summary>
    /// Accumulates elapsed real time. Safe to call when the clock is being held back — that is the
    /// whole point: time offered while the drive is catching up is <b>carried</b>, never dropped, so
    /// total simulated time still equals total real time once the backlog clears.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Negative input (a rewinding clock is a bug, not a state to tolerate), or an accumulator
    /// overflow. Overflow throws rather than wrapping, per the repo's magnitude rule.
    /// </exception>
    public void Offer(long elapsedMicros)
    {
        if (elapsedMicros < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMicros), "elapsed time cannot be negative.");
        if (elapsedMicros > long.MaxValue - _pendingMicros)
            throw new ArgumentOutOfRangeException(nameof(elapsedMicros), "elapsed time overflows the microsecond accumulator.");
        _pendingMicros += elapsedMicros;
    }

    /// <summary>
    /// Whole ticks available from the accumulated microseconds, remainder retained.
    ///
    /// <para>Returns 0 rather than null when less than a full tick has accumulated. The distinction
    /// matters: null means <i>the clock cannot move</i> (the interactive-dwell case
    /// <see cref="SimulationClock.TryAdvance"/> reports as <c>Blocked</c>), whereas a partial
    /// millisecond means <i>it has nowhere to move yet</i> — an ordinary frame at 60 fps offers
    /// 16 667 µs and the leftover 667 belongs to the next one.</para>
    /// </summary>
    public long? NextAdvance(long now, EventQueue queue, long frames)
    {
        var ticks = _pendingMicros / MicrosPerTick;
        _pendingMicros -= ticks * MicrosPerTick;
        return ticks;
    }
}
