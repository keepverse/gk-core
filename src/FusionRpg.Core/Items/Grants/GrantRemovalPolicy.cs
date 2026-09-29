using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Items.Grants;

/// <summary>What a grant removal does to an actor sitting in one <see cref="TurnState"/>.</summary>
public enum GrantRemovalEffect
{
    /// <summary>The action leaves the selectable set immediately. Nothing has been paid — the intent
    /// source simply stops offering it.</summary>
    Immediate = 0,

    /// <summary>The in-flight run completes: costs stay paid, resolve handles fire, cooldown starts.
    /// "Committing is what costs, not landing" — cancelling here needs a refund path that rule
    /// forbids.</summary>
    LetTheRunComplete,

    /// <summary>Applies at the actor's next transition back into <see cref="TurnState.Charging"/> —
    /// the only edge out of <see cref="TurnState.Recovering"/>.</summary>
    AtNextCharging,

    /// <summary>Recorded now, applied if the actor returns. <c>Downed → Charging</c> is legal, so a
    /// revive must not resurrect a removed grant.</summary>
    RecordedAndSurvivesRevive,
}

/// <summary>
/// ⭐ <b>Handshake item 7, claimed and written.</b> ssot-granted-actions.md §3.5 <i>proposes</i> the
/// per-FSM-state removal table and spec-granted-actions.md records the audit's own verdict — "not
/// written down anywhere the kernel can be held to", assigned to nobody. This module claims it on the
/// lane's own reasoning: the rule says what a GRANT REMOVAL means, and grants are this module's whole
/// product. The kernel supplies the states; it owes no opinion on an item leaving.
///
/// <para><b>It is unreachable today, which is exactly what makes it cheap.</b> Equipment cannot change
/// mid-run — <c>UniqueActorService.PutEquipment</c> refuses unless the actor's phase is <c>Roster</c>
/// (<c>phase.not_roster</c>) and <c>ClearEquipment</c> routes through the same method. So the shipping
/// rule is unchanged: <b>the actor's granted-action set is assembled at run start and is immutable for
/// the run</b> (<c>FrozenActionSet.FreezeAtRunStart</c>). This table is the contract for the day that
/// stops being true, and <see cref="ItemGrantLandedFlags.MidRunEquipLanded"/> carries the fact that
/// the day has not come.</para>
///
/// <para><b>There is no enforcement code and that is deliberate</b> — there is nothing to enforce until
/// mid-match equip exists, and a policy that reached into the kernel now would be inventing the very
/// coupling invariant 3 forbids. This is a pure lookup plus three invariants a test can hold the
/// kernel to.</para>
/// </summary>
public static class GrantRemovalPolicy
{
    /// <summary>
    /// The per-state rule, verified against the shipped FSM: <see cref="TurnState"/> is eight values
    /// and <c>TurnTransitions</c> declares every edge in the same file.
    /// </summary>
    public static GrantRemovalEffect EffectIn(TurnState state) => state switch
    {
        // Nothing has been paid — IIntentSource.TryDeclare simply stops offering it.
        TurnState.Charging => GrantRemovalEffect.Immediate,
        TurnState.Ready => GrantRemovalEffect.Immediate,

        // "Committing is what costs, not landing." Cancelling here needs a refund path the action
        // model forbids, and it would mean an inventory event reaching the kernel's slot accounting.
        TurnState.Committed => GrantRemovalEffect.LetTheRunComplete,
        TurnState.Resolving => GrantRemovalEffect.LetTheRunComplete,

        // Recovering → Charging is the only edge out.
        TurnState.Recovering => GrantRemovalEffect.AtNextCharging,

        // Downed → Charging is legal, so a revive must not resurrect a removed grant.
        TurnState.Downed => GrantRemovalEffect.RecordedAndSurvivesRevive,
        TurnState.Dead => GrantRemovalEffect.RecordedAndSurvivesRevive,
        TurnState.Withdrawn => GrantRemovalEffect.RecordedAndSurvivesRevive,

        _ => throw new ArgumentOutOfRangeException(nameof(state), state,
            "grant-removal: TurnState grew a member and this table did not — the table is the contract"),
    };

    /// <summary>Invariant 1: removal applies at the next QUIESCENT point, never mid-commitment.</summary>
    public static bool AppliesImmediately(TurnState state) => EffectIn(state) == GrantRemovalEffect.Immediate;

    /// <summary>Invariant 2: removal NEVER cancels a committed action. There is no refund path, by
    /// rule, so a cancellation would either leak a paid cost or invent one.</summary>
    public static bool CancelsACommittedAction(TurnState _) => false;

    /// <summary>
    /// ⛔ Invariant 3: an inventory event NEVER becomes an <c>InterruptCause</c>. A third cause for
    /// "the item left" puts an item-layer concern inside the kernel's slot accounting — the one place
    /// in this repo with a zero-allocation contract and a byte-identical gate in front of it. This
    /// module adds no member and requests the refusal in writing (§9.10); the guard is a test over the
    /// shipped enum, not a check here.
    ///
    /// <para>⚠ <b>Doc drift, recorded not absorbed:</b> spec-granted-actions.md and ssot §3.5 both say
    /// the enum is "<c>CrowdControl</c> and <c>Damage</c>". It has THREE members today —
    /// <c>ResourceExhausted</c> landed with the per-tick cost model, and its own comment already states
    /// this rule ("a mechanical fact about the actor's own resources, never an inventory/content
    /// concept reaching this enum"). So the invariant is intact and the count is stale; the test
    /// asserts the invariant, never the count.</para>
    /// </summary>
    public const bool InventoryEventMayBeAnInterruptCause = false;

    /// <summary>
    /// ⚠ <b>Nothing needs reverting, and cooldown survives removal for free.</b> A granted action
    /// creates no binding, so the apply/revert lifecycle <c>stat.modify</c> and <c>stat.derived</c>
    /// carry does not apply; and <c>CooldownLedger</c> keys on <c>CooldownSlot(ActorKey, Slot)</c>, not
    /// on the item, so unequip-then-re-equip does not reset a cooldown. That closes the classic swap
    /// exploit and nobody should "fix" it.
    /// </summary>
    public const bool CooldownIsKeyedOnTheItem = false;
}
