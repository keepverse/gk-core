using FusionRpg.Core.Battle;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Actions;

/// <summary>
/// combat-ai `intent-router` (module 4, CAI1.10, spec-intent-router.md): ONE router, every place. It
/// decides WHO answers <see cref="IIntentSource.TryDeclare"/> for an actor and WHAT view that answer
/// is computed against. It never scores, never gates and never resolves — those stay module 1/3's and
/// the engine's own.
///
/// <para><b>Replaces two identical routers.</b> `RaidIntentSource` (delve) and `SiegeIntentSource`
/// (siege, `Battle/Siege/SiegeAi.cs`, deleted by this same commit) were the SAME four lines — steered
/// key set → player source, everything else → the automated source — written twice. This is the one
/// place that logic lives now.</para>
///
/// <para><b>The chain, in order</b> (`TryDeclare`/`RetargetFor` alike):
/// <list type="number">
/// <item>a live direct order for this actor (§3) — structural this commit: <see cref="IOrderQueue"/>
/// is null for every caller CAI1.10 ships, so this step is always skipped in production today;
/// `commander-direct-orders` (module 20) is the first real producer.</item>
/// <item><c>steered</c> when <c>steeredKeys</c> is null (covers every actor) or contains the actor —
/// a SELECT, never a cascade: a steered actor that declares nothing is never handed to <c>policy</c>
/// (the two deleted routers' own proven contract, ported verbatim from their own tests).</item>
/// <item><c>policy</c> — the mode's profiled policy (module 1 + 3), or `state.DefaultAiIntentSource`'s
/// role today.</item>
/// <item><c>fallback</c> — tried only when <c>policy</c> declares nothing (<see cref="ActionIntent.IsNone"/>).
/// A null fallback means the chain has no step 4 at all: the router returns <see cref="ActionIntent.None"/>
/// rather than inventing one.</item>
/// </list></para>
/// </summary>
public sealed class IntentRouter : IIntentSource
{
    readonly IIntentSource _policy;
    readonly IIntentSource? _fallback;
    readonly IIntentSource? _steered;
    readonly IReadOnlySet<string>? _steeredKeys;
    readonly IReadOnlyList<ITraitDecorator>? _decorators;
    readonly IOrderQueue? _orders;
    readonly long _orderTimeoutTicks;
    readonly Func<DirectOrder, ActionIntent?>? _forcedIntent;
    readonly IAiDecisionSink? _sink;

    public IntentRouter(
        IIntentSource policy,
        IIntentSource? fallback = null,
        IIntentSource? steered = null,
        IReadOnlySet<string>? steeredKeys = null,
        IReadOnlyList<ITraitDecorator>? decorators = null,
        IOrderQueue? orders = null,
        long orderTimeoutTicks = 0,
        Func<DirectOrder, ActionIntent?>? forcedIntent = null,
        IAiDecisionSink? sink = null)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        // combat-ai `decision-inspector` CAI2.4: every arm records through the ONE sink the caller
        // supplied, and the wrap lives HERE — at construction — rather than in `Compose`, so the property
        // "every arm records" holds for EVERY construction path and not only for the one factory.
        // MEASURED, not reasoned: with the wrap left in `Compose` (the pre-move shape), the 24 existing
        // tests passed and only `A_directly_constructed_router_records_its_source_arms_too` failed — a
        // caller using this constructor directly with a sink got the ORDER arm recorded and the three
        // source arms SILENT. `Origin` distinguishes the arms, which is the field D4 needs the inspector to
        // READ rather than infer, and a null sink leaves every arm exactly as it was.
        if (sink is not null)
        {
            policy = new AiDecisionRecordingSource(policy, sink, AiDecisionOrigin.Policy);
            if (fallback is not null) fallback = new AiDecisionRecordingSource(fallback, sink, AiDecisionOrigin.Policy);
            if (steered is not null) steered = new AiDecisionRecordingSource(steered, sink, AiDecisionOrigin.Steered);
        }

        _policy = policy;
        _fallback = fallback;
        _steered = steered;
        _steeredKeys = steeredKeys;
        _decorators = decorators;
        _orders = orders;
        _orderTimeoutTicks = orderTimeoutTicks;
        _forcedIntent = forcedIntent;
        _sink = sink;
    }

    /// <summary>
    /// combat-ai `intent-router` §1a: the ONE construction entry point every caller uses. Never a
    /// second one — a caller that inverts <paramref name="steeredSourceFor"/>'s own wrap (building
    /// `steered` itself and passing it to the constructor directly) skips the exact ordering
    /// `InteractiveIntentSource`'s own `Record` call needs (the automated policy must exist BEFORE
    /// the interactive wrapper that takes it as its timeout fallback does) and breaks replay of an
    /// AFK turn.
    /// </summary>
    public static IntentRouter Compose(
        IIntentSource policy,
        IIntentSource? fallback = null,
        Func<IIntentSource, IIntentSource>? steeredSourceFor = null,
        IReadOnlySet<string>? steeredKeys = null,
        IReadOnlyList<ITraitDecorator>? decorators = null,
        IOrderQueue? orders = null,
        long orderTimeoutTicks = 0,
        IAiDecisionSink? sink = null,
        Func<DirectOrder, ActionIntent?>? forcedIntent = null)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        var steered = steeredSourceFor?.Invoke(policy);

        // The sink is forwarded, not used here: the per-arm wrap is the CONSTRUCTOR's job (see its own
        // comment), so every construction path records the same way and this factory stays an ordering
        // helper. The factory still hands the policy to `steeredSourceFor` UNWRAPPED, which is what that
        // parameter's own doc requires.
        return new IntentRouter(policy, fallback, steered, steeredKeys, decorators, orders, orderTimeoutTicks, forcedIntent, sink);
    }

    public ActionIntent TryDeclare(string actorKey, long nowTick) =>
        ApplyDecorators(actorKey, Resolve(actorKey, nowTick));

    /// <summary>
    /// combat-ai `intent-router` §1a: target-only re-query for an already-committed action —
    /// `ActionRunner.cs`'s own <c>TryReselect</c> contract. The committed envelope is fixed; only the
    /// target is open. For this commit, "the resolved source's ordinary target pick" is the SAME
    /// chain `TryDeclare` runs, read back through its own <see cref="ActionIntent.TargetKey"/> — byte-
    /// identical to what `TimelineDispatch.Reselect` returns today (`actionId`/`deadTargetKey` are
    /// unused for the identical reason today's `Reselect` never reads its own `deadTargetKey`
    /// parameter either; a real per-action-id target stage is module 1's `CoreIntentPolicy.RetargetFor`,
    /// reached once a wiring module supplies it as `policy`).
    ///
    /// <para><b>No post-decision redirect here, deliberately.</b> `TimelineDispatch.Reselect` has never
    /// applied the `loyal` bodyguard redirect (only the commit path did, inside `DeclareBasicAttack`),
    /// so applying <see cref="ITraitDecorator.EffectiveTargetOf"/> here would be a second, unannounced
    /// behaviour change bundled into CAI1.11's one cause. The redirect therefore stays on the
    /// <see cref="TryDeclare"/> path, which is exactly where the engine's old inline redirect sat.</para>
    /// </summary>
    public string? RetargetFor(string actorKey, string actionId, string? deadTargetKey, long nowTick)
    {
        var intent = Resolve(actorKey, nowTick);
        return intent.IsNone ? null : intent.TargetKey;
    }

    /// <summary>
    /// CAI1.11 (§2): the post-decision half of a trait decorator — "where the answer LANDS". The
    /// engine's old `BasicAttack.cs` bodyguard redirect now lives behind
    /// <see cref="ITraitDecorator.EffectiveTargetOf"/>, and this is the one place that function is
    /// called on the engine side, so the trait is declared once and applied once per side (the other
    /// side being the scorer's own `loyalRedirect` seam). Every decorator is offered the ORIGINAL
    /// target, never the previous decorator's answer, so a redirect of a redirect cannot chain.
    /// </summary>
    ActionIntent ApplyDecorators(string actorKey, ActionIntent intent)
    {
        if (_decorators is null || intent.IsNone || intent.TargetKey is null) return intent;

        var original = intent.TargetKey;
        for (var i = 0; i < _decorators.Count; i++)
        {
            var decorator = _decorators[i];
            if (!decorator.AppliesTo(actorKey)) continue;
            var effective = decorator.EffectiveTargetOf(actorKey, original);
            if (!string.Equals(effective, original, StringComparison.Ordinal))
                return intent with { TargetKey = effective };
        }

        return intent;
    }

    /// <summary>The one chain, in order: a live/expired order, then `steered`, then `policy`, then
    /// `fallback`. Both <see cref="TryDeclare"/> and <see cref="RetargetFor"/> resolve through this SAME
    /// method — the property that closes M5 (there is no second expression left to disagree with).</summary>
    ActionIntent Resolve(string actorKey, long nowTick)
    {
        ConsumeExpiredOrder(actorKey, nowTick);

        // combat-ai `commander-direct-orders` (module 20, CAI4.9 row 14): a LIVE order is a RANK-0
        // CANDIDATE, never a bypass. The hook owns the gates -- this router never runs them, so it cannot
        // answer "did the order clear them" -- and it returns the intent only when the order actually
        // did. Null (or None) means it did not, and the chain falls through to steered/policy/fallback
        // exactly as if no order existed: the spec's "the identical order that fails a gate does not fire
        // and the policy's own choice is returned". The step sits BEFORE steered because the chain's own
        // pinned order is "order-injected, then steered, then policy, then default". A null forcedIntent
        // leaves every caller exactly as it was -- today's inert state, where a live order falls through
        // because nothing can fire it.
        if (_orders is not null && _forcedIntent is not null && _orders.TryPeek(actorKey, out var liveOrder))
        {
            var forced = _forcedIntent(liveOrder);
            if (forced is { } forcedIntent && !forcedIntent.IsNone)
            {
                // CAI2.4's fourth arm (lane `cai3`, 2026-09-23): an ORDER-driven decision records through
                // the SAME sink, with `AiDecisionOrigin.Order`. The three source arms are wrapped in
                // `AiDecisionRecordingSource` at `Compose`, but this step returns BEFORE any of them runs,
                // so without this line an order-driven decision produced NO record at all — and
                // `AiDecisionOrigin.Order` was a closed-vocabulary member nothing could emit, which is
                // exactly what CAI4.9's criterion 7 depends on the inspector being able to read rather
                // than infer. Same record shape the decorator builds (the router knows the actor, the
                // tick, the arm and the intent; it knows no round and no candidate detail), through the
                // same builder, so there is still one place that shapes a router-level record.
                AiDecisionRecordingSource.RecordRouterDecision(_sink, actorKey, nowTick, AiDecisionOrigin.Order, forcedIntent);
                return forcedIntent;
            }
        }

        if (TrySteered(actorKey, nowTick, out var steeredIntent))
            return steeredIntent;

        var policyIntent = _policy.TryDeclare(actorKey, nowTick);
        if (!policyIntent.IsNone) return policyIntent;

        return _fallback is null ? ActionIntent.None : _fallback.TryDeclare(actorKey, nowTick);
    }

    /// <summary>`RaidIntentSource.KeysForParty`, moved verbatim (§1) — still built from
    /// <see cref="BattleSetup"/> BEFORE `Resolve` runs, never re-derived from a live `IBattleView`.
    /// </summary>
    public static IReadOnlySet<string> KeysForParty(BattleSetup setup, int partyIndex) =>
        setup.Squad.Where(a => a.PartyIndex == partyIndex).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

    bool TrySteered(string actorKey, long nowTick, out ActionIntent intent)
    {
        if (_steered is not null && (_steeredKeys is null || _steeredKeys.Contains(actorKey)))
        {
            intent = _steered.TryDeclare(actorKey, nowTick);
            return true;
        }
        intent = default;
        return false;
    }

    /// <summary>Step 1 (§3): a live order past its own timeout is expired and never fires — this is
    /// the one piece of order handling this module actually implements. Firing a LIVE (non-expired)
    /// order as a "rank-0 candidate" needs a way to hand a forced (action, target) pair into whichever
    /// `IIntentSource` step 2-4 resolves to, and no `IIntentSource` implementation exposes that hook
    /// today — that plumbing is `commander-direct-orders`' (module 20) own commit, named here rather
    /// than guessed at, not silently worked around. A non-expired live order therefore falls through
    /// to steps 2-4 exactly as if no order existed, which is inert today because `_orders` is null for
    /// every caller this module ships.</summary>
    void ConsumeExpiredOrder(string actorKey, long nowTick)
    {
        if (_orders is null || !_orders.TryPeek(actorKey, out var order)) return;
        if (checked(nowTick - order.IssuedTick) >= _orderTimeoutTicks)
            _orders.Expire(actorKey);
    }
}
