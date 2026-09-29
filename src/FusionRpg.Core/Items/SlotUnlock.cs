namespace FusionRpg.Core.Items;

/// <summary>What a future unlock rule (a breakthrough system, a quest gate) needs to know about the
/// actor asking. Minimal on purpose — D2 ships the predicate, not a rule, so this only carries what
/// a real rule would need to exist at all.</summary>
public readonly record struct ActorContext(string ActorId, int Level);

/// <summary>One future unlock rule. Not implemented by anything today — the interface exists so a
/// later breakthrough/quest system is a rule, not a schema migration.</summary>
public interface ISlotUnlockRule
{
    bool Evaluate(ItemRole role, ActorContext actor);
}

/// <summary>
/// D2: every slot is open from the start, but the gate exists and defaults to open, so a later
/// breakthrough or quest system can close slots without a schema migration or a content re-author.
/// <c>ssot-equip-slots.md</c> §8.2 names the unlock ladder as the only mitigation for "gearing a new
/// specimen is a chore" — this predicate's existence is what makes turning it back on reversible.
///
/// <para><b>Do not hard-code fifteen-always-open.</b> The requirement is the predicate, not the
/// outcome — a caller that special-cased "always true" here would have shipped D2's outcome without
/// its reversibility.</para>
/// </summary>
public sealed class SlotUnlock
{
    readonly ISlotUnlockRule? _rule;

    public SlotUnlock(ISlotUnlockRule? rule = null) => _rule = rule;

    public bool IsUnlocked(ItemRole role, ActorContext actor) =>
        _rule is null || _rule.Evaluate(role, actor);   // no rule configured => open
}
