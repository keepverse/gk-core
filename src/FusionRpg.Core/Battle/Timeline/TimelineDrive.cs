namespace FusionRpg.Core.Battle.Timeline;

/// <summary>What one <see cref="TimelineDrive.Tick"/> did. A struct — the drive path allocates nothing.</summary>
public struct DriveStats
{
    /// <summary>Events dispatched this frame.</summary>
    public int Processed;

    /// <summary>
    /// True when work is still outstanding at the end of the frame — either buffered events not yet
    /// dispatched, or events still due at the current tick. The next frame resumes them and holds the
    /// clock until they clear.
    /// </summary>
    public bool Deferred;

    /// <summary>
    /// True when the clock was held back this frame because of a backlog. Offered time was still
    /// accumulated, so nothing is lost — this is pacing, not drift.
    /// </summary>
    public bool ClockHeld;
}

/// <summary>
/// The per-frame drive: advance the clock by measured real time, dispatch due events under a work
/// budget, and resume next frame. Spec:
/// <c>docs/architecture/battle/spec-injector-kernel-drive.md</c> §3.
///
/// <para><b>This type lives in Core on purpose.</b> CI runs ten test projects and never builds
/// <c>gk-fusion/src/FusionRpg.Injector</c> (<c>.github/workflows/ci.yml</c>), so drive logic placed injector-side
/// would be untested forever. The injector keeps only the adapter: Unity's <c>float</c> delta becomes
/// whole microseconds at that boundary, and nothing else.</para>
///
/// <para><b>The budget follows <c>EventDrain</c> rather than reinventing one</b> — timestamp ticks, an
/// injected time source so no test ever reads a real clock, and the same starvation guard: at least
/// one event always dispatches, so a single event costing more than the whole budget cannot wedge the
/// drive into dispatching nothing forever.</para>
///
/// <para><b>Backpressure is the rule that is new here.</b> The event pipeline may let a backlog grow
/// and drop droppable kinds; this drive may not, because a dropped shield expiry or DoT pulse is a
/// correctness bug rather than a lost telemetry row. Instead <b>the clock does not advance while a
/// backlog exists</b> — simulated time slows, ordering is untouched, and nothing is discarded. Offered
/// real time is still accumulated while the clock is held, so total simulated time equals total real
/// time once the backlog clears.</para>
///
/// <para><b>Single-threaded</b>, like everything else in this module.</para>
/// </summary>
public sealed class TimelineDrive
{
    /// <summary>
    /// Structural per-frame cap, not a balance number and not a progression ceiling — the class
    /// <c>tunables-ssot.md</c> §1 lists as "per-frame/runtime caps", exempt and required to say so.
    /// It bounds the scratch buffer so a long hitch cannot make the drive allocate; the events it
    /// declines to pop are deferred to the next frame in unchanged order, never dropped.
    /// </summary>
    const int MaxPopPerPass = 256;

    readonly SimulationClock _clock;
    readonly EventQueue _queue;
    readonly Action<ScheduledEvent> _handler;
    readonly Func<long> _timestamp;
    readonly DeltaTickAdvance _advance = new();
    readonly List<ScheduledEvent> _due;
    int _cursor;

    /// <param name="timestamp">
    /// Monotonic time source in <c>Stopwatch</c> ticks, measuring how long dispatch is taking.
    ///
    /// <para><b>Required, with no convenient default</b> — and that is a purity constraint, not a
    /// style choice. A <c>?? Stopwatch.GetTimestamp</c> fallback here puts a wall-clock reference
    /// inside this directory, which <c>TimelinePurityGuardTests</c> rejects with no file exempt.
    /// The host names its own clock (the injector passes <c>Stopwatch.GetTimestamp</c>; tests pass a
    /// stepper), which also means no test can accidentally read a real clock and measure the build
    /// agent instead of the code.</para>
    ///
    /// <para>This is the only wall-clock input the drive has, and it is used solely to decide when to
    /// stop dispatching. Simulated time comes exclusively from <paramref name="clock"/>, so the
    /// budget can never influence <i>what</i> fires or in what order — only how much of it fits in
    /// this frame.</para>
    /// </param>
    public TimelineDrive(
        SimulationClock clock,
        EventQueue queue,
        Action<ScheduledEvent> handler,
        Func<long> timestamp)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
        _due = new List<ScheduledEvent>(MaxPopPerPass);
    }

    /// <summary>Real time offered but not yet turned into ticks — exposed for the drift assertion.</summary>
    public long PendingMicros => _advance.PendingMicros;

    /// <summary>Events buffered but not yet dispatched.</summary>
    public int CarryCount => _due.Count - _cursor;

    /// <summary>True while anything is still owed at the current tick.</summary>
    public bool Backlogged =>
        _cursor < _due.Count || (_queue.PeekDueTick() is { } d && d <= _clock.Now);

    /// <summary>
    /// One frame of drive.
    /// </summary>
    /// <param name="elapsedMicros">Measured unscaled real time since the last call, in microseconds.</param>
    /// <param name="budgetTicks">
    /// Dispatch budget in <c>Stopwatch</c> ticks. A per-frame runtime cap (structural, see
    /// <see cref="MaxPopPerPass"/>); the caller sizes it from the kernel's own frame share.
    /// </param>
    public DriveStats Tick(long elapsedMicros, long budgetTicks)
    {
        // Offered FIRST and unconditionally. Returning early on a backlog without accumulating would
        // silently discard that frame's real time, and the drift would be invisible: every individual
        // frame would look correct while the run as a whole ran slow.
        _advance.Offer(elapsedMicros);

        var start = _timestamp();
        var stats = default(DriveStats);

        // Clear whatever is already owed at the current tick before letting time move again.
        Drain(budgetTicks, start, ref stats);

        if (Backlogged)
        {
            stats.ClockHeld = true;
            stats.Deferred = true;
            return stats;
        }

        _clock.TryAdvance(_advance, _queue);
        Drain(budgetTicks, start, ref stats);

        stats.Deferred = Backlogged;
        return stats;
    }

    void Drain(long budgetTicks, long start, ref DriveStats stats)
    {
        while (true)
        {
            if (_cursor >= _due.Count)
            {
                _due.Clear();
                _cursor = 0;
                if (_queue.PopDue(_clock.Now, _due, MaxPopPerPass) == 0) return;
            }

            while (_cursor < _due.Count)
            {
                // `Processed > 0` is the starvation guard, not an optimisation: without it, an event
                // whose handler costs more than the whole budget would be re-deferred every frame and
                // never run at all.
                if (stats.Processed > 0 && _timestamp() - start >= budgetTicks) return;
                _handler(_due[_cursor++]);
                stats.Processed++;
            }
        }
    }
}
