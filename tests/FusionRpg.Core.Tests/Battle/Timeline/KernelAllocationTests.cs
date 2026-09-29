using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// The kernel ticks inside the Unity frame (owner decision 2026-08-21), so allocation is a
/// correctness constraint, not a tuning concern: GC pressure is what produces stutter, and "no
/// gen2 during a level" is already a stated non-goal in the perf plan.
///
/// These assert BYTES, not milliseconds. A wall-clock test in CI measures the build agent's mood;
/// an allocation delta is deterministic, fast, and fails for exactly one reason. Operation counts
/// cover complexity — the regression class that hid an O(n) reschedule behind a passing suite.
/// </summary>
public class KernelAllocationTests
{
    /// <summary>Runs an action after warm-up and returns bytes allocated on this thread.</summary>
    static long BytesFor(Action warmup, Action measured)
    {
        warmup();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        measured();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void A_steady_state_schedule_and_drain_cycle_allocates_nothing()
    {
        // The shape of a real frame: schedule the next event per actor, drain what is due, repeat.
        // Pre-sized structures plus a reused buffer must make this free.
        var q = new EventQueue(expectedEvents: 256);
        var buffer = new List<ScheduledEvent>(256);
        var handles = new EventHandle[128];
        long tick = 0;

        void Cycle()
        {
            for (var round = 0; round < 20; round++)
            {
                for (var i = 0; i < handles.Length; i++)
                    handles[i] = q.Schedule(tick + 1 + i, "actor", 0, i);

                tick += 200;
                buffer.Clear();
                q.PopDue(tick, buffer);
            }
        }

        var bytes = BytesFor(Cycle, Cycle);
        Assert.True(bytes == 0, $"steady-state schedule/drain allocated {bytes} bytes; budget is 0");
    }

    [Fact]
    public void Rescheduling_in_the_steady_state_allocates_nothing()
    {
        // Re-timing is what haste, slow, and delay effects do every round — the operation most
        // likely to run per-frame at scale, and the one whose first implementation was O(n).
        var q = new EventQueue(expectedEvents: 256);
        var handles = new EventHandle[200];
        for (var i = 0; i < handles.Length; i++)
            handles[i] = q.Schedule(1000 + i, "actor", 0, i);

        void Churn()
        {
            for (var round = 0; round < 30; round++)
                for (var i = 0; i < handles.Length; i++)
                    q.Reschedule(handles[i], 1000 + ((round * 200 + i) % 5000));
        }

        var bytes = BytesFor(Churn, Churn);
        Assert.True(bytes == 0, $"6000 reschedules allocated {bytes} bytes; budget is 0");
    }

    [Fact]
    public void Advancing_the_clock_allocates_nothing()
    {
        var clock = new SimulationClock();
        var advance = new FixedIncrementAdvance(1000, 60);
        var q = new EventQueue(expectedEvents: 8);

        void Frames()
        {
            for (var i = 0; i < 5_000; i++) clock.TryAdvance(advance, q, frames: 1);
        }

        var bytes = BytesFor(Frames, Frames);
        Assert.True(bytes == 0, $"5000 clock advances allocated {bytes} bytes; budget is 0");
    }

    [Fact]
    public void Declaring_an_intent_allocates_nothing()
    {
        // ActionIntent was a record CLASS: one heap object per actor per turn. At 200 entities
        // that is per-frame garbage on the Unity main thread. It is a struct now, and this is the
        // test that keeps it one.
        var envelope = ActionEnvelope.NoOp with { ActionId = "basic-attack" };
        IIntentSource source = new FixedIntent(envelope);
        var sink = 0;

        void Declare()
        {
            for (var i = 0; i < 10_000; i++)
            {
                var intent = source.TryDeclare("squad:0", i);
                if (!intent.IsNone) sink++;
            }
        }

        var bytes = BytesFor(Declare, Declare);
        Assert.True(bytes == 0, $"10000 intent declarations allocated {bytes} bytes; budget is 0");
        Assert.True(sink > 0);
    }

    [Fact]
    public void Envelope_equality_and_hashing_allocate_nothing()
    {
        // GetHashCode was boxing an enumerator per call — 32 bytes — by `foreach`-ing an
        // IReadOnlyList. Exactly the allocation OffsetsEqual was hand-rolled to avoid eight lines
        // earlier in the same file, and uncovered because no test hashed an envelope. It matters
        // because the envelope is documented as a cache key, and a cache key's hot operation IS
        // GetHashCode: 200 actors at 60 fps would be ~384 KB/s against a zero-byte budget.
        var a = ActionEnvelope.NoOp with { ActionId = "x", ResolveOffsets = new long[] { 0, 100, 250 } };
        var b = a with { };
        var sink = 0;

        void Hash()
        {
            for (var i = 0; i < 10_000; i++)
            {
                sink ^= a.GetHashCode();
                if (a.Equals(b)) sink++;
            }
        }

        var bytes = BytesFor(Hash, Hash);
        Assert.True(bytes == 0, $"10000 hash+equality calls allocated {bytes} bytes; budget is 0");
        Assert.True(sink != 0);
    }

    [Fact]
    public void The_turn_machine_transitions_without_allocating()
    {
        var m = new ActorTurnMachine("squad:0");

        void Cycle()
        {
            for (var i = 0; i < 10_000; i++)
            {
                m.TransitionTo(TurnState.Ready);
                m.TransitionTo(TurnState.Committed);
                m.TransitionTo(TurnState.Resolving);
                m.TransitionTo(TurnState.Recovering);
                m.TransitionTo(TurnState.Charging);
            }
        }

        var bytes = BytesFor(Cycle, Cycle);
        Assert.True(bytes == 0, $"50000 transitions allocated {bytes} bytes; budget is 0");
    }

    [Fact]
    public void Slot_acquire_and_release_allocates_nothing_once_warm()
    {
        var slots = new ActionSlots(4);

        void Cycle()
        {
            for (var i = 0; i < 5_000; i++)
            {
                slots.TryAcquire("squad:0", "squad");
                slots.TryAcquire("squad:1", "squad");
                slots.Release("squad:0");
                slots.Release("squad:1");
            }
        }

        var bytes = BytesFor(Cycle, Cycle);
        Assert.True(bytes == 0, $"slot churn allocated {bytes} bytes; budget is 0");
    }

    sealed class FixedIntent : IIntentSource
    {
        readonly ActionEnvelope _envelope;
        public FixedIntent(ActionEnvelope envelope) => _envelope = envelope;
        public ActionIntent TryDeclare(string actorKey, long nowTick) =>
            new("basic-attack", "wave:0", _envelope);
    }

    [Fact]
    public void A_full_action_lifecycle_allocates_nothing_after_the_first_turn()
    {
        // Commit, wind-up, multi-hit resolve, recovery — the path every actor walks every turn.
        // The runner keeps one reusable run per actor precisely so this stays at zero; a record
        // per commit would be a heap object per actor per turn on the Unity main thread.
        var queue = new EventQueue(expectedEvents: 128);
        var slots = new ActionSlots(8);
        var cooldowns = new CooldownLedger();
        var runner = new ActionRunner(queue, slots, cooldowns, _ => true, expectedActors: 8);
        var buffer = new List<ScheduledEvent>(64);

        var actors = new ActorTurnMachine[8];
        for (var i = 0; i < actors.Length; i++) actors[i] = new ActorTurnMachine("actor:" + i);

        var envelope = new ActionEnvelope
        {
            ActionId = "combo",
            WindupTicks = 40,
            RecoveryTicks = 20,
            ResolveOffsets = new long[] { 0, 15, 30 },
            Class = CooldownClass.Specific,
            CooldownTicks = 10,
            StartsAt = CooldownStart.Resolve
        };

        long now = 0;

        void Turn()
        {
            for (var round = 0; round < 20; round++)
            {
                for (var i = 0; i < actors.Length; i++)
                {
                    actors[i].TransitionTo(TurnState.Ready);
                    runner.TryCommit(actors[i], "left", new ActionIntent("combo", "target", envelope), now);
                }

                now += 500;
                buffer.Clear();
                queue.PopDue(now, buffer);
                for (var i = 0; i < buffer.Count; i++)
                {
                    var e = buffer[i];
                    var actor = actors[(int)(e.OwnerKey[6] - '0')];
                    if ((TimelineEventKind)e.Kind == TimelineEventKind.Resolve) runner.OnResolveDue(actor, e);
                    else runner.OnRecoveryDue(actor, e);
                }

                // Recovery lands on the same drain tick as the last hit here, so pump once more.
                buffer.Clear();
                queue.PopDue(now, buffer);
                for (var i = 0; i < buffer.Count; i++)
                {
                    var e = buffer[i];
                    runner.OnRecoveryDue(actors[(int)(e.OwnerKey[6] - '0')], e);
                }
            }
        }

        var bytes = BytesFor(Turn, Turn);
        Assert.True(bytes == 0, $"a steady-state action lifecycle allocated {bytes} bytes; budget is 0");
    }
}

/// <summary>
/// Complexity proven by counting, not by timing. A stopwatch in CI blames the build agent; a
/// comparison count fails only when the algorithm actually degrades — which is precisely what a
/// green suite hid when reschedule was a linear scan.
/// </summary>
public class KernelComplexityTests
{
    /// <summary>Ordering and lookup work performed by n reschedules over a queue of n events.</summary>
    static (long Comparisons, long Probes) RescheduleWork(int n)
    {
        var q = new EventQueue(expectedEvents: n);
        var handles = new EventHandle[n];
        for (var i = 0; i < n; i++) handles[i] = q.Schedule(i, "a", 0, i);

        var c0 = q.ComparisonCount;                  // exclude the build phase
        var p0 = q.ProbeCount;
        for (var i = 0; i < n; i++)
            q.Reschedule(handles[i], (i * 7919) % (n * 4));

        return (q.ComparisonCount - c0, q.ProbeCount - p0);
    }

    [Fact]
    public void Reschedule_ordering_cost_grows_log_linearly()
    {
        // Scaling n by 8, log-linear ordering work grows ~6.5x in practice (measured), quadratic
        // ~64x. The ceiling sits between, so it discriminates — but see the probe test below:
        // comparisons ALONE cannot see the regression this suite is named after.
        var small = RescheduleWork(2_000);
        var large = RescheduleWork(16_000);

        Assert.True(small.Comparisons > 0, "no comparisons counted — the instrument is not wired");
        Assert.True(large.Comparisons < small.Comparisons * 12,
            $"reschedule ordering looks super-linear: {small.Comparisons} at n=2000 vs " +
            $"{large.Comparisons} at n=16000 ({(double)large.Comparisons / small.Comparisons:F1}x " +
            "for an 8x board; log-linear measures ~6.5x, quadratic ~64x)");
    }

    [Fact]
    public void Reschedule_lookup_cost_stays_constant_per_call()
    {
        // THE test that catches the actual historical regression. Deleting the index map and
        // finding a heap slot by linear scan leaves the sift work — and therefore the comparison
        // count — BYTE-IDENTICAL, so the ordering test above sails straight through a 5x
        // wall-clock blowup. Lookups are the axis that moves, so lookups are what to count.
        //
        // Indexed: exactly one probe per Reschedule, regardless of n. Linear scan: ~n/2 each,
        // so this ratio would be ~8x rather than 1x.
        var small = RescheduleWork(2_000);
        var large = RescheduleWork(16_000);

        Assert.Equal(2_000, small.Probes);
        Assert.Equal(16_000, large.Probes);

        var probesPerCall = (double)large.Probes / 16_000;
        Assert.True(probesPerCall <= 1.0,
            $"lookup is no longer O(1): {probesPerCall:F2} probes per reschedule at n=16000");
    }

    [Fact]
    public void Drain_cost_grows_log_linearly_not_quadratically()
    {
        static long DrainComparisons(int n)
        {
            var q = new EventQueue(expectedEvents: n);
            for (var i = 0; i < n; i++) q.Schedule((i * 7919) % n, "a", 0, i);

            var before = q.ComparisonCount;
            q.PopDue(long.MaxValue, new List<ScheduledEvent>(n));
            return q.ComparisonCount - before;
        }

        var small = DrainComparisons(2_000);
        var large = DrainComparisons(16_000);

        Assert.True(small > 0);
        Assert.True(large < small * 20,
            $"drain looks super-linear: {small} vs {large} ({(double)large / small:F1}x for an 8x board)");
    }

    [Fact]
    public void The_structure_never_holds_more_than_the_live_event_count()
    {
        // The growth property that the lazy-deletion design violated 61x over.
        var q = new EventQueue(expectedEvents: 64);
        var handles = new EventHandle[50];
        for (var i = 0; i < handles.Length; i++) handles[i] = q.Schedule(100 + i, "a", 0, i);

        for (var round = 0; round < 500; round++)
            for (var i = 0; i < handles.Length; i++)
                q.Reschedule(handles[i], 100 + ((round + i) % 900));

        Assert.Equal(50, q.Count);
    }
}
