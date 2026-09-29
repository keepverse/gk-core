using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// T9 (action-todo.md, spec-usability-conditions.md). Six gates, cheapest first, short-circuiting:
/// stance → bound → cooldown → afford → range → condition. Every refusal is typed and names its own
/// gate — an action simultaneously on cooldown and unaffordable reports `OnCooldown`, proving order.
///
/// <para>Allocation-free: <see cref="FactReader"/> is a struct and is only touched by gate 5, so an
/// earlier refusal leaves <see cref="FactReader.Reads"/> at zero — the short-circuit is measurable,
/// not merely argued.</para>
/// </summary>
public static class UsabilityEvaluator
{
    public static UsabilityResult Evaluate(
        string actorKey,
        ActionRow action,
        bool actorHoldsAction,
        long nowTick,
        CooldownLedger cooldownLedger,
        IStanceCheck stanceCheck,
        IAffordabilityCheck affordability,
        GridPos? casterPos,
        GridPos? targetPos,
        ICompiledPredicate condition,
        ref FactReader facts) =>
        Evaluate(actorKey, action.ActionId, action.Envelope, action.MinRange, action.MaxRange,
            actorHoldsAction, nowTick, cooldownLedger, stanceCheck, affordability, casterPos, targetPos, condition, ref facts);

    /// <summary>Same six gates, over the four scalar fields actually read — added for T34's
    /// <see cref="StubIntentSource"/>, which reads already-compiled <see cref="CompiledAction"/>
    /// (T30) rather than a raw <see cref="ActionRow"/>. The <see cref="ActionRow"/> overload above is
    /// unchanged and now simply forwards here, so no existing caller or test moved.</summary>
    public static UsabilityResult Evaluate(
        string actorKey,
        string actionId,
        ActionEnvelope envelope,
        int minRange,
        int maxRange,
        bool actorHoldsAction,
        long nowTick,
        CooldownLedger cooldownLedger,
        IStanceCheck stanceCheck,
        IAffordabilityCheck affordability,
        GridPos? casterPos,
        GridPos? targetPos,
        ICompiledPredicate condition,
        ref FactReader facts)
    {
        // gate 0: stance — per-actor, so A7 hoists it out of both the action loop and the target
        // loop. No exemption list: guard-while-moving passes by being the release, not by bypass.
        var stance = stanceCheck.Check(actorKey, actionId);
        if (stance is { } stanceRefusal) return stanceRefusal;

        // gate 1: enabled / bound — a dictionary hit
        if (!actorHoldsAction)
            return UsabilityResult.Refuse(UsabilityReason.NotBound);

        // gate 2: cooldown — one lookup
        if (!cooldownLedger.IsReady(actorKey, envelope, nowTick))
            return UsabilityResult.Refuse(UsabilityReason.OnCooldown);

        // gate 3: affordability — a seam until A3
        var afford = affordability.Check(actorKey, actionId);
        if (!afford.IsUsable) return afford;

        // gate 4: range — O(1) Chebyshev, or pass with no board (spec-targeting.md §4)
        if (casterPos is { } cp && targetPos is { } tp)
        {
            var distance = GridDistance.Chebyshev(cp, tp);
            if (distance < minRange)
                return UsabilityResult.Refuse(UsabilityReason.TooClose);
            if (distance > maxRange)
                return UsabilityResult.Refuse(UsabilityReason.OutOfRange);
        }

        // gate 5: condition — the compiled predicate, ≤16 nodes
        if (!condition.Evaluate(ref facts))
            return UsabilityResult.Refuse(UsabilityReason.ConditionFailed);

        return UsabilityResult.Usable;
    }
}
