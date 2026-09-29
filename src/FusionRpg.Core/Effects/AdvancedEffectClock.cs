namespace FusionRpg.Core.Effects;

/// <summary>
/// `solid-remediation` T4.14 (D15) — a clock a live host ADVANCES, rather than one it reads.
///
/// <para><b>The defect, stated precisely, because the obvious reading of it is wrong.</b> The injector
/// wiring <c>EffectBag.UtcNow = () =&gt; DateTimeOffset.UtcNow</c> is a <i>documented deliberate
/// choice</i> — `EffectBag.UtcNow`'s own error text says a live host wires the real clock "explicitly,
/// on purpose, at its own composition root", and that is correct: the lawn runs in real time and its
/// time has to come from somewhere real. Anyone reading that line as a mistake will delete a correct
/// decision.</para>
///
/// <para><b>What went wrong is narrower.</b> The choice was made for the time <b>source</b> and
/// silently became the <b>scheduler</b> for half the tick vocabulary. Pulse scheduling rides the
/// engine: the injector drives `PulseDotsNow` off a 100 ms grid accumulated from frame delta. Status
/// <i>expiry</i> did not — it compared against whatever `DateTimeOffset.UtcNow` returned at the moment
/// the tick happened. So one tick answered "has a pulse come due?" in engine time and "has this status
/// expired?" in wall time, and the two only agree while nothing perturbs the frame loop.</para>
///
/// <para><b>This keeps the source and replaces the scheduler.</b> The host still takes its time from
/// the real world — it advances this clock by the frame delta it already has — but every status
/// question is then answered against the advanced value, so a pulse and an expiry are computed from
/// one number. A pause, a long frame, or a breakpoint moves both together or neither.</para>
///
/// <para><b>No round trip, by construction.</b> <see cref="UtcNow"/> is a field read. The hot-path
/// budget this module must hold (T4.16) is not at risk from a clock that never leaves the process —
/// which is the design's own answer to the note that if unifying the clock cost a round trip per tick,
/// the design would be wrong.</para>
///
/// <para>Deliberately NOT thread-safe beyond the monotonic guarantee below: the injector advances from
/// its Unity main-thread tick, the same thread the status runtime already runs on.</para>
/// </summary>
public sealed class AdvancedEffectClock : IEffectClock
{
    DateTimeOffset _now;

    public AdvancedEffectClock(DateTimeOffset start) => _now = start;

    // Deliberately NO `StartingNow()` here. A first cut had one, and `WorldDeterminismGuardTests`
    // refused it — correctly: Core is inside the world-simulation purity scan, and a Core type reading
    // `DateTimeOffset.UtcNow` is a wall-clock read in the layer that must not have one, no matter how
    // narrow. The SEED is the host's to supply, which is also the better shape: this type then has no
    // opinion about where time comes from, and a deterministic host passes a fixed start without having
    // to avoid a convenience method that would have been wrong for it.

    public DateTimeOffset UtcNow => _now;

    /// <summary>
    /// Advances by a frame's elapsed time. Negative deltas are ignored rather than rejected: a live
    /// host can hand one over across a pause or a clock adjustment, and a status schedule that ran
    /// BACKWARDS would resurrect expired instances — a far worse failure than a tick that stands
    /// still. Monotonic is the property the schedule actually needs.
    /// </summary>
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        _now = _now.Add(elapsed);
    }

    /// <summary>Frame-delta convenience — seconds, the unit a live host's tick already carries.</summary>
    public void AdvanceSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return;
        Advance(TimeSpan.FromSeconds(seconds));
    }
}
