using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>T2b — concurrency width, deterministic contention, and release on every exit path.</summary>
public class ActionSlotsTests
{
    [Fact]
    public void Width_one_serializes()
    {
        var s = new ActionSlots(1);
        Assert.True(s.TryAcquire("a", "squad"));
        Assert.False(s.TryAcquire("b", "squad"));
        Assert.Equal(1, s.Held);
    }

    [Fact]
    public void Width_two_provably_overlaps()
    {
        // Proven by CONTRAST. Asserting only that W=1 serializes passes for any width when
        // actions are zero-length, which is how a vacuous test slips through.
        var s = new ActionSlots(2);
        Assert.True(s.TryAcquire("a", "squad"));
        Assert.True(s.TryAcquire("b", "squad"));
        Assert.False(s.TryAcquire("c", "squad"));
        Assert.Equal(2, s.Held);
    }

    [Fact]
    public void Per_side_scope_lets_one_actor_from_each_side_act()
    {
        var s = new ActionSlots(1, WScope.PerSide);
        Assert.True(s.TryAcquire("squad:0", "squad"));
        Assert.True(s.TryAcquire("wave:0", "wave"));    // a global W=1 could not express this
        Assert.False(s.TryAcquire("squad:1", "squad"));
    }

    [Fact]
    public void Releasing_frees_the_width_for_the_next_actor()
    {
        var s = new ActionSlots(1);
        Assert.True(s.TryAcquire("a", "squad"));
        Assert.True(s.Release("a"));
        Assert.True(s.TryAcquire("b", "squad"));
    }

    // NOTE — deliberately NOT a [Theory] over resolve/interrupt/fizzle/death/withdrawal.
    // An earlier draft ran five inline cases whose bodies were identical, which reads as
    // five-path coverage while exercising one code path five times: exactly the vacuous
    // parameterisation this project has already been bitten by. ActionSlots exposes a single
    // Release, so at THIS layer there is one behaviour to test. Per-exit-path coverage belongs
    // where the FSM actually drives the slots (B12/B14), and is tracked there.

    [Fact]
    public void Release_is_idempotent_and_cannot_corrupt_the_count()
    {
        var s = new ActionSlots(2);
        s.TryAcquire("a", "squad");
        Assert.True(s.Release("a"));
        Assert.False(s.Release("a"));   // double release is a no-op, not a negative count
        Assert.Equal(0, s.Held);
        Assert.True(s.TryAcquire("x", "squad"));
        Assert.True(s.TryAcquire("y", "squad"));
    }

    [Fact]
    public void An_actor_cannot_hold_two_slots()
    {
        var s = new ActionSlots(4);
        Assert.True(s.TryAcquire("a", "squad"));
        Assert.False(s.TryAcquire("a", "squad"));
        Assert.Equal(1, s.Held);
    }

    [Fact]
    public void Contention_order_is_ready_tick_then_sequence()
    {
        var contenders = new List<(string ActorKey, long ReadyTick, long Seq)>
        {
            ("late", 300, 0), ("tie-b", 100, 7), ("tie-a", 100, 3), ("early", 50, 9)
        };
        ActionSlots.SortContenders(contenders);

        Assert.Equal(new[] { "early", "tie-a", "tie-b", "late" }, contenders.Select(c => c.ActorKey));
    }

    [Fact]
    public void Width_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActionSlots(0));
    }
}

/// <summary>The intent seam — and the deadlock it exists to prevent.</summary>
public class IntentSourceTests
{
    sealed class NeverDeclares : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) => ActionIntent.None;
    }

    sealed class AlwaysAttacks : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) =>
            new("basic-attack", "wave:0", ActionEnvelope.NoOp with { ActionId = "basic-attack" });
    }

    [Fact]
    public void No_legal_intent_means_no_slot_is_taken()
    {
        // The front-door deadlock: an actor that reaches Ready, takes the only slot, and then has
        // nothing to schedule drains the queue and stops the clock forever.
        var slots = new ActionSlots(1);
        IIntentSource source = new NeverDeclares();

        var intent = source.TryDeclare("squad:0", 0);
        Assert.True(intent.IsNone);
        // kernel rule: pass, do not seat
        Assert.False(slots.Holds("squad:0"));
        Assert.True(slots.TryAcquire("other", "wave"), "the slot must still be available");
    }

    [Fact]
    public void A_declared_intent_carries_its_envelope()
    {
        var intent = new AlwaysAttacks().TryDeclare("squad:0", 0);
        Assert.False(intent.IsNone);
        Assert.Equal("basic-attack", intent.ActionId);
        Assert.Equal("basic-attack", intent.Envelope.ActionId);
    }
}
