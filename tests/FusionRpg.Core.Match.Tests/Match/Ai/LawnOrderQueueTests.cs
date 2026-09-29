using System;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §3/§6/§7) —
/// the lawn's order queue. Spec test rows 1, 2, 7, 8, 10, 11, 12, 13 and the queue half of 15.
///
/// <para>Rows 3, 4 and 5 (the durable-identity refusals) now HAVE their transport shape: the two additive
/// <c>DirectOrder.SubjectId</c>/<c>ScopeId</c> fields landed 2026-09-23 (lane `cai2`), so the rules are
/// fed from the ORDER rather than from loose arguments threaded beside it — see
/// <see cref="An_order_carries_its_durable_identity_through_the_queue_and_the_rules_decide_on_it"/>.
/// Row 14 (an order as a top-rank candidate) is still owed: it needs a forced-intent hook on
/// <c>IIntentSource</c>, which no implementation exposes (seven implementations would move), and the
/// injector host the queue is offered from. See `tasks/reports/CAI4.9.md`.</para>
/// </summary>
public class LawnOrderQueueTests
{
    const long Lifetime = 80; // lawn ticks — the tuning seed `lawn.order.lifetimeTicks`

    static DirectOrder Order(string actorKey, string actionId = "act.skill", long issuedTick = 100) =>
        new(actorKey, actionId, TargetKey: "ptr.target", IssuedTick: issuedTick);

    /// <summary>
    /// Spec rows 3, 4 and 5's SUPPLY, and the reason the two fields exist: the queue hands back the
    /// order the caller offered, durable identity included, and the admission rules decide on what came
    /// back rather than on arguments the caller had to keep in step. The planted violation is the
    /// second `CheckScope` line — an order from another run must be <see cref="DirectOrderRefusal.StaleRun"/>,
    /// or a previous match's order would command a creature in the next one.
    /// </summary>
    [Fact]
    public void An_order_carries_its_durable_identity_through_the_queue_and_the_rules_decide_on_it()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", new DirectOrder(
            "ptr.a", "act.skill", TargetKey: "ptr.target", IssuedTick: 100,
            SubjectId: "inst.7", ScopeId: "match.7"));

        Assert.True(queue.TryPeek("ptr.a", out var peeked));
        Assert.Equal("inst.7", peeked.SubjectId);
        Assert.Equal("match.7", peeked.ScopeId);

        // Row 3: the order's own scope is the live run -> admitted; any other run -> StaleRun.
        Assert.Equal(DirectOrderRefusal.None, DirectOrderAdmission.CheckScope(peeked.ScopeId!, "match.7"));
        Assert.Equal(DirectOrderRefusal.StaleRun, DirectOrderAdmission.CheckScope(peeked.ScopeId!, "match.8"));

        // Rows 4 and 5: the durable subject the caller resolved, checked against the order's ptr.
        Assert.Equal(DirectOrderRefusal.None, DirectOrderAdmission.CheckSubject(
            new OrderSubject(Ptr: peeked.ActorKey, IsBound: true, IsLive: true), peeked.ActorKey));
        Assert.Equal(DirectOrderRefusal.SubjectMoved, DirectOrderAdmission.CheckSubject(
            new OrderSubject(Ptr: "ptr.reused", IsBound: true, IsLive: true), peeked.ActorKey));
        Assert.Equal(DirectOrderRefusal.SubjectGone, DirectOrderAdmission.CheckSubject(
            default, peeked.ActorKey));
    }

    /// <summary>The two fields are additive: an order that carries neither is byte-identical to the one
    /// this module shipped, so every existing construction and the whole queue keep working.</summary>
    [Fact]
    public void An_order_without_the_durable_identity_is_still_the_shipped_order()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a"));

        Assert.True(queue.TryPeek("ptr.a", out var peeked));
        Assert.Null(peeked.SubjectId);
        Assert.Null(peeked.ScopeId);
        Assert.Equal(Order("ptr.a"), peeked);
    }

    [Fact]
    public void An_admitted_order_is_returned_for_its_own_actor_and_for_no_other()
    {
        var queue = new LawnOrderQueue();
        var order = Order("ptr.a");

        var result = queue.Offer("ptr.a", order);

        Assert.True(result.Accepted);
        Assert.False(result.Superseded);
        Assert.Equal(DirectOrderRefusal.None, result.Refusal);
        Assert.True(queue.TryPeek("ptr.a", out var peeked));
        Assert.Equal(order, peeked);
        Assert.False(queue.TryPeek("ptr.b", out _));
    }

    [Fact]
    public void A_second_order_for_the_same_actor_replaces_the_first_and_reports_it()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a", "act.first"));
        var replacement = Order("ptr.a", "act.second");

        var result = queue.Offer("ptr.a", replacement);

        Assert.True(result.Accepted);
        Assert.True(result.Superseded); // the host reports this as `lawn.order.superseded`
        Assert.Equal(1, queue.Count);   // one live order per actor, never a queue of them
        Assert.True(queue.TryPeek("ptr.a", out var live));
        Assert.Equal("act.second", live.ActionId);
        // The replaced order is never returned again.
        Assert.NotEqual(Order("ptr.a", "act.first"), live);
    }

    [Fact]
    public void At_the_cap_a_new_order_is_refused_and_no_existing_order_is_evicted()
    {
        var queue = new LawnOrderQueue();
        for (var i = 0; i < LawnOrderQueue.Cap; i++) queue.Offer("ptr." + i.ToString("D2"), Order("ptr." + i.ToString("D2")));

        var refused = queue.Offer("ptr.extra", Order("ptr.extra"));

        Assert.False(refused.Accepted);
        Assert.Equal(DirectOrderRefusal.QueueFull, refused.Refusal);
        Assert.Equal(LawnOrderQueue.Cap, queue.Count);
        // Every existing order survives — the cross-actor failure this refuses.
        for (var i = 0; i < LawnOrderQueue.Cap; i++)
            Assert.True(queue.TryPeek("ptr." + i.ToString("D2"), out _));
        Assert.False(queue.TryPeek("ptr.extra", out _));

        // An order for an actor that ALREADY holds one is not a new key, so the cap does not refuse it.
        var replaced = queue.Offer("ptr.00", Order("ptr.00", "act.newer"));
        Assert.True(replaced.Accepted);
        Assert.True(replaced.Superseded);
    }

    [Fact]
    public void TryPeek_at_lifetime_minus_one_returns_the_order_and_at_lifetime_it_is_expired()
    {
        // The lifetime comparison is the REAL router's own rule (`IntentRouter.ConsumeExpiredOrder`,
        // `checked(nowTick - IssuedTick) >= orderTimeoutTicks`), so this test drives that seam rather
        // than re-implementing the comparison here — one implementation, no clock on the queue.
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a", issuedTick: 100));
        var router = new IntentRouter(
            policy: NoneIntentSource.Instance, orders: queue, orderTimeoutTicks: Lifetime);

        router.TryDeclare("ptr.a", 100 + Lifetime - 1);
        Assert.True(queue.TryPeek("ptr.a", out _));

        router.TryDeclare("ptr.a", 100 + Lifetime);
        Assert.False(queue.TryPeek("ptr.a", out _));
    }

    [Fact]
    public void A_retryable_gate_refusal_leaves_the_order_live_and_a_terminal_one_removes_it()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a"));

        // OnCooldown becomes true with time, so the order stays live and is offered on the next edge.
        Assert.False(queue.HandleGateRefusal("ptr.a", new UsabilityResult(UsabilityReason.OnCooldown, "act.skill")));
        Assert.True(queue.TryPeek("ptr.a", out _));

        // NotBound never does — retrying cannot help, so the slot disarms.
        Assert.True(queue.HandleGateRefusal("ptr.a", new UsabilityResult(UsabilityReason.NotBound)));
        Assert.False(queue.TryPeek("ptr.a", out _));
    }

    [Fact]
    public void The_same_reason_reported_twice_puts_one_report_on_the_wire_and_a_different_reason_a_second()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a"));

        Assert.True(queue.ShouldReport("ptr.a", new UsabilityResult(UsabilityReason.OnCooldown, "act.skill")));
        Assert.False(queue.ShouldReport("ptr.a", new UsabilityResult(UsabilityReason.OnCooldown, "act.skill")));
        Assert.True(queue.ShouldReport("ptr.a", new UsabilityResult(UsabilityReason.CannotAfford, "stamina")));
        // Back to a reason already reported, still inside this order's own budget.
        Assert.False(queue.ShouldReport("ptr.a", new UsabilityResult(UsabilityReason.OnCooldown, "act.skill")));

        // A NEW order starts its own budget: the same reason is reported again because it is a new order.
        queue.Offer("ptr.a", Order("ptr.a", "act.other"));
        Assert.True(queue.ShouldReport("ptr.a", new UsabilityResult(UsabilityReason.OnCooldown, "act.other")));
    }

    [Fact]
    public void Death_removes_the_order_and_a_reused_ptr_address_finds_nothing_in_both_orders()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a"));

        // death-then-spawn: the drop runs first, then the address is handed out again.
        Assert.True(queue.Remove("ptr.a"));
        Assert.False(queue.TryPeek("ptr.a", out _));
        Assert.False(queue.Remove("ptr.a"));
        Assert.False(queue.TryPeek("ptr.a", out _));

        // spawn-then-death: the stale entry is dropped by the same call.
        queue.Offer("ptr.a", Order("ptr.a", "act.stale"));
        Assert.True(queue.Remove("ptr.a"));
        Assert.False(queue.TryPeek("ptr.a", out _));
    }

    [Fact]
    public void The_board_edge_and_the_kill_switch_each_clear_every_order()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a"));
        queue.Offer("ptr.b", Order("ptr.b"));

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryPeek("ptr.a", out _));
        Assert.False(queue.TryPeek("ptr.b", out _));
    }

    [Fact]
    public void Commit_removes_the_order_and_a_second_commit_is_a_no_op()
    {
        var queue = new LawnOrderQueue();
        queue.Offer("ptr.a", Order("ptr.a"));

        queue.Commit("ptr.a");

        Assert.False(queue.TryPeek("ptr.a", out _));
        queue.Commit("ptr.a"); // idempotent: no resurrection, no negative count
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void The_structural_constants_are_the_values_their_rules_implement()
    {
        // Closed, code-owned structural constants — a memory bound, module 4's own one-live-order rule,
        // and an observability rate bound. None is a magnitude and none is a progression number.
        Assert.Equal(16, LawnOrderQueue.Cap);
        Assert.Equal(1, LawnOrderQueue.MaxLiveOrdersPerActor);
        Assert.Equal(1, LawnOrderQueue.ReportsPerOrderPerReason);
    }

    [Fact]
    public void Offering_an_empty_key_or_a_mismatched_order_is_a_caller_bug()
    {
        var queue = new LawnOrderQueue();

        Assert.Throws<ArgumentException>(() => queue.Offer("", Order("ptr.a")));
        Assert.Throws<ArgumentException>(() => queue.Offer("ptr.b", Order("ptr.a")));
    }
}
