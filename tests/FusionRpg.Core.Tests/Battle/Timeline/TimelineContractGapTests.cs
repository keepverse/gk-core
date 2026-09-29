using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// Contracts the first pass declared in prose but never asserted. Each of these is a public
/// behavior a caller could rely on, so leaving them uncovered means the doc comment is the only
/// thing holding them — and a doc comment has never failed a build.
/// </summary>
public class EventQueueContractGapTests
{
    [Fact]
    public void PopDue_appends_and_never_clears_the_caller_buffer()
    {
        // The exact contract that bit ShieldRuntime.DrainEvents earlier today: the doc says
        // "appended to, never cleared" and nothing checked it. A caller accumulating several
        // drains into one buffer depends on this.
        var q = new EventQueue();
        q.Schedule(100, "a", 1, 0);
        q.Schedule(200, "b", 1, 0);

        var buffer = new List<ScheduledEvent> { new(0, -1, "pre-existing", 0, 0) };
        q.PopDue(100, buffer);
        q.PopDue(200, buffer);

        Assert.Equal(new[] { "pre-existing", "a", "b" }, buffer.Select(e => e.OwnerKey));
    }

    [Fact]
    public void PopDue_returns_how_many_it_drained()
    {
        var q = new EventQueue();
        q.Schedule(10, "a", 1, 0);
        q.Schedule(10, "b", 1, 0);
        q.Schedule(99, "c", 1, 0);

        var into = new List<ScheduledEvent>();
        Assert.Equal(2, q.PopDue(10, into));
        Assert.Equal(0, q.PopDue(10, into));
        Assert.Equal(1, q.PopDue(99, into));
    }

    [Fact]
    public void PopDue_rejects_a_null_buffer()
    {
        Assert.Throws<ArgumentNullException>(() => new EventQueue().PopDue(0, null!));
    }

    [Fact]
    public void Count_tracks_live_events_across_schedule_cancel_reschedule_and_pop()
    {
        // Count is the only external window onto queue occupancy, and reschedule pushes a second
        // heap entry for the same event — so an off-by-one here would be invisible otherwise.
        var q = new EventQueue();
        Assert.Equal(0, q.Count);

        var a = q.Schedule(100, "a", 1, 0);
        var b = q.Schedule(200, "b", 1, 0);
        q.Schedule(300, "c", 1, 0);
        Assert.Equal(3, q.Count);

        q.Cancel(a);
        Assert.Equal(2, q.Count);

        q.Reschedule(b, 250);           // must NOT change the count
        Assert.Equal(2, q.Count);

        q.PopDue(250, new List<ScheduledEvent>());
        Assert.Equal(1, q.Count);       // only "c" (at 300) remains
    }

    [Fact]
    public void Rescheduling_a_fired_handle_is_a_no_op()
    {
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);
        q.PopDue(100, new List<ScheduledEvent>());

        Assert.False(q.Reschedule(h, 500));
        Assert.Null(q.PeekDueTick());   // it must not resurrect
    }

    [Fact]
    public void Rescheduling_a_cancelled_handle_is_a_no_op()
    {
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);
        q.Cancel(h);

        Assert.False(q.Reschedule(h, 500));
        Assert.Null(q.PeekDueTick());
    }

    [Fact]
    public void An_event_can_be_rescheduled_more_than_once_and_only_the_last_tick_stands()
    {
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);

        Assert.True(q.Reschedule(h, 500));
        Assert.True(q.Reschedule(h, 300));
        Assert.True(q.Reschedule(h, 700));

        Assert.Equal(700, q.PeekDueTick());
        var into = new List<ScheduledEvent>();
        Assert.Equal(1, q.PopDue(10_000, into));    // exactly once, not once per reschedule
        Assert.Equal(700, into[0].DueTick);
    }

    [Fact]
    public void The_queue_does_not_clamp_a_backwards_reschedule_the_clock_does()
    {
        // Deliberate split of responsibility: the queue has no notion of "now", so clamping
        // belongs to the caller. What must hold is that a past-due event cannot rewind the clock.
        var q = new EventQueue();
        var h = q.Schedule(500, "a", 1, 0);
        var clock = new SimulationClock();
        clock.TryAdvance(new NextEventAdvance(), q);
        Assert.Equal(500, clock.Now);

        q.Schedule(600, "b", 1, 0);
        var h2 = q.Schedule(900, "c", 1, 0);
        Assert.True(q.Reschedule(h2, 10));          // into the past: allowed at queue level
        Assert.Equal(10, q.PeekDueTick());

        clock.TryAdvance(new NextEventAdvance(), q);
        Assert.Equal(500, clock.Now);               // clock holds; it never goes backwards
    }
}

/// <summary>
/// Reschedule works by pushing a SECOND heap entry with the same Seq and marking the old one
/// stale. That makes duplicate-entry handling the subtlest thing in the queue, and the cases
/// below are the ones where staleness cannot distinguish the copies — so an event firing twice
/// is stopped by exactly one line (PopDue retiring the Seq on fire). These lock that.
/// </summary>
public class EventQueueDuplicateEntryTests
{
    [Fact]
    public void Rescheduling_onto_its_own_current_tick_still_fires_exactly_once()
    {
        // Both heap entries now have the SAME DueTick as the reschedule record, so neither looks
        // stale. Only retiring the Seq on fire prevents a double-fire.
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);
        Assert.True(q.Reschedule(h, 100));

        var into = new List<ScheduledEvent>();
        Assert.Equal(1, q.PopDue(100, into));
        Assert.Single(into);
        Assert.Equal(0, q.Count);
        Assert.Null(q.PeekDueTick());
    }

    [Fact]
    public void Rescheduling_twice_onto_the_same_tick_still_fires_exactly_once()
    {
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);
        q.Reschedule(h, 500);
        q.Reschedule(h, 500);   // a third heap entry, again indistinguishable by tick

        var into = new List<ScheduledEvent>();
        Assert.Equal(1, q.PopDue(1000, into));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Rescheduling_one_event_onto_another_events_tick_keeps_both_and_orders_by_seq()
    {
        var q = new EventQueue();
        var first = q.Schedule(100, "first", 1, 0);
        q.Schedule(500, "second", 1, 0);
        Assert.True(q.Reschedule(first, 500));

        var into = new List<ScheduledEvent>();
        Assert.Equal(2, q.PopDue(500, into));
        Assert.Equal(new[] { "first", "second" }, into.Select(e => e.OwnerKey));   // Seq preserved
    }

    [Fact]
    public void Cancelling_after_rescheduling_removes_every_copy()
    {
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);
        q.Reschedule(h, 500);
        Assert.True(q.Cancel(h));

        Assert.Equal(0, q.Count);
        Assert.Null(q.PeekDueTick());
        Assert.Empty(Drain(q));
    }

    [Fact]
    public void Rescheduling_a_currently_due_event_defers_it_rather_than_firing_it()
    {
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);
        q.Reschedule(h, 900);

        Assert.Empty(Drain(q, 100));            // no longer due at 100
        Assert.Equal(1, q.Count);
        Assert.Equal(new[] { "a" }, Drain(q, 900).Select(e => e.OwnerKey));
    }

    [Fact]
    public void A_single_element_heap_pops_cleanly()
    {
        // PopRoot assigns _heap[0] = _heap[last] before removing; at Count == 1 those are the
        // same slot, which is the classic off-by-one in a hand-written heap.
        var q = new EventQueue();
        q.Schedule(42, "only", 1, 0);
        Assert.Equal(new[] { "only" }, Drain(q, 42).Select(e => e.OwnerKey));
        Assert.Equal(0, q.Count);
        Assert.Null(q.PeekDueTick());
    }

    [Fact]
    public void Heap_pops_a_shuffled_batch_in_exact_due_tick_order()
    {
        // Exercises sift-up and sift-down over enough elements that a broken comparison shows.
        var q = new EventQueue();
        var ticks = new[] { 50L, 900, 3, 120, 7, 400, 65, 2, 800, 31, 11, 500, 1, 77, 250 };
        foreach (var t in ticks) q.Schedule(t, "t" + t, 0, t);

        var popped = Drain(q, long.MaxValue).Select(e => e.DueTick).ToList();
        Assert.Equal(ticks.OrderBy(t => t).ToList(), popped);
    }

    static List<ScheduledEvent> Drain(EventQueue q, long now = long.MaxValue)
    {
        var into = new List<ScheduledEvent>();
        q.PopDue(now, into);
        return into;
    }
}

public class EventQueueChurnTests
{
    [Fact]
    public void Heavy_cancel_churn_leaves_the_queue_correct()
    {
        // Cancellation is by tombstone, so the bookkeeping sets grow with the number of events
        // ever scheduled. That is bounded by battle length for the modes we own (and T7 is
        // stateless projection with no queue at all), but the correctness under churn is what
        // must hold: stale entries must never surface, and Count must stay honest.
        var q = new EventQueue();
        var handles = new List<EventHandle>();
        for (var i = 0; i < 2000; i++)
            handles.Add(q.Schedule(i, "a" + i, 0, i));

        for (var i = 0; i < 2000; i += 2) q.Cancel(handles[i]);      // cancel every other
        Assert.Equal(1000, q.Count);

        var into = new List<ScheduledEvent>();
        q.PopDue(long.MaxValue, into);

        Assert.Equal(1000, into.Count);
        Assert.All(into, e => Assert.True(e.Tag % 2 == 1, $"cancelled event {e.OwnerKey} surfaced"));
        Assert.Equal(0, q.Count);
        Assert.Null(q.PeekDueTick());
    }

    [Fact]
    public void Peek_skips_stale_entries_without_consuming_live_ones()
    {
        var q = new EventQueue();
        var doomed = q.Schedule(10, "doomed", 0, 0);
        q.Schedule(20, "alive", 0, 0);
        q.Cancel(doomed);

        Assert.Equal(20, q.PeekDueTick());
        Assert.Equal(20, q.PeekDueTick());   // idempotent: peeking twice must not drop it

        var into = new List<ScheduledEvent>();
        Assert.Equal(1, q.PopDue(20, into));
        Assert.Equal("alive", into[0].OwnerKey);
    }
}

/// <summary>Regression locks for the 2026-08-21 code-review findings.</summary>
public class ReviewFixTests
{
    [Fact]
    public void A_foreign_handle_cannot_cancel_an_event()
    {
        // Handles are bare integers; without a queue id, q1's handle addressed a DIFFERENT event
        // in q2 and deleted it silently — no error, no exception, and a replay that diverges with
        // nothing to point at.
        var q1 = new EventQueue();
        var q2 = new EventQueue();
        var h1 = q1.Schedule(100, "q1-first", 1, 0);
        q2.Schedule(100, "q2-victim", 1, 0);
        q2.Schedule(200, "q2-other", 1, 0);

        Assert.False(q2.Cancel(h1));
        Assert.False(q2.Reschedule(h1, 900));
        Assert.Equal(2, q2.Count);

        var into = new List<ScheduledEvent>();
        q2.PopDue(long.MaxValue, into);
        Assert.Equal(new[] { "q2-victim", "q2-other" }, into.Select(e => e.OwnerKey));
    }

    [Fact]
    public void A_bogus_handle_cannot_drive_count_negative()
    {
        // The liveness bound was one-sided (`seq < _nextSeq`), so every negative read as live and
        // decremented Count. A driver looping `while (Count > 0)` could then never terminate.
        var q = new EventQueue();
        q.Schedule(100, "a", 1, 0);

        Assert.False(q.Cancel(new EventHandle(long.MaxValue, -1)));
        Assert.False(q.Cancel(new EventHandle(long.MaxValue, -999)));
        Assert.Equal(1, q.Count);

        q.PopDue(100, new List<ScheduledEvent>());
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void The_legal_transition_table_cannot_be_rewritten_through_the_public_view()
    {
        // static readonly array behind IReadOnlyList: one cast rewrote the rules for every actor,
        // every battle, and every later test in the process.
        Assert.Throws<InvalidCastException>(() =>
            _ = (ValueTuple<TurnState, TurnState>[])TurnTransitions.All);
    }

    [Fact]
    public void A_retreating_actor_can_withdraw_mid_resolution()
    {
        // The engine checks retreat inside the dispatch loop, so a coward crossing its threshold
        // on the hit it is delivering withdraws while Resolving. The table omitted it while its
        // own comment claimed "any live state" — and illegal transitions throw.
        var m = new ActorTurnMachine("squad:0");
        m.TransitionTo(TurnState.Ready);
        m.TransitionTo(TurnState.Committed);
        m.TransitionTo(TurnState.Resolving);

        m.TransitionTo(TurnState.Withdrawn);   // must not throw
        Assert.Equal(TurnState.Withdrawn, m.State);
    }

    [Fact]
    public void Contender_order_is_total_even_when_tick_and_seq_collide()
    {
        // List<T>.Sort is introsort: unstable, and it switches algorithm above 16 elements. With
        // only (tick, seq) as keys, 40 colliding contenders came out in a pivot-dependent
        // permutation — in the one method whose doc promises determinism. The 4-element test
        // could never catch it, being inside the stable insertion-sort region.
        var contenders = Enumerable.Range(0, 40)
            .Select(i => ("a" + i.ToString("D2"), 0L, 0L)).ToList();

        var first = new List<(string, long, long)>(contenders);
        ActionSlots.SortContenders(first);

        var shuffled = contenders.AsEnumerable().Reverse().ToList();
        ActionSlots.SortContenders(shuffled);

        Assert.Equal(first.Select(c => c.Item1), shuffled.Select(c => c.Item1));
        Assert.Equal(first.Select(c => c.Item1).OrderBy(k => k, StringComparer.Ordinal),
                     first.Select(c => c.Item1));
    }

    [Fact]
    public void Envelopes_compare_by_value_including_their_resolve_offsets()
    {
        var a = ActionEnvelope.NoOp with { ResolveOffsets = new long[] { 0, 100 } };
        var b = ActionEnvelope.NoOp with { ResolveOffsets = new long[] { 0, 100 } };
        var c = ActionEnvelope.NoOp with { ResolveOffsets = new long[] { 0, 250 } };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Trace_views_are_snapshots_that_cannot_be_forged()
    {
        var t = new BattleTrace();
        t.Phase(1, "status");
        t.Draw("crit", 5);

        Assert.Throws<InvalidCastException>(() => _ = (List<string>)t.Phases);
        Assert.Throws<InvalidCastException>(() => _ = (List<int>)t.Draws("crit"));

        var before = t.Draws("crit");
        t.Draw("crit", 6);
        Assert.Single(before);           // the snapshot did not grow underneath the caller
        Assert.Equal(2, t.Draws("crit").Count);
    }

    [Fact]
    public void An_absurd_frame_count_is_refused_rather_than_overflowing()
    {
        var advance = new FixedIncrementAdvance(1000, 60);
        var clock = new SimulationClock();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            clock.TryAdvance(advance, new EventQueue(), frames: long.MaxValue / 500));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            clock.TryAdvance(advance, new EventQueue(), frames: -1));
    }
}

public class ClockContractGapTests
{
    [Fact]
    public void Fixed_increment_rejects_a_non_positive_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedIncrementAdvance(0, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedIncrementAdvance(1000, 0));
    }

    [Fact]
    public void Advancing_zero_frames_moves_nothing()
    {
        var clock = new SimulationClock();
        var r = clock.TryAdvance(new FixedIncrementAdvance(1000, 60), new EventQueue(), frames: 0);
        Assert.Equal(AdvanceOutcome.Advanced, r.Outcome);
        Assert.Equal(0, clock.Now);
    }

    [Fact]
    public void An_event_due_exactly_now_advances_without_moving_the_clock()
    {
        var q = new EventQueue();
        var clock = new SimulationClock();
        q.Schedule(0, "a", 1, 0);

        var r = clock.TryAdvance(new NextEventAdvance(), q);
        Assert.Equal(AdvanceOutcome.Advanced, r.Outcome);
        Assert.Equal(0, clock.Now);
    }

    [Fact]
    public void TryAdvance_rejects_null_arguments()
    {
        var clock = new SimulationClock();
        Assert.Throws<ArgumentNullException>(() => clock.TryAdvance(null!, new EventQueue()));
        Assert.Throws<ArgumentNullException>(() => clock.TryAdvance(new NextEventAdvance(), null!));
    }
}

public class ActorTurnMachineContractGapTests
{
    [Fact]
    public void TryTransitionTo_moves_on_a_legal_edge_and_refuses_an_illegal_one()
    {
        // The whole try-variant was public and had zero coverage.
        var m = new ActorTurnMachine("squad:0");

        Assert.True(m.TryTransitionTo(TurnState.Ready));
        Assert.Equal(TurnState.Ready, m.State);

        Assert.False(m.TryTransitionTo(TurnState.Recovering));
        Assert.Equal(TurnState.Ready, m.State);   // refused, and did not move
    }

    [Fact]
    public void An_actor_key_is_required()
    {
        Assert.Throws<ArgumentException>(() => new ActorTurnMachine(""));
        Assert.Throws<ArgumentException>(() => new ActorTurnMachine("   "));
        Assert.Throws<ArgumentException>(() => new ActorTurnMachine(null!));
    }

    [Fact]
    public void A_downed_actor_is_still_present_but_a_withdrawn_one_is_not()
    {
        var downed = new ActorTurnMachine("a");
        downed.TransitionTo(TurnState.Downed);
        Assert.True(downed.IsPresent);   // targetable and revivable

        var gone = new ActorTurnMachine("b");
        gone.TransitionTo(TurnState.Withdrawn);
        Assert.False(gone.IsPresent);
    }
}

public class ActionEnvelopeContractGapTests
{
    [Fact]
    public void NoOp_is_zero_length_but_still_a_normal_slot_consuming_action()
    {
        var e = ActionEnvelope.NoOp;
        Assert.Equal(0, e.WindupTicks);
        Assert.Equal(0, e.RecoveryTicks);
        Assert.Equal(0, e.TimeCostTicks);
        Assert.True(e.SlotConsuming);
        Assert.Equal(new long[] { 0 }, e.ResolveOffsets);
        Assert.Equal(DerivedTurnChannels.Speed, e.SpeedChannel);
        Assert.Equal(CooldownClass.None, e.Class);
    }

    [Fact]
    public void A_slot_free_envelope_is_expressible()
    {
        // Movement and periodic pulses must not consume the W slot, or at W=1 only one actor
        // could ever move.
        var pulse = ActionEnvelope.NoOp with { ActionId = "regen-tick", SlotConsuming = false };
        Assert.False(pulse.SlotConsuming);
    }

    [Fact]
    public void Turn_channels_are_flat_and_not_element_split()
    {
        Assert.Equal("turn.speed", DerivedTurnChannels.Speed);
        Assert.Equal("turn.haste", DerivedTurnChannels.Haste);
        Assert.DoesNotContain(".omni", DerivedTurnChannels.Speed, StringComparison.Ordinal);
    }
}
