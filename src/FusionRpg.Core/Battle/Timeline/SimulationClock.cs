namespace FusionRpg.Core.Battle.Timeline;

public enum AdvanceOutcome
{
    Advanced,
    /// <summary>The clock could not move — nothing scheduled, or something external is pending.</summary>
    Blocked
}

public readonly record struct AdvanceResult(AdvanceOutcome Outcome, string? Reason)
{
    public static readonly AdvanceResult Advanced = new(AdvanceOutcome.Advanced, null);
    public static AdvanceResult Blocked(string reason) => new(AdvanceOutcome.Blocked, reason);
}

/// <summary>
/// How simulated time moves. The two discrete-event-simulation mechanisms, and the only thing
/// that distinguishes a turn-based battle from a real-time one.
/// </summary>
public interface ITimeAdvance
{
    /// <summary>Ticks to move, or null when the clock cannot advance.</summary>
    long? NextAdvance(long now, EventQueue queue, long frames);
}

/// <summary>Next-event time advance — the clock jumps straight to the next scheduled event.</summary>
public sealed class NextEventAdvance : ITimeAdvance
{
    public long? NextAdvance(long now, EventQueue queue, long frames)
    {
        var due = queue.PeekDueTick();
        if (due is not { } d) return null;
        return d > now ? d - now : 0;   // never rewind: a past-due event fires at `now`
    }
}

/// <summary>
/// Fixed-increment time advance, carry-corrected. Ticks per frame is a <b>rational</b>
/// (<c>num/den</c>) because the real value usually isn't an integer: at 60 fps a frame is
/// 1000/60 = 16.667 ms, and truncating to 16 loses 40 ms every second — 2.4 seconds a minute.
/// The remainder is carried, so a long run gains and loses exactly nothing.
///
/// <para><b>This instance is STATEFUL and must not be shared between clocks.</b> The carry is
/// per-timeline; two clocks driven from one instance would interleave their remainders and both
/// would drift — silently, since neither would look wrong on its own. One clock, one advance
/// policy. (The structurally safe version binds the policy to the clock at construction; that is
/// a deliberate follow-up rather than a change made mid-review.)</para>
/// </summary>
public sealed class FixedIncrementAdvance : ITimeAdvance
{
    readonly long _num;
    readonly long _den;
    long _carry;

    public FixedIncrementAdvance(long ticksPerFrameNum, long ticksPerFrameDen)
    {
        if (ticksPerFrameNum <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerFrameNum));
        if (ticksPerFrameDen <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerFrameDen));
        _num = ticksPerFrameNum;
        _den = ticksPerFrameDen;
    }

    public long? NextAdvance(long now, EventQueue queue, long frames)
    {
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames), "frames cannot be negative.");
        if (frames == 0) return 0;
        // frames * _num overflows silently at absurd inputs, and the resulting negative delta was
        // being swallowed by the caller's `if (d > 0)` — reporting Advanced with the clock
        // unmoved and the carry left garbage. Refuse rather than drift.
        if (frames > (long.MaxValue - _carry) / _num)
            throw new ArgumentOutOfRangeException(nameof(frames), "frame count overflows the tick clock.");

        var total = frames * _num + _carry;
        var ticks = total / _den;
        _carry = total - ticks * _den;
        return ticks;
    }
}

/// <summary>
/// The simulation clock. Holds integer ticks (1 tick = 1 ms) and nothing else — it cannot read
/// the wall clock, and no floating-point value reaches it.
/// </summary>
public sealed class SimulationClock
{
    public long Now { get; private set; }

    /// <summary>
    /// Attempts to move time forward. Returns <see cref="AdvanceOutcome.Blocked"/> rather than
    /// silently doing nothing, because a jumping clock has no wall-time to dwell in: an
    /// interactive battle must be able to hold while it waits for input instead of leaping past
    /// its own input window.
    /// </summary>
    public AdvanceResult TryAdvance(ITimeAdvance advance, EventQueue queue, long frames = 1)
    {
        if (advance == null) throw new ArgumentNullException(nameof(advance));
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        var delta = advance.NextAdvance(Now, queue, frames);
        if (delta is not { } d)
            return AdvanceResult.Blocked("no scheduled event");

        if (d > 0) Now += d;
        return AdvanceResult.Advanced;
    }
}
