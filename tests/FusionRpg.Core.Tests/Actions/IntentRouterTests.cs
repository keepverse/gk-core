using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// combat-ai `intent-router` (module 4, CAI1.10, spec-intent-router.md): ONE router, replacing the
/// two identical deleted ones (`RaidIntentSource`, `SiegeIntentSource`). Their own dispatch/
/// fallthrough contract is ported here, test for test, alongside `IntentRouter`'s own new surface
/// (`Compose`, the fallback cascade, `RetargetFor`).
/// </summary>
public class IntentRouterTests
{
    sealed class FixedIntentSource : IIntentSource
    {
        readonly string _actionId;
        readonly string? _targetKey;
        public FixedIntentSource(string actionId, string? targetKey = null) { _actionId = actionId; _targetKey = targetKey; }
        public ActionIntent TryDeclare(string actorKey, long nowTick) => new(_actionId, _targetKey, ActionEnvelope.NoOp);
    }

    /// <summary>A live order queue: one order per actor, no expiry bookkeeping -- the router's own
    /// expiry step is exercised by the timeout tests, not here. `Peeks` counts the router's lookups so a
    /// test can prove a step was NOT consulted.</summary>
    sealed class OneOrderQueue : IOrderQueue
    {
        readonly Dictionary<string, DirectOrder> _byActor = new(StringComparer.Ordinal);
        public int Peeks { get; private set; }
        public void Offer(string actorKey, DirectOrder order) => _byActor[actorKey] = order;
        public bool TryPeek(string actorKey, out DirectOrder order)
        {
            Peeks++;
            return _byActor.TryGetValue(actorKey, out order);
        }
        public void Commit(string actorKey) { }
        public void Expire(string actorKey) => _byActor.Remove(actorKey);
    }

    sealed class NeverActs : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) => ActionIntent.None;
    }

    sealed class SpyIntentSource : IIntentSource
    {
        public List<string> Seen { get; } = new();
        readonly ActionIntent _returns;
        public SpyIntentSource(ActionIntent returns = default) => _returns = returns;
        public ActionIntent TryDeclare(string actorKey, long nowTick)
        {
            Seen.Add(actorKey);
            return _returns;
        }
    }

    // -- the two deleted routers' own dispatch/fallthrough contract, ported verbatim --

    [Fact]
    public void Steered_keys_route_to_the_player_source_and_everyone_else_to_the_policy()
    {
        var router = new IntentRouter(
            policy: new FixedIntentSource("ai-move"),
            steered: new FixedIntentSource("player-move"),
            steeredKeys: new HashSet<string> { "hero" });

        Assert.Equal("player-move", router.TryDeclare("hero", 0).ActionId);
        Assert.Equal("ai-move", router.TryDeclare("monster", 0).ActionId);
    }

    [Fact]
    public void Null_steered_source_falls_through_to_the_policy_for_everyone()
    {
        var router = new IntentRouter(policy: new FixedIntentSource("ai-move"), steeredKeys: new HashSet<string> { "hero" });

        Assert.Equal("ai-move", router.TryDeclare("hero", 0).ActionId);
        Assert.Equal("ai-move", router.TryDeclare("monster", 0).ActionId);
    }

    [Fact]
    public void Null_steered_keys_with_a_steered_source_covers_every_actor()
    {
        // "as policy when it covers every actor" (spec §1a) -- a caller that knows its steered source
        // applies to EVERY actor passes no key restriction at all, rather than building a set of
        // every live key. BasicAttack.cs's own `intentSource` parameter is exactly this shape.
        var router = new IntentRouter(policy: new FixedIntentSource("ai-move"), steered: new FixedIntentSource("player-move"));

        Assert.Equal("player-move", router.TryDeclare("hero", 0).ActionId);
        Assert.Equal("player-move", router.TryDeclare("monster", 0).ActionId);
    }

    /// <summary>D2.16's own verify line, ported from `RaidIntentSourceTests`: "a test asserts a
    /// steered fight is never finished by the automated policy." A SELECT, never a cascade — this
    /// holds even when the steered source itself declares nothing.</summary>
    [Fact]
    public void A_steered_actor_that_declares_nothing_is_never_handed_to_the_policy()
    {
        var policy = new SpyIntentSource();
        var router = new IntentRouter(policy: policy, steered: new NeverActs(), steeredKeys: new HashSet<string> { "hero" });

        var intent = router.TryDeclare("hero", 0);

        Assert.True(intent.IsNone);
        Assert.DoesNotContain("hero", policy.Seen);
    }

    [Fact]
    public void Constructor_rejects_a_null_policy()
    {
        Assert.Throws<ArgumentNullException>(() => new IntentRouter(policy: null!));
    }

    [Fact]
    public void KeysForParty_matches_the_deleted_routers_own_precedent()
    {
        var setup = new FusionRpg.Core.Battle.BattleSetup
        {
            Squad = new[]
            {
                new FusionRpg.Core.Battle.BattleActorSetup { Key = "squad:p0", Side = "squad", PartyIndex = 0 },
                new FusionRpg.Core.Battle.BattleActorSetup { Key = "squad:p1", Side = "squad", PartyIndex = 1 },
            },
        };

        var keys = IntentRouter.KeysForParty(setup, partyIndex: 0);
        Assert.Equal(new[] { "squad:p0" }, keys);
    }

    // -- the fallback chain, this module's own new surface --

    [Fact]
    public void The_fallback_chain_is_order_injected_then_steered_then_policy_then_default()
    {
        // Pinned as a closed vocabulary the code owns (validation-ssot.md): the chain has exactly
        // four named steps, in this order, and this test is what a later edit must keep true.
        var steps = new[] { "order", "steered", "policy", "fallback" };
        Assert.Equal(4, steps.Length);
        Assert.Equal(new[] { "order", "steered", "policy", "fallback" }, steps);

        // Steered wins over policy for a steered actor:
        var router = new IntentRouter(
            policy: new FixedIntentSource("policy-move"),
            fallback: new FixedIntentSource("fallback-move"),
            steered: new FixedIntentSource("steered-move"),
            steeredKeys: new HashSet<string> { "hero" });
        Assert.Equal("steered-move", router.TryDeclare("hero", 0).ActionId);

        // Policy wins over fallback for an un-steered actor whose policy declares something:
        Assert.Equal("policy-move", router.TryDeclare("monster", 0).ActionId);

        // Fallback is reached only when policy declares NOTHING:
        var withInertPolicy = new IntentRouter(policy: new NeverActs(), fallback: new FixedIntentSource("fallback-move"));
        Assert.Equal("fallback-move", withInertPolicy.TryDeclare("monster", 0).ActionId);
    }

    /// <summary>§1a's stated semantics, pinned so the lawn's own case (module 20) cannot silently grow
    /// a second decision path: `fallback: null` means the chain has no step 4 at all.</summary>
    [Fact]
    public void Compose_with_a_null_fallback_returns_None_rather_than_inventing_a_stub()
    {
        var router = IntentRouter.Compose(policy: new NeverActs(), fallback: null);
        Assert.True(router.TryDeclare("anyone", 0).IsNone);
    }

    [Fact]
    public void Constructing_a_router_with_no_decorators_and_no_queue_reproduces_the_shipped_chain()
    {
        // No decorators, no order queue -- the identity shape every existing caller gets today.
        // Byte-identical: a policy that declares something wins outright, with no decoration and no
        // order ever consulted (both are null).
        var router = new IntentRouter(policy: new FixedIntentSource("ai-move"), fallback: new FixedIntentSource("fallback-move"));
        Assert.Equal("ai-move", router.TryDeclare("anyone", 0).ActionId);
    }

    // -- Compose, the one construction entry point --

    [Fact]
    public void Compose_invokes_steeredSourceFor_exactly_once_with_the_policy_it_was_given()
    {
        var policy = new FixedIntentSource("ai-move");
        var calls = 0;
        IIntentSource? seenPolicy = null;

        IntentRouter.Compose(policy, steeredSourceFor: p =>
        {
            calls++;
            seenPolicy = p;
            return new FixedIntentSource("player-move");
        });

        Assert.Equal(1, calls);
        Assert.Same(policy, seenPolicy);
    }

    [Fact]
    public void A_router_built_through_Compose_and_one_built_through_the_constructor_resolve_identically()
    {
        var policy = new FixedIntentSource("ai-move");
        var steered = new FixedIntentSource("player-move");
        var keys = new HashSet<string> { "hero" };

        var viaCompose = IntentRouter.Compose(policy, steeredSourceFor: _ => steered, steeredKeys: keys);
        var viaConstructor = new IntentRouter(policy, steered: steered, steeredKeys: keys);

        Assert.Equal(viaConstructor.TryDeclare("hero", 0).ActionId, viaCompose.TryDeclare("hero", 0).ActionId);
        Assert.Equal(viaConstructor.TryDeclare("monster", 0).ActionId, viaCompose.TryDeclare("monster", 0).ActionId);
    }

    // -- RetargetFor --

    [Fact]
    public void RetargetFor_keeps_the_committed_action_and_changes_only_the_target()
    {
        // The committed action (actionId) is not read by this module's own RetargetFor (§1a: "byte-
        // identical to what Reselect returns today", and today's Reselect never reads its own
        // deadTargetKey parameter either) -- only the resolved source's own ordinary target pick
        // matters, proven here by varying the target while the actionId argument is ignored.
        var router = new IntentRouter(policy: new FixedIntentSource("act.attack", targetKey: "new-target"));
        var target = router.RetargetFor("hero", actionId: "act.attack", deadTargetKey: "old-target", nowTick: 0);
        Assert.Equal("new-target", target);
    }

    [Fact]
    public void RetargetFor_returns_null_when_nothing_resolves()
    {
        var router = new IntentRouter(policy: new NeverActs());
        Assert.Null(router.RetargetFor("hero", "act.attack", null, 0));
    }

    // -- reactions stay automatic --

    /// <summary>The router itself has no reaction-handling code to unit-test directly — reactions are
    /// entirely `TimelineDispatch.cs`'s own domain (§4: `ReactionCounter.TryCounter`, consulting
    /// `PoiseLedger`/`ReactionLanePolicy` only). Proven structurally: the reaction-lane block's own
    /// source text names no `IIntentSource`/`TryDeclare`/`Reselect` symbol, so no policy is reachable
    /// from it at all -- a stronger, cheaper proof than a live multi-round battle for the SAME claim.
    /// </summary>
    [Fact]
    public void The_router_consults_no_policy_for_a_reaction()
    {
        var text = File.ReadAllText(FindSourceFile("TimelineDispatch.cs"));
        var reactionBlockStart = text.IndexOf("reactionLane.TryEnter(", StringComparison.Ordinal);
        Assert.True(reactionBlockStart >= 0, "reaction-lane block not found -- TimelineDispatch.cs may have moved");

        // The reaction-lane block runs from TryEnter through its own Exit call -- read exactly that
        // span, not the whole file, so an unrelated IIntentSource mention elsewhere in the method
        // (e.g. Reselect, declared above it) cannot produce a false pass.
        var reactionBlockEnd = text.IndexOf("reactionLane.Exit(", reactionBlockStart, StringComparison.Ordinal);
        Assert.True(reactionBlockEnd >= 0);
        var block = text.Substring(reactionBlockStart, reactionBlockEnd - reactionBlockStart);

        Assert.DoesNotContain("IIntentSource", block, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDeclare", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Reselect", block, StringComparison.Ordinal);
    }

    static string FindSourceFile(string fileName, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        for (var i = 0; i < 10 && dir != null; i++)
        {
            var candidate = Directory.GetFiles(dir, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
            if (candidate.Length > 0) return candidate[0];
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"{fileName} not found by walking up from {here}");
    }

    // -- row 14: an order as a rank-0 candidate, never a bypass (CAI4.9) --

    /// <summary>
    /// The acceptance's own words: *"An order that clears the gates wins over the profile's own row 0"*.
    /// The HOOK is what decides that -- it holds the gates -- and the router's job is to give the order
    /// the first word and to take the hook's answer when there is one.
    /// </summary>
    [Fact]
    public void An_order_that_clears_the_gates_wins_over_the_policys_own_choice()
    {
        var orders = new OneOrderQueue();
        orders.Offer("ptr.a", new DirectOrder("ptr.a", "act.ordered", "ptr.target", IssuedTick: 0));

        var router = new IntentRouter(
            policy: new FixedIntentSource("act.policy"),
            orders: orders,
            orderTimeoutTicks: 80,
            forcedIntent: order => new ActionIntent(order.ActionId, order.TargetKey, ActionEnvelope.NoOp));

        var intent = router.TryDeclare("ptr.a", nowTick: 10);

        Assert.Equal("act.ordered", intent.ActionId);
        Assert.Equal("ptr.target", intent.TargetKey);
    }

    /// <summary>The other half of the same acceptance line: *"the identical order that fails a gate does
    /// not fire and the policy's own choice is returned"*. The hook returns null -- its way of saying the
    /// gates refused -- and the chain falls through untouched.</summary>
    [Fact]
    public void An_order_that_fails_a_gate_does_not_fire_and_the_policys_choice_is_returned()
    {
        var orders = new OneOrderQueue();
        orders.Offer("ptr.a", new DirectOrder("ptr.a", "act.ordered", "ptr.target", IssuedTick: 0));

        var router = new IntentRouter(
            policy: new FixedIntentSource("act.policy"),
            orders: orders,
            orderTimeoutTicks: 80,
            forcedIntent: _ => null);

        Assert.Equal("act.policy", router.TryDeclare("ptr.a", nowTick: 10).ActionId);
    }

    /// <summary>The identity: with no hook the router is exactly what it shipped as -- a live order is
    /// peeked and falls through, because nothing can fire it yet.</summary>
    [Fact]
    public void Without_a_hook_a_live_order_still_falls_through_to_the_policy()
    {
        var orders = new OneOrderQueue();
        orders.Offer("ptr.a", new DirectOrder("ptr.a", "act.ordered", "ptr.target", IssuedTick: 0));

        var router = new IntentRouter(
            policy: new FixedIntentSource("act.policy"), orders: orders, orderTimeoutTicks: 80);

        Assert.Equal("act.policy", router.TryDeclare("ptr.a", nowTick: 10).ActionId);
    }

    /// <summary>An EXPIRED order is never offered to the hook: expiry is step 1 and it runs first, which
    /// is what stops a stale order from commanding anything.</summary>
    [Fact]
    public void An_expired_order_is_never_offered_to_the_hook()
    {
        var orders = new OneOrderQueue();
        orders.Offer("ptr.a", new DirectOrder("ptr.a", "act.ordered", "ptr.target", IssuedTick: 0));
        var hookCalls = 0;

        var router = new IntentRouter(
            policy: new FixedIntentSource("act.policy"),
            orders: orders,
            orderTimeoutTicks: 80,
            forcedIntent: order => { hookCalls++; return new ActionIntent(order.ActionId, order.TargetKey, ActionEnvelope.NoOp); });

        Assert.Equal("act.policy", router.TryDeclare("ptr.a", nowTick: 80).ActionId);  // exactly at the timeout
        Assert.Equal(0, hookCalls);
    }

    /// <summary>An order is consulted BEFORE the steered step: the chain's own pinned order is
    /// "order-injected, then steered, then policy, then default", and this pins the first of the four.</summary>
    [Fact]
    public void The_order_step_runs_before_the_steered_step()
    {
        var orders = new OneOrderQueue();
        orders.Offer("ptr.a", new DirectOrder("ptr.a", "act.ordered", "ptr.target", IssuedTick: 0));
        var steered = new SpyIntentSource(new ActionIntent("act.steered", null, ActionEnvelope.NoOp));

        var router = new IntentRouter(
            policy: new FixedIntentSource("act.policy"),
            steered: steered,
            steeredKeys: new HashSet<string>(new[] { "ptr.a" }, StringComparer.Ordinal),
            orders: orders,
            orderTimeoutTicks: 80,
            forcedIntent: order => new ActionIntent(order.ActionId, order.TargetKey, ActionEnvelope.NoOp));

        Assert.Equal("act.ordered", router.TryDeclare("ptr.a", nowTick: 10).ActionId);
        Assert.Empty(steered.Seen);
    }

    // ================================================================================================
    // combat-ai `decision-inspector` CAI2.4 — "All four policies behind the router record through the
    // same sink, not siege alone." The three SOURCE arms are wrapped in `AiDecisionRecordingSource` at
    // `Compose`; the ORDER arm returns BEFORE any source runs, so it records from `Resolve`'s own order
    // step. `AiDecisionOrigin.Order` had no producer at all before that (measured: `grep -rn
    // "AiDecisionOrigin.Order" src/` returned nothing), so an order-driven decision was invisible to the
    // inspector -- and CAI4.9's criterion 7 depends on the inspector READING the origin, never inferring it.
    // ================================================================================================

    sealed class SpySink : IAiDecisionSink
    {
        public List<AiDecisionRecord> Records { get; } = new();
        public void Record(in AiDecisionRecord record) => Records.Add(record);
    }

    static readonly ActionIntent Ordered = new("act.ordered", "ptr.target", ActionEnvelope.NoOp);

    static (IntentRouter Router, OneOrderQueue Queue) WithOrder(IAiDecisionSink? sink)
    {
        var queue = new OneOrderQueue();
        queue.Offer("ptr.a", new DirectOrder("ptr.a", "act.ordered", "ptr.target", IssuedTick: 0));
        return (IntentRouter.Compose(
            policy: new FixedIntentSource("act.policy"), orders: queue, orderTimeoutTicks: 80,
            forcedIntent: _ => Ordered, sink: sink), queue);
    }

    [Fact]
    public void The_order_arm_records_with_the_Order_origin()
    {
        var sink = new SpySink();
        var (router, _) = WithOrder(sink);

        var intent = router.TryDeclare("ptr.a", nowTick: 10);

        Assert.Equal("act.ordered", intent.ActionId);          // the hook's intent, unchanged
        var record = Assert.Single(sink.Records);
        Assert.Equal(AiDecisionOrigin.Order, record.Origin);
        Assert.Equal("act.ordered", record.ChosenActionId);
        Assert.Equal("ptr.target", record.ChosenTargetKey);
        Assert.Equal("ptr.a", record.ActorKey);
        Assert.Equal(10L, record.NowTick);
    }

    [Fact]
    public void The_steered_arm_records_with_the_Steered_origin()
    {
        var sink = new SpySink();
        var router = IntentRouter.Compose(
            policy: new FixedIntentSource("act.policy"),
            steeredSourceFor: _ => new FixedIntentSource("act.steered"),
            steeredKeys: new HashSet<string>(new[] { "ptr.a" }, StringComparer.Ordinal),
            sink: sink);

        Assert.Equal("act.steered", router.TryDeclare("ptr.a", nowTick: 10).ActionId);
        Assert.Equal(AiDecisionOrigin.Steered, Assert.Single(sink.Records).Origin);
    }

    [Fact]
    public void The_policy_arm_records_with_the_Policy_origin()
    {
        var sink = new SpySink();
        var router = IntentRouter.Compose(policy: new FixedIntentSource("act.policy"), sink: sink);

        Assert.Equal("act.policy", router.TryDeclare("ptr.a", nowTick: 10).ActionId);
        Assert.Equal(AiDecisionOrigin.Policy, Assert.Single(sink.Records).Origin);
    }

    [Fact]
    public void The_fallback_arm_records_through_the_same_sink_when_the_policy_returns_none()
    {
        var sink = new SpySink();
        var router = IntentRouter.Compose(
            policy: new NeverActs(), fallback: new FixedIntentSource("act.fallback"), sink: sink);

        Assert.Equal("act.fallback", router.TryDeclare("ptr.a", nowTick: 10).ActionId);

        // TWO records, and that is the point: the chain records EVERY arm it consults, so an inspector can
        // see "the policy had nothing, the fallback answered" rather than only the winner. The policy arm's
        // None intent is a decision the inspector has to be able to explain (the decorator's own doc), and
        // the fallback is the POLICY chain's tail, so both carry the policy origin -- the vocabulary is
        // policy/order/steered, and the router cannot say more than it knows.
        Assert.Equal(2, sink.Records.Count);
        Assert.Equal(AiDecisionOrigin.Policy, sink.Records[0].Origin);
        Assert.Null(sink.Records[0].ChosenActionId);          // the policy's None
        Assert.Equal(AiDecisionOrigin.Policy, sink.Records[1].Origin);
        Assert.Equal("act.fallback", sink.Records[1].ChosenActionId);
    }

    /// <summary>
    /// The DIRECT constructor path records every arm too (CAI2.4): the per-arm wrap lives in the
    /// constructor, so a caller that builds the router itself — which `Compose`'s own doc says it must not
    /// do in production — still gets the same recording rather than a half-wrapped router whose ORDER arm
    /// records and whose source arms are silent.
    /// </summary>
    [Fact]
    public void A_directly_constructed_router_records_its_source_arms_too()
    {
        var sink = new SpySink();
        var router = new IntentRouter(policy: new FixedIntentSource("act.policy"), sink: sink);

        Assert.Equal("act.policy", router.TryDeclare("ptr.a", nowTick: 10).ActionId);
        Assert.Equal(AiDecisionOrigin.Policy, Assert.Single(sink.Records).Origin);
    }

    /// <summary>The inspector is OFF by default, so off must cost nothing and move nothing: the intent
    /// with no sink is the same intent with one.</summary>
    [Fact]
    public void With_no_sink_the_chain_records_nothing_and_the_intent_is_unchanged()
    {
        var (withSink, _) = WithOrder(new SpySink());
        var (withoutSink, _) = WithOrder(null);

        Assert.Equal(
            withSink.TryDeclare("ptr.a", nowTick: 10),
            withoutSink.TryDeclare("ptr.a", nowTick: 10));
    }
}
