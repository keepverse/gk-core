using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// T34 (spec-action-selection.md): the stub AI — "deliberately stupid, and honest about it." Pursue
/// the nearest live enemy, use the first usable action against them, move if out of reach, pass if
/// nothing works. No threat evaluation, no retreat, no kiting, no plan, no read of the power vector.
///
/// <para><b>Reads only `IBattleView`</b> (T33) — never a direct read of battle state. The seam is what
/// makes fog of war later a one-implementation swap instead of an AI rewrite.</para>
///
/// <para><b>Interpretation decided here, not left ambiguous</b>: spec §2 names "who?" (nearest) and
/// "with what?" (first usable action against THAT target) as two SEPARATE, sequential steps — never
/// "try the next-nearest target if the nearest one has nothing usable." Exactly ONE target is ever
/// examined per decision. This is what makes "deliberately stupid" (§1) concrete rather than a slogan,
/// and it is what makes the "`FactReader.Reads` scales with targets, not actions × targets" acceptance
/// line trivially true rather than merely likely: `Reads` per decision is bounded by this actor's OWN
/// held-action count, independent of how many other enemies exist on the board at all — a stronger,
/// simpler property than "try targets nearest-first until one works" would give.</para>
///
/// <para><b>`HeldActionsOf` is expected to already be preference-ordered</b> — sorted once wherever an
/// actor's action set is frozen (T24's `FrozenActionSet`), not per decision. Sorting per call would be
/// the exact per-decision allocation this module's own zero-allocation acceptance line forbids.</para>
/// </summary>
public sealed class StubIntentSource : IIntentSource
{
    readonly IBattleView _view;
    readonly CooldownLedger _cooldowns;
    readonly IStanceCheck _stance;
    readonly IAffordabilityCheck _affordability;

    public StubIntentSource(IBattleView view, CooldownLedger cooldowns, IStanceCheck stance, IAffordabilityCheck affordability)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _cooldowns = cooldowns ?? throw new ArgumentNullException(nameof(cooldowns));
        _stance = stance ?? throw new ArgumentNullException(nameof(stance));
        _affordability = affordability ?? throw new ArgumentNullException(nameof(affordability));
    }

    public ActionIntent TryDeclare(string actorKey, long nowTick)
    {
        var heldActions = _view.HeldActionsOf(actorKey);
        if (heldActions.Count == 0) return ActionIntent.None; // step 1: cannot act at all

        var targetKey = NearestEnemy(actorKey); // step 2: who -- purely geometric, touches no FactReader
        if (targetKey is null) return ActionIntent.None; // no live enemy exists at all

        var casterPos = _view.PositionOf(actorKey);
        var targetPos = _view.PositionOf(targetKey);
        var selfFacts = _view.FactsOf(actorKey);
        var targetFacts = _view.FactsOf(targetKey);

        // step 3: with what -- first usable action, in the actor's own preference order, against
        // the ONE chosen target. Gates 0-3 (stance/bound/cooldown/afford) never touch FactReader
        // (confirmed: only gate 5 calls FactReader.Pick), so hoisting them changes nothing about
        // Reads -- they are cheap regardless of where they run, and this loop already runs each
        // gate at most once per candidate action, never once per (action, target) pair times a
        // target count, since there is only ever the one target.
        // Indexed `for`, not `foreach`: iterating an interface-typed (`IReadOnlyList<T>`) collection
        // via `foreach` boxes its enumerator when the concrete type is not visible at the call site —
        // exactly the per-decision allocation this module's own acceptance line forbids.
        for (var i = 0; i < heldActions.Count; i++)
        {
            var action = heldActions[i];
            var facts = new FactReader(selfFacts, targetFacts);
            var result = UsabilityEvaluator.Evaluate(
                actorKey, action.ActionId, action.Envelope, action.MinRange, action.MaxRange,
                actorHoldsAction: true, nowTick, _cooldowns, _stance, _affordability,
                casterPos, targetPos, action.Condition, ref facts);

            if (result.IsUsable)
                return new ActionIntent(action.ActionId, targetKey, action.Envelope);
        }

        // step 4: can't reach with anything -- move toward them if any held action is tagged
        // Movement and clears its own (non-range) gates. Inert until a real board exists (spec §2
        // "written against a synthetic board" — MinRange/MaxRange on a movement action do not yet
        // mean "how far this actor can move," so range is deliberately NOT re-checked here).
        for (var i = 0; i < heldActions.Count; i++)
        {
            var action = heldActions[i];
            if (!Contains(action.Tags, ActionTag.Movement)) continue;

            var facts = new FactReader(selfFacts, targetFacts);
            var result = UsabilityEvaluator.Evaluate(
                actorKey, action.ActionId, action.Envelope, minRange: 0, maxRange: int.MaxValue,
                actorHoldsAction: true, nowTick, _cooldowns, _stance, _affordability,
                casterPos: null, targetPos: null, action.Condition, ref facts); // range gate short-circuited: moving is never refused for being "out of range" of itself

            if (result.IsUsable)
                return new ActionIntent(action.ActionId, targetKey, action.Envelope);
        }

        return ActionIntent.None; // step 5: pass -- a requirement, not a fallback
    }

    /// <summary>Step 2. Ties break on ordinal ptr, case-insensitive — the same convention
    /// <c>TargetResolver</c> already uses (spec §3: "the same tiebreak … already use"). With no board
    /// (<c>PositionOf</c> null), falls back to plain listed order — <c>SourceOrder</c>, the exact
    /// default the shipped engine's own target selection already uses with no board, which is what
    /// keeps this module golden-neutral until a board exists (spec §6).</summary>
    string? NearestEnemy(string actorKey)
    {
        var mySide = _view.SideOf(actorKey);
        var myPos = _view.PositionOf(actorKey);

        string? best = null;
        var bestDistance = int.MaxValue;
        var liveActorKeys = _view.LiveActorKeys;

        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var candidate = liveActorKeys[i];
            if (string.Equals(candidate, actorKey, StringComparison.Ordinal)) continue;
            if (_view.SideOf(candidate) == mySide) continue;

            if (myPos is null) return candidate; // no board: SourceOrder -- first enemy in list order, stop

            var distance = GridDistance.Chebyshev(myPos.Value, _view.PositionOf(candidate)!.Value);
            if (best is null || distance < bestDistance ||
                (distance == bestDistance && string.Compare(candidate, best, StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    static bool Contains(IReadOnlyList<ActionTag> tags, ActionTag tag)
    {
        for (var i = 0; i < tags.Count; i++)
            if (tags[i] == tag) return true;
        return false;
    }
}
