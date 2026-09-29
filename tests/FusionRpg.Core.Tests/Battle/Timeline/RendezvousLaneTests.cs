using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B7 / T2e — multi-actor coordinated actions. Proven with a minimal rig, same shape as
/// <see cref="TurnFsmActionEnvelopeTests"/>'s <c>Rig</c>: an <see cref="EventQueue"/>, an
/// <see cref="ActionSlots"/>, and a dictionary of actors — no game attached.
/// </summary>
public class RendezvousLaneTests
{
    static readonly ActionEnvelope LinkEnvelope = new()
    {
        ActionId = "link-strike",
        WindupTicks = 100,
        RecoveryTicks = 50,
        SlotConsuming = true
    };

    sealed class Rig
    {
        public readonly EventQueue Queue = new(32);
        public readonly ActionSlots Slots;
        public readonly CooldownLedger Cooldowns = new();
        public readonly RendezvousLane Lane;
        public readonly ActionRunner Runner;

        readonly Dictionary<string, ActorTurnMachine> _actors = new(StringComparer.Ordinal);

        public Rig(int width = 4)
        {
            Slots = new ActionSlots(width);
            Lane = new RendezvousLane(Queue, Slots, Cooldowns, Actor);
            Runner = new ActionRunner(Queue, Slots, Cooldowns, _ => true);
        }

        public ActorTurnMachine Add(string key)
        {
            var m = new ActorTurnMachine(key);
            _actors[key] = m;
            return m;
        }

        public ActorTurnMachine Actor(string key) => _actors[key];
    }

    [Fact]
    public void Two_actors_committing_together_schedule_exactly_one_resolve_event()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("b");

        var first = rig.Lane.Open("link:1", new[] { "a", "b" }, "a", "left", LinkEnvelope, targetKey: null, nowTick: 0, timeoutTicks: 500);
        Assert.Equal(RendezvousOutcome.Waiting, first);
        // FSM-neutral while waiting: no WaitingForPartner state exists, and the lane does not touch
        // the actor's own turn state at all until the WHOLE reservation completes.
        Assert.Equal(TurnState.Charging, rig.Actor("a").State);

        var second = rig.Lane.TryJoin("link:1", "b", nowTick: 10);
        Assert.Equal(RendezvousOutcome.Committed, second);

        Assert.Equal(TurnState.Committed, rig.Actor("a").State);
        Assert.Equal(TurnState.Committed, rig.Actor("b").State);
        Assert.Equal(1, rig.Queue.Count); // ONE shared resolve — not two

        // Drain it like a real pump would (PopDue removes it from the queue) before handing it to
        // the handler — calling the handler without draining first would double-count below.
        var due = new List<ScheduledEvent>();
        Assert.Equal(1, rig.Queue.PopDue(110, due));
        Assert.Equal((int)TimelineEventKind.LinkedResolve, due[0].Kind);
        Assert.Equal("link:1", due[0].OwnerKey);
        rig.Lane.OnLinkedResolveDue(due[0].OwnerKey, due[0].DueTick);

        // Both transitioned off the SAME firing, straight through to Recovering (single-hit scope).
        Assert.Equal(TurnState.Recovering, rig.Actor("a").State);
        Assert.Equal(TurnState.Recovering, rig.Actor("b").State);
        Assert.Equal(0, rig.Slots.Held); // released on resolve
        Assert.Equal(2, rig.Queue.Count); // each participant's OWN recovery — recovery applies to each
    }

    [Fact]
    public void A_partner_that_never_arrives_times_out_and_both_act_solo_no_hang_at_w1()
    {
        var rig = new Rig(width: 1);
        rig.Add("a");
        rig.Add("b");

        var joinOutcome = rig.Lane.Open("link:2", new[] { "a", "b" }, "a", "left", LinkEnvelope, targetKey: null, nowTick: 0, timeoutTicks: 200);
        Assert.Equal(RendezvousOutcome.Waiting, joinOutcome);

        // "b" never joins. The timeout fires — this is what a kernel drive would do on draining a
        // RendezvousTimeout event; simulated directly here since there is no game loop in this module.
        var fallenBack = rig.Lane.OnTimeoutDue("link:2");
        Assert.Equal(new[] { "a" }, fallenBack);

        // A late join after the timeout reports Expired rather than resurrecting the reservation.
        Assert.Equal(RendezvousOutcome.Expired, rig.Lane.TryJoin("link:2", "b", nowTick: 250));

        // Both actors fall back to solo commits — no hang, and W=1 does not deadlock either of them
        // in sequence (the first releases before the second needs the slot).
        rig.Actor("a").TransitionTo(TurnState.Ready);
        var soloA = rig.Runner.TryCommit(rig.Actor("a"), "left", new ActionIntent("basic", null, LinkEnvelope), nowTick: 250);
        Assert.Equal(CommitRefusal.None, soloA);
        // LinkEnvelope leaves Interruptible at its default (OnCC), so CrowdControl is the cause that
        // actually yields — frees the slot for "b" deterministically rather than waiting on the
        // windup to resolve.
        rig.Runner.Interrupt(rig.Actor("a"), 251, InterruptCause.CrowdControl);

        rig.Actor("b").TransitionTo(TurnState.Ready);
        var soloB = rig.Runner.TryCommit(rig.Actor("b"), "left", new ActionIntent("basic", null, LinkEnvelope), nowTick: 252);
        Assert.Equal(CommitRefusal.None, soloB);
    }

    [Fact]
    public void Partial_acquire_never_leaves_a_held_slot()
    {
        var rig = new Rig(width: 1); // only one slot exists, but the reservation needs two
        rig.Add("a");
        rig.Add("b");
        rig.Add("c"); // holds the one slot before the rendezvous ever completes

        Assert.True(rig.Slots.TryAcquire("c", "left"));
        Assert.Equal(1, rig.Slots.Held);

        rig.Lane.Open("link:3", new[] { "a", "b" }, "a", "left", LinkEnvelope, targetKey: null, nowTick: 0, timeoutTicks: 500);
        var outcome = rig.Lane.TryJoin("link:3", "b", nowTick: 5);

        Assert.Equal(RendezvousOutcome.NoSlot, outcome);
        Assert.Equal(1, rig.Slots.Held);              // still only "c" — the rollback did not touch it
        Assert.False(rig.Slots.Holds("a"));            // "a" acquired then rolled back
        Assert.False(rig.Slots.Holds("b"));             // "b" never acquired at all
        Assert.Equal(TurnState.Charging, rig.Actor("a").State); // never transitioned — nothing was committed
        Assert.Equal(0, rig.Queue.Count);               // no resolve was scheduled for a failed reservation
    }

    [Fact]
    public void Opening_with_fewer_than_two_participants_is_rejected()
    {
        var rig = new Rig();
        rig.Add("a");
        Assert.Throws<ArgumentException>(() =>
            rig.Lane.Open("link:4", new[] { "a" }, "a", "left", LinkEnvelope, null, 0, 100));
    }

    [Fact]
    public void An_unbounded_timeout_is_rejected()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("b");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            rig.Lane.Open("link:5", new[] { "a", "b" }, "a", "left", LinkEnvelope, null, 0, timeoutTicks: 0));
    }

    [Fact]
    public void Reopening_the_same_reservation_id_throws()
    {
        var rig = new Rig();
        rig.Add("a"); rig.Add("b"); rig.Add("c"); rig.Add("d");
        rig.Lane.Open("link:6", new[] { "a", "b" }, "a", "left", LinkEnvelope, null, 0, 100);
        Assert.Throws<InvalidOperationException>(() =>
            rig.Lane.Open("link:6", new[] { "c", "d" }, "c", "left", LinkEnvelope, null, 0, 100));
    }

    [Fact]
    public void Joining_with_a_key_outside_the_reservation_throws()
    {
        var rig = new Rig();
        rig.Add("a"); rig.Add("b"); rig.Add("stranger");
        rig.Lane.Open("link:7", new[] { "a", "b" }, "a", "left", LinkEnvelope, null, 0, 100);
        Assert.Throws<ArgumentException>(() => rig.Lane.TryJoin("link:7", "stranger", 5));
    }
}
