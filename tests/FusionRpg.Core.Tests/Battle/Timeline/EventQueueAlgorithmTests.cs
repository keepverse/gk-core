using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// The queue is an INDEXED binary heap, so every cancel and reschedule mutates the middle of the
/// structure and must repair both the heap order and the seq→index map. That is precisely where a
/// hand-written heap goes wrong, and the failure is silent: a corrupt index map still pops
/// *something*, just not in the right order.
///
/// These tests therefore check the INVARIANT (a full drain is globally sorted) under adversarial
/// churn, rather than checking a handful of scripted sequences.
/// </summary>
public class EventQueueAlgorithmTests
{
    static List<ScheduledEvent> DrainAll(EventQueue q)
    {
        var into = new List<ScheduledEvent>();
        q.PopDue(long.MaxValue, into);
        return into;
    }

    static void AssertGloballySorted(List<ScheduledEvent> popped)
    {
        for (var i = 1; i < popped.Count; i++)
        {
            var prev = popped[i - 1];
            var cur = popped[i];
            var ordered = prev.DueTick < cur.DueTick || (prev.DueTick == cur.DueTick && prev.Seq < cur.Seq);
            Assert.True(ordered,
                $"heap order broken at {i}: ({prev.DueTick},{prev.Seq}) then ({cur.DueTick},{cur.Seq})");
        }
    }

    [Fact]
    public void A_deterministic_churn_of_schedule_cancel_and_reschedule_stays_perfectly_ordered()
    {
        // Deterministic pseudo-random churn — no RNG dependency, but irregular enough to drive
        // sift-up, sift-down, and mid-heap removal in every combination.
        var q = new EventQueue();
        var handles = new List<EventHandle>();
        var live = 0;

        for (var i = 0; i < 3000; i++)
        {
            var tick = (i * 7919) % 5000;
            handles.Add(q.Schedule(tick, "a" + i, i % 5, i));
            live++;

            if (i % 3 == 0 && handles.Count > 10)
            {
                var victim = handles[(i * 31) % handles.Count];
                if (q.Cancel(victim)) live--;
            }

            if (i % 2 == 0 && handles.Count > 5)
            {
                var target = handles[(i * 17) % handles.Count];
                q.Reschedule(target, (i * 4093) % 5000);   // moves both earlier and later
            }
        }

        Assert.Equal(live, q.Count);

        var popped = DrainAll(q);
        Assert.Equal(live, popped.Count);
        AssertGloballySorted(popped);
        Assert.Equal(0, q.Count);
        Assert.Null(q.PeekDueTick());
    }

    [Fact]
    public void Rescheduling_does_not_grow_the_structure()
    {
        // The measured failure of the lazy-deletion draft: 200 live events, 12 000 reschedules,
        // and a heap that had grown to 12 200 entries. Count IS the heap length now, so this
        // asserts the growth property directly.
        var q = new EventQueue();
        var handles = new List<EventHandle>();
        for (var i = 0; i < 200; i++)
            handles.Add(q.Schedule(1000 + i, "actor" + i, 0, i));

        for (var round = 0; round < 60; round++)
            for (var i = 0; i < 200; i++)
                Assert.True(q.Reschedule(handles[i], 1000 + ((round * 200 + i) % 7000)));

        Assert.Equal(200, q.Count);          // no accumulation whatsoever
        AssertGloballySorted(DrainAll(q));
    }

    [Fact]
    public void Cancelling_everything_leaves_nothing_behind()
    {
        // Lazy deletion retained a tombstone per event forever; exact removal must not.
        var q = new EventQueue();
        var handles = new List<EventHandle>();
        for (var i = 0; i < 500; i++)
            handles.Add(q.Schedule((i * 13) % 500, "a" + i, 0, i));

        foreach (var h in handles) Assert.True(q.Cancel(h));

        Assert.Equal(0, q.Count);
        Assert.Null(q.PeekDueTick());
        Assert.Empty(DrainAll(q));
        foreach (var h in handles) Assert.False(q.Cancel(h));   // idempotent, still no drift
    }

    [Fact]
    public void Cancelling_the_root_the_last_and_a_middle_node_all_repair_the_heap()
    {
        // Removal relocates the last element into the hole and must sift it in whichever
        // direction it belongs. Getting that wrong is silent — the heap still pops, just wrongly.
        foreach (var which in new[] { "root", "middle", "last" })
        {
            var q = new EventQueue();
            var handles = new List<EventHandle>();
            foreach (var t in new long[] { 10, 90, 20, 80, 30, 70, 40, 60, 50, 100 })
                handles.Add(q.Schedule(t, "t" + t, 0, t));

            var victim = which switch
            {
                "root" => handles[0],                       // tick 10 — the minimum
                "middle" => handles[4],                     // tick 30
                _ => handles[^1]                            // tick 100 — the last added
            };
            Assert.True(q.Cancel(victim));

            var popped = DrainAll(q);
            Assert.Equal(9, popped.Count);
            AssertGloballySorted(popped);
        }
    }

    [Fact]
    public void Rescheduling_the_minimum_to_the_maximum_and_back_keeps_order()
    {
        var q = new EventQueue();
        var min = q.Schedule(1, "min", 0, 0);
        for (var i = 2; i <= 20; i++) q.Schedule(i * 10, "t" + i, 0, i);

        Assert.True(q.Reschedule(min, 9999));      // minimum sinks to the bottom
        Assert.Equal(20, q.PeekDueTick());
        Assert.True(q.Reschedule(min, 0));         // and rises back to the top
        Assert.Equal(0, q.PeekDueTick());

        AssertGloballySorted(DrainAll(q));
    }

    [Fact]
    public void Replaying_the_same_churn_script_produces_an_identical_sequence()
    {
        string Run()
        {
            var q = new EventQueue();
            var handles = new List<EventHandle>();
            for (var i = 0; i < 400; i++) handles.Add(q.Schedule((i * 37) % 300, "a" + i, i % 3, i));
            for (var i = 0; i < 400; i += 7) q.Cancel(handles[i]);
            for (var i = 1; i < 400; i += 11) q.Reschedule(handles[i], (i * 53) % 900);
            return string.Join(",", DrainAll(q).Select(e => $"{e.DueTick}:{e.Seq}"));
        }

        Assert.Equal(Run(), Run());
    }
}
