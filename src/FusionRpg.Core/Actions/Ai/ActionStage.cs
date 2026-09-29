using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>Which of the three waste guards fired — for a refusal trace/diagnostic only; never a
/// second gate-order contract (the gates are fixed: `UsabilityEvaluator`'s six, then resolvable-here,
/// then these three).</summary>
public enum WasteGuardKind { MinTargetsForArea = 0, TargetNotAboutToDie = 1, FightNotAboutToEnd = 2 }

/// <summary>
/// combat-ai `core-scorer` (module 1, spec-core-scorer.md §2's per-mille discipline): the three waste
/// guards' own thresholds, each with a documented "off" sentinel so the identity case needs no branch
/// of its own. Module 2 (`profile-schema`) authors these per profile; this module only defines the
/// shape and the "off" contract.
/// </summary>
public readonly record struct WasteGuardThresholds(
    int MinTargetsForArea,    // <= 0 => the area-target-count guard never refuses
    int KillMarginMilli,      // < 0  => the low-target-HP guard never refuses
    int FightEndingLiveCount) // < 0  => the ending-fight guard never refuses
{
    /// <summary>Every guard off — the identity at migration (`SiegeAiIntentSource` reads this today).</summary>
    public static readonly WasteGuardThresholds Off = new(0, -1, -1);
}

/// <summary>
/// combat-ai `core-scorer` (module 1, spec-core-scorer.md §5): one pass over held actions, cheapest
/// gate first, tier-gated at the last step. Walks the caller-supplied
/// <paramref name="heldActionsInPreferenceOrder"/> — already frozen and sorted by
/// `ActionTagPreference.Compare` wherever an actor's action set is compiled
/// (`Battle/BattleRunState.cs:582`) — NEVER re-sorted here, which is the exact per-decision allocation
/// `StubIntentSource.cs` already refuses. For each action:
///
/// <list type="number">
/// <item><see cref="UsabilityEvaluator.Evaluate"/> — the six shipped gates, stance → bound → cooldown →
/// afford → range → condition, short-circuiting. Called, never re-implemented. Gate 3 (afford) is
/// whatever <see cref="IAffordabilityCheck"/> the caller supplies — the reserve floor
/// (<see cref="ReserveFloorAffordability"/>) is a DECORATOR on that same argument, not a separate step
/// here, keeping one cost authority.</item>
/// <item><b>Resolvable-here.</b> A <c>Func&lt;string, bool&gt;</c> seam this module declares and
/// `resolvable-here` (module 5) implements from each place's executor allowlist. The default delegate
/// admits everything — today's behaviour.</item>
/// <item><b>Waste guards</b> — only when <paramref name="runWasteGuards"/> is true. A closed set of
/// three, each a named predicate with a tunable threshold (module 2 owns the keys; the NAMES are
/// code). When the flag is false the whole step is skipped BY THE BRANCH, not by authoring the
/// thresholds at identity — each guard needs a live-count census, exactly the work the performance
/// tier exists not to do (`spec-ai-tiers-personality.md` §2). Authoring the thresholds off is a
/// DIFFERENT lever: it still pays for the census. The two live-count reads
/// (<paramref name="liveTargetsInAreaOf"/>, <paramref name="opposingSideLiveCountOf"/>) are invoked
/// ONLY when their own guard's threshold is live AND <paramref name="runWasteGuards"/> is true.</item>
/// </list>
///
/// The first action that clears every step wins. Which RANK ROW it must come from is module 2's
/// profile, walked by `AiRowSelector.TryPick` (`spec-profile-schema.md` §4) — this module receives no
/// profile type and does not re-implement that walk; a caller narrows
/// <paramref name="heldActionsInPreferenceOrder"/> (or supplies an <paramref name="actionFilter"/>
/// predicate) to express a row's own action filter. Declared here, rather than taking module 2's
/// `AiActionFilter` record directly, because module 1 depends on nothing and module 2 depends on
/// module 1 — a predicate seam keeps the arrow pointing the right way; the first caller that actually
/// HAS an `AiActionFilter` (module 3's `CoreIntentPolicy`, `ai-tiers-personality`) turns it into this
/// predicate once per decision, not per action.
/// </summary>
public static class ActionStage
{
    public static bool TryPick(
        string actorKey,
        long nowTick,
        IReadOnlyList<CompiledAction> heldActionsInPreferenceOrder,
        CooldownLedger cooldowns,
        IStanceCheck stance,
        IAffordabilityCheck affordability,
        GridPos? casterPos,
        GridPos? targetPos,
        EntityFacts selfFacts,
        EntityFacts targetFacts,
        out string actionId,
        Func<string, bool>? resolvableHere = null,
        bool runWasteGuards = true,
        WasteGuardThresholds guards = default,
        Func<CompiledAction, bool>? actionFilter = null,
        Func<CompiledAction, int>? liveTargetsInAreaOf = null,
        Func<int>? opposingSideLiveCountOf = null)
    {
        resolvableHere ??= _ => true;
        actionFilter ??= _ => true;

        for (var i = 0; i < heldActionsInPreferenceOrder.Count; i++)
        {
            var action = heldActionsInPreferenceOrder[i];
            if (!actionFilter(action)) continue;

            var facts = new FactReader(selfFacts, targetFacts);
            var result = UsabilityEvaluator.Evaluate(
                actorKey, action.ActionId, action.Envelope, action.MinRange, action.MaxRange,
                actorHoldsAction: true, nowTick, cooldowns, stance, affordability,
                casterPos, targetPos, action.Condition, ref facts);
            if (!result.IsUsable) continue;

            if (!resolvableHere(action.ActionId)) continue;

            if (runWasteGuards && !PassesWasteGuards(action, targetFacts, guards, liveTargetsInAreaOf, opposingSideLiveCountOf))
                continue;

            actionId = action.ActionId;
            return true;
        }

        actionId = "";
        return false;
    }

    static bool PassesWasteGuards(
        CompiledAction action, EntityFacts targetFacts, WasteGuardThresholds guards,
        Func<CompiledAction, int>? liveTargetsInAreaOf, Func<int>? opposingSideLiveCountOf)
    {
        // Guard 1: an area envelope needs at least N live targets in its area. Never fires without a
        // caller-supplied census -- "does this need an area census at all" is itself a wiring question
        // this module does not answer (no envelope-shape oracle exists in Core.Actions today).
        if (guards.MinTargetsForArea > 0 && liveTargetsInAreaOf is not null &&
            liveTargetsInAreaOf(action) < guards.MinTargetsForArea)
            return false;

        // Guard 2: refuse when the target's current HP is already at or below the margin -- the
        // "ultimate on the last dying enemy" complaint. No census read: targetFacts is already in hand.
        if (guards.KillMarginMilli >= 0 && targetFacts.HpMilli <= guards.KillMarginMilli)
            return false;

        // Guard 3: refuse a buff/heal when the opposing side's live count is at or below N.
        if (guards.FightEndingLiveCount >= 0 && opposingSideLiveCountOf is not null &&
            ContainsAny(action.Tags, ActionTag.Buff, ActionTag.Heal) &&
            opposingSideLiveCountOf() <= guards.FightEndingLiveCount)
            return false;

        return true;
    }

    static bool ContainsAny(IReadOnlyList<ActionTag> tags, ActionTag a, ActionTag b)
    {
        for (var i = 0; i < tags.Count; i++)
            if (tags[i] == a || tags[i] == b) return true;
        return false;
    }
}
