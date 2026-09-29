using System.Diagnostics;
using FusionRpg.Core.Battle.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// P2 — the measurement gate in front of the injector drive (T13).
///
/// The kernel is going to tick inside the Unity frame, where its failure mode is stutter that no
/// unit test sees. This harness shapes a 200-entity, 60 fps stress board offline and measures what
/// the kernel costs per frame. **If it cannot hold its slice here it will not hold it in the
/// frame** — and learning that costs a harness rather than an injector integration.
///
/// Budgets inherited from perf-probe-plan.md §0: the injector gets ≤2 ms/frame at 200+ entities
/// with no gen2 GC during a level, and the kernel's share of that is ≤0.15 ms/frame.
///
/// On timing assertions: the pass/fail gate is <b>allocation</b>, which is deterministic. Wall
/// clock is reported for the record but asserted only against a deliberately loose ceiling —
/// a tight timing assertion in CI measures the build agent, and the first flake gets it muted.
/// </summary>
public class KernelStressHarnessTests
{
    const int Entities = 200;
    const int Frames = 600;              // 10 seconds at 60 fps
    const double KernelSliceMs = 0.15;   // the kernel's share of the injector budget

    readonly ITestOutputHelper _out;
    public KernelStressHarnessTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// One frame of a stress board: advance the clock, drain what is due, and re-arm each actor
    /// that acted. That is the real shape — a scheduler's per-frame cost is dominated by drain
    /// plus re-arm, not by scheduling in bulk.
    /// </summary>
    readonly record struct HarnessResult(
        double TotalMs, long Bytes, int Drained, long Transitions, long SlotFailures, long Reschedules);

    static HarnessResult RunFrames(int frames, bool measureAllocation)
    {
        var q = new EventQueue(expectedEvents: Entities * 2);
        var clock = new SimulationClock();
        var advance = new FixedIncrementAdvance(1000, 60);
        var buffer = new List<ScheduledEvent>(Entities * 2);

        // The full per-frame path, not just the queue. An earlier version measured only clock +
        // queue and reported the result as "the kernel's cost", which understated it: a real
        // frame also drives each acting actor through its state machine and the slot pool.
        var machines = new ActorTurnMachine[Entities];
        var keys = new string[Entities];
        for (var i = 0; i < Entities; i++)
        {
            keys[i] = "actor:" + i;             // hoisted: building keys per frame would allocate
            machines[i] = new ActorTurnMachine(keys[i]);
        }

        // Width 4, not 200: acquiring and releasing inside one call means the width never binds and
        // the contention branch is dead code. A narrow pool makes TryAcquire actually fail, which
        // is the path a stress board exercises and the one worth measuring.
        var slots = new ActionSlots(4);
        var transitions = 0L;
        var slotFailures = 0L;

        void DriveActor(long tag)
        {
            var m = machines[tag];

            // An actor still holding a slot from a previous frame finishes its action first, so
            // slots are genuinely held ACROSS frames rather than acquired and dropped in place.
            if (m.State == TurnState.Committed)
            {
                m.TransitionTo(TurnState.Resolving);
                m.TransitionTo(TurnState.Recovering);
                slots.Release(keys[tag]);
                m.TransitionTo(TurnState.Charging);
                transitions += 4;
                return;
            }

            if (m.State != TurnState.Charging) return;
            m.TransitionTo(TurnState.Ready);
            transitions++;
            if (!slots.TryAcquire(keys[tag], "squad"))
            {
                slotFailures++;
                m.TransitionTo(TurnState.Charging);   // no slot: pass, do not block the clock
                transitions++;
                return;
            }

            m.TransitionTo(TurnState.Committed);
            transitions++;
        }

        // Arm every actor with a staggered first event.
        for (var i = 0; i < Entities; i++)
            q.Schedule(10 + (i * 7 % 900), "actor", 0, i);

        // Warm up JIT and let the collections settle before measuring anything.
        for (var w = 0; w < 60; w++)
        {
            clock.TryAdvance(advance, q, frames: 1);
            buffer.Clear();
            q.PopDue(clock.Now, buffer);
            foreach (var e in buffer)
            {
                DriveActor(e.Tag);
                q.Schedule(clock.Now + 200 + (e.Tag * 13 % 700), "actor", 0, e.Tag);
            }
        }

        // The instrument is allocated BEFORE the snapshot. Creating it after would charge its own
        // 40 bytes to the kernel — which is precisely what the first run of this harness reported,
        // and a useful demonstration that the gate is sensitive down to a single small object.
        // Both scratch buffers live ABOVE the snapshot. `live` is 200 EventHandles ≈ 3.2 KB, and
        // allocating it inside the window charged the kernel for the harness's own bookkeeping —
        // the same mistake as the Stopwatch, caught the same way.
        var sw = new Stopwatch();
        var live = new EventHandle[Entities];

        long before = 0;
        if (measureAllocation)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            before = GC.GetAllocatedBytesForCurrentThread();
        }

        var drained = 0;
        var reschedules = 0L;
        sw.Restart();
        for (var f = 0; f < frames; f++)
        {
            clock.TryAdvance(advance, q, frames: 1);
            buffer.Clear();
            drained += q.PopDue(clock.Now, buffer);

            // Drive the actor through its turn and re-arm it, as a live board would.
            for (var i = 0; i < buffer.Count; i++)
            {
                var tag = buffer[i].Tag;
                DriveActor(tag);
                live[tag] = q.Schedule(clock.Now + 200 + (tag * 13 % 700), "actor", 0, tag);
            }

            // Re-time a slice of the board every frame. Haste, slow, and delay all do this, and it
            // is the operation most likely to run per-frame at scale — so a harness that certifies
            // the frame slice without it is certifying the wrong loop.
            //
            // The new time must sit on the SAME horizon the actor was armed on. An earlier version
            // pushed every touched event further out each frame, which starved the drain to
            // 1.1 events/frame and quietly turned this into a reschedule-only benchmark.
            for (var i = f % 20; i < Entities; i += 20)
            {
                if (q.Reschedule(live[i], clock.Now + 180 + (i * 11 % 700))) reschedules++;
            }
        }

        sw.Stop();
        var bytes = measureAllocation ? GC.GetAllocatedBytesForCurrentThread() - before : 0;
        return new HarnessResult(sw.Elapsed.TotalMilliseconds, bytes, drained, transitions, slotFailures, reschedules);
    }

    [Fact]
    public void A_two_hundred_entity_board_costs_nothing_per_frame_in_allocation()
    {
        // THE GATE. Allocation is the deterministic half and the one that actually causes stutter:
        // "no gen2 GC during a level" is already a stated non-goal in the perf plan.
        var r = RunFrames(Frames, measureAllocation: true);

        _out.WriteLine(
            $"{Entities} entities, {Frames} frames: {r.Drained} events ({(double)r.Drained / Frames:F1}/frame), " +
            $"{r.Reschedules} reschedules, {r.Transitions} transitions, {r.SlotFailures} slot denials, " +
            $"{r.Bytes} bytes");

        // Liveness first: a zero-byte result means nothing if the work stopped happening. Deleting
        // DriveActor from the loop entirely used to leave this test green.
        Assert.True(r.Drained > 0, "harness drained nothing — it is not exercising the kernel");
        Assert.True(r.Transitions >= Entities * 5, $"FSM barely ran ({r.Transitions} transitions)");
        Assert.True(r.Reschedules > 0, "no reschedules — the per-frame path is not representative");
        Assert.True(r.SlotFailures > 0, "slots never contended — the width never binds, so W is untested");

        Assert.True(r.Bytes == 0,
            $"steady-state stress allocated {r.Bytes} bytes over {Frames} frames; budget is 0");
    }

    [Fact]
    public void A_two_hundred_entity_board_holds_the_kernel_frame_slice()
    {
        // Reported precisely, asserted loosely: the number below is the useful output, and the
        // assertion exists only to catch a catastrophic regression (an accidental O(n^2), say)
        // rather than to police a few microseconds on a shared build agent.
        // Five runs, median reported. A single run straddles the tiered-compilation transition and
        // spreads over ~5x, so publishing one number as "the measurement" overstates it.
        var samples = new double[5];
        for (var i = 0; i < samples.Length; i++)
        {
            var run = RunFrames(Frames, measureAllocation: false);
            samples[i] = run.TotalMs / Frames;
        }

        Array.Sort(samples);
        var median = samples[samples.Length / 2];

        _out.WriteLine(
            $"{Entities} entities, {Frames} frames, {samples.Length} runs: median {median:F4} ms/frame " +
            $"(min {samples[0]:F4}, max {samples[^1]:F4}; slice budget {KernelSliceMs} ms)");

        Assert.True(median < KernelSliceMs * 20,
            $"kernel cost {median:F4} ms/frame is catastrophically over the {KernelSliceMs} ms slice");
    }

    [Fact]
    public void Per_event_frame_cost_does_not_grow_with_board_size()
    {
        // The property that actually matters for a stress board: cost per event must stay flat
        // (or grow only logarithmically) as the board grows. A quadratic scheduler shows up as
        // per-event cost rising with n.
        //
        // An earlier version counted DRAINED EVENTS, which scale linearly with board size BY
        // CONSTRUCTION — so the assertion was near-tautological and would have passed against a
        // quadratic queue. Comparisons per event is the honest measure.
        static double ComparisonsPerEvent(int entities)
        {
            var q = new EventQueue(expectedEvents: entities * 2);
            var buffer = new List<ScheduledEvent>(entities * 2);
            for (var i = 0; i < entities; i++) q.Schedule(i % 500, "a", 0, i);

            var before = q.ComparisonCount;
            long events = 0;
            for (var f = 0; f < 200; f++)
            {
                buffer.Clear();
                events += q.PopDue(f * 16, buffer);
                for (var i = 0; i < buffer.Count; i++)
                    q.Schedule(f * 16 + 300, "a", 0, buffer[i].Tag);
            }

            return events == 0 ? 0 : (double)(q.ComparisonCount - before) / events;
        }

        var small = ComparisonsPerEvent(100);
        var large = ComparisonsPerEvent(400);

        Assert.True(small > 0, "no comparisons counted — the instrument is not wired");
        Assert.True(large < small * 3,
            $"per-event cost grew with board size: {small:F1} comparisons/event at 100 entities " +
            $"vs {large:F1} at 400 — a heap should grow only with log n");
    }
}
