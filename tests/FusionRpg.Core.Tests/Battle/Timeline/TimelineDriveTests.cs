using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// T13 / B25 — the per-frame drive: delta-driven clock, bounded resumable drain, and backpressure.
/// Spec: <c>docs/architecture/battle/spec-injector-kernel-drive.md</c> §3.
///
/// <para>Every budget test drives an <b>injected</b> timestamp. A wall-clock assertion in CI measures
/// the build agent's mood, and this suite has to be able to say "the budget was exceeded" without
/// waiting for it to actually be exceeded.</para>
/// </summary>
public class TimelineDriveTests
{
    /// <summary>
    /// A monotonic fake stopwatch: every read advances by <c>step</c> ticks.
    ///
    /// <para>It must keep moving across frames, not replay a fixed script. A first draft returned a
    /// scripted sequence and then held its last value — which made frame 2's <c>start</c> and its
    /// budget check read the <i>same</i> timestamp, so the budget silently never tripped again and
    /// the whole backlog drained in one frame. The test passed a bug through by being wrong about
    /// time, which is the failure this suite is supposed to catch.</para>
    /// </summary>
    sealed class SteppingClock
    {
        readonly long _step;
        long _t;
        public SteppingClock(long step) => _step = step;
        public long Next() => _t += _step;
    }

    static (TimelineDrive drive, SimulationClock clock, EventQueue queue, List<ScheduledEvent> fired)
        Build(Func<long>? timestamp = null)
    {
        var clock = new SimulationClock();
        var queue = new EventQueue();
        var fired = new List<ScheduledEvent>();
        var drive = new TimelineDrive(clock, queue, e => fired.Add(e), timestamp ?? (() => 0L));
        return (drive, clock, queue, fired);
    }

    // ---- the clock: measured real time, carried ----

    [Fact]
    public void A_partial_millisecond_does_not_move_the_clock_but_is_not_lost()
    {
        var (drive, clock, _, _) = Build();

        drive.Tick(600, budgetTicks: long.MaxValue);     // 0.6 ms
        Assert.Equal(0, clock.Now);

        drive.Tick(600, budgetTicks: long.MaxValue);     // 1.2 ms total
        Assert.Equal(1, clock.Now);
        Assert.Equal(200, drive.PendingMicros);
    }

    /// <summary>
    /// The drift class <c>FixedIncrementAdvance</c>'s own comment names: truncating 1000/60 to 16
    /// loses 2.4 s a minute. Ten thousand irregular frames must lose and gain exactly nothing.
    /// </summary>
    [Fact]
    public void Total_simulated_time_equals_total_real_time_over_ten_thousand_irregular_frames()
    {
        var (drive, clock, _, _) = Build();
        long offered = 0;

        // Deliberately not a round number and not constant — 16667 µs is the 60 fps frame that
        // truncation gets wrong, and the wobble stops the remainder settling into a lucky cycle.
        for (var i = 0; i < 10_000; i++)
        {
            var micros = 16_667 + i % 7;
            offered += micros;
            drive.Tick(micros, budgetTicks: long.MaxValue);
        }

        Assert.Equal(offered / 1000, clock.Now);
        Assert.Equal(offered % 1000, drive.PendingMicros);
    }

    [Fact]
    public void Negative_elapsed_time_is_refused_rather_than_rewinding_the_clock()
    {
        var (drive, _, _, _) = Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => drive.Tick(-1, budgetTicks: long.MaxValue));
    }

    [Fact]
    public void The_microsecond_accumulator_throws_on_overflow_rather_than_wrapping()
    {
        var advance = new DeltaTickAdvance();
        advance.Offer(long.MaxValue - 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => advance.Offer(10));
    }

    // ---- the drain: bounded, resumable, ordered ----

    [Fact]
    public void Due_events_fire_in_due_tick_then_seq_order()
    {
        var (drive, _, queue, fired) = Build();
        queue.Schedule(3, "c", kind: 0, tag: 3);
        queue.Schedule(1, "a", kind: 0, tag: 1);
        queue.Schedule(2, "b", kind: 0, tag: 2);

        drive.Tick(10_000, budgetTicks: long.MaxValue);

        Assert.Equal(new long[] { 1, 2, 3 }, fired.Select(e => e.Tag).ToArray());
    }

    /// <summary>
    /// The B25 acceptance line: an oversized backlog drains across frames, in unchanged order, with
    /// nothing dropped. The budget is exhausted immediately on every frame, so each one dispatches
    /// exactly the starvation-guard minimum of one.
    /// </summary>
    [Fact]
    public void A_budget_exhausted_drain_resumes_next_frame_with_the_same_set_in_the_same_order()
    {
        var exhausted = new SteppingClock(10);            // every read is 10 ticks past the last
        var (drive, _, queue, fired) = Build(exhausted.Next);
        for (var i = 1; i <= 5; i++) queue.Schedule(i, "a", kind: 0, tag: i);

        var frames = 0;
        while (drive.Backlogged || fired.Count < 5)
        {
            drive.Tick(10_000, budgetTicks: 1);
            if (++frames > 50) break;                     // never spin forever, even on a bug
        }

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, fired.Select(e => e.Tag).ToArray());
        Assert.True(frames >= 5, $"expected the drain to spread over at least 5 frames, took {frames}");
    }

    /// <summary>
    /// The starvation guard, proven by making it matter: with the budget already blown before the
    /// first dispatch, a naive `if over budget: return` would dispatch nothing, forever.
    /// </summary>
    [Fact]
    public void One_event_costing_more_than_the_whole_budget_still_fires()
    {
        var blown = new SteppingClock(long.MaxValue / 4);
        var (drive, _, queue, fired) = Build(blown.Next);
        queue.Schedule(1, "a", kind: 0, tag: 7);

        drive.Tick(10_000, budgetTicks: 1);

        Assert.Single(fired);
        Assert.Equal(7, fired[0].Tag);
    }

    [Fact]
    public void Nothing_is_dropped_when_the_pop_bound_is_exceeded()
    {
        var (drive, _, queue, fired) = Build();
        // 600 > MaxPopPerPass (256): the pop bound, not the time budget, is what defers here.
        for (var i = 1; i <= 600; i++) queue.Schedule(1, "a", kind: 0, tag: i);

        var frames = 0;
        while ((drive.Backlogged || fired.Count < 600) && ++frames <= 20)
            drive.Tick(10_000, budgetTicks: long.MaxValue);

        Assert.Equal(600, fired.Count);
        Assert.Equal(Enumerable.Range(1, 600).Select(i => (long)i).ToArray(), fired.Select(e => e.Tag).ToArray());
    }

    // ---- backpressure ----

    /// <summary>
    /// The rule this drive adds over the event pipeline: while work is owed, the clock is held, so a
    /// backlog cannot grow without bound. The held time is still accumulated — pacing, not drift.
    /// </summary>
    [Fact]
    public void The_clock_is_held_while_a_backlog_exists_and_the_held_time_is_still_accumulated()
    {
        var exhausted = new SteppingClock(10);
        var (drive, clock, queue, _) = Build(exhausted.Next);
        queue.Schedule(1, "a", kind: 0, tag: 1);
        queue.Schedule(1, "b", kind: 0, tag: 2);
        queue.Schedule(1, "c", kind: 0, tag: 3);

        var first = drive.Tick(10_000, budgetTicks: 1);   // 10 ms offered, one event dispatched
        Assert.Equal(10, clock.Now);
        Assert.True(first.Deferred);

        var second = drive.Tick(10_000, budgetTicks: 1);  // 10 ms more, clock must NOT move
        Assert.True(second.ClockHeld);
        Assert.Equal(10, clock.Now);

        // …and the held frame's time was banked, not discarded: once the backlog clears, the clock
        // catches up by exactly the amount that was offered while it was held.
        var frames = 0;
        while (drive.Backlogged && ++frames <= 20) drive.Tick(0, budgetTicks: long.MaxValue);
        drive.Tick(0, budgetTicks: long.MaxValue);
        Assert.Equal(20, clock.Now);
    }

    [Fact]
    public void An_empty_queue_leaves_the_drive_idle_and_not_backlogged()
    {
        var (drive, clock, _, fired) = Build();

        var stats = drive.Tick(16_667, budgetTicks: long.MaxValue);

        Assert.Equal(0, stats.Processed);
        Assert.False(stats.Deferred);
        Assert.False(drive.Backlogged);
        Assert.Equal(16, clock.Now);
        Assert.Empty(fired);
    }

    // ---- the allocation contract ----

    /// <summary>
    /// <c>spec-kernel-performance.md</c>'s central property: a warmed steady-state frame allocates
    /// zero bytes. Measured with the same instrument the existing kernel allocation suite uses.
    /// </summary>
    [Fact]
    public void A_warmed_steady_state_drive_frame_allocates_zero_bytes()
    {
        var clock = new SimulationClock();
        var queue = new EventQueue(expectedEvents: 64);
        var sink = 0L;
        Action<ScheduledEvent> handler = e => sink += e.Tag;
        var drive = new TimelineDrive(clock, queue, handler, () => 0L);

        // Warm up: JIT every path, and let both the queue's heap and the drive's scratch reach
        // their steady size before measuring.
        for (var i = 0; i < 200; i++)
        {
            queue.Schedule(clock.Now + 1, "a", kind: 0, tag: i);
            drive.Tick(16_667, budgetTicks: long.MaxValue);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            queue.Schedule(clock.Now + 1, "a", kind: 0, tag: i);
            drive.Tick(16_667, budgetTicks: long.MaxValue);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(sink != 0, "the handler must actually have run — otherwise zero bytes is trivially true");
    }
}
