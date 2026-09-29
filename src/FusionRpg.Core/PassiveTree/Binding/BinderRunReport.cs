namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>A whole binder run's outcome — never a third, ambiguous value (task D2, §7.2 item 3:
/// "the run's verdict is `FAIL`, not `NOT_MEASURED`" — `setgen/verdict.py:83-96`'s "a held partition
/// alone denies a pass" rule, ported to this module's own vocabulary).</summary>
public enum RunVerdict { Pass, Fail }

/// <summary>
/// One node's bind-time refusal, reported rather than absorbed (§7.2 item 2). A conversion node
/// (§7.1/§7.2) is the archetypal case this spec names, but the shape is generic — any
/// <see cref="BindRefusal"/> the orchestrator catches while binding a node lands here the same way,
/// because a refusal that reports nothing is indistinguishable from a tree that quietly ships under
/// its own budget (the description this task's spec section opens with).
///
/// <para><see cref="UnspentBudgetShareMilli"/> is the refused node's own `budgetShareMilli` —
/// per-mille of ONE BRANCH (R5's denominator), the same unit the plan emitted it in and the same
/// unit `potency.maxNodeShareMilli` is compared against. Never re-expressed in any other unit.</para>
///
/// <para><see cref="DeliberateHole"/> mirrors <see cref="BindInputNode.DeliberateHole"/> so a reader
/// of the report never has to cross-reference back to the input to see why this refusal did or did
/// not trip the run's <see cref="RunVerdict"/>.</para>
/// </summary>
public sealed record RefusedSlot(string NodeId, string Reason, long UnspentBudgetShareMilli, bool DeliberateHole);

/// <summary>
/// The whole run's report (task D2). <see cref="Bound"/> holds every node that priced cleanly;
/// <see cref="Refused"/> holds every node that did not, each naming its own unspent budget share.
/// Nothing is ever dropped silently — a node is in exactly one of the two lists.
///
/// <para><b>W3 (battle-derived-wire T2, 2026-09-23): a `Bound` node on a `combat.reflect.*` channel IS
/// now read in battle.</b> This note previously recorded the opposite — `TryReflect` had exactly one
/// caller (`DispatchInstant`) and nothing under `gk-core/src/FusionRpg.Core/Battle/` reached it, so a reflect
/// node's zero in a battle/sim sweep was a MISSING READER, not a weak price. That reader exists now:
/// `BattleRunState.ApplyHp` (the direct HP path — basic attack, guardian share, DoT pulse) calls the
/// SAME `TryReflect` the effect path uses, so the §6 M2 caveat is retired and a battle measurement of a
/// reflect node is a real reading. `ReflectHasNoBattlePathTests` pins the new shape against the real
/// source (battle calls `TryReflect`; no Battle file calls `DispatchInstant` directly).</para>
/// </summary>
public sealed record BinderRunReport(
    IReadOnlyList<BoundNode> Bound,
    IReadOnlyList<RefusedSlot> Refused,
    RunVerdict Verdict)
{
    /// <summary>The sum of every refused slot's unspent share, reported so a run's total shortfall
    /// is never something a reader has to add up by hand.</summary>
    public long TotalUnspentBudgetShareMilli => Refused.Sum(r => r.UnspentBudgetShareMilli);

    /// <summary>
    /// Builds the report and derives <see cref="RunVerdict"/> from it: `Fail` the moment any
    /// refusal is NOT flagged <see cref="RefusedSlot.DeliberateHole"/> — one held partition denies a
    /// pass (§7.2 item 3). A run whose only refusals are all deliberate holes still names every
    /// unspent share (nothing is absorbed) but is `Pass` — that is the entire point of the flag
    /// (§7.2's "consequence for tree-plan").
    /// </summary>
    public static BinderRunReport From(IReadOnlyList<BoundNode> bound, IReadOnlyList<RefusedSlot> refused)
    {
        var verdict = refused.Any(r => !r.DeliberateHole) ? RunVerdict.Fail : RunVerdict.Pass;
        return new BinderRunReport(bound, refused, verdict);
    }
}
