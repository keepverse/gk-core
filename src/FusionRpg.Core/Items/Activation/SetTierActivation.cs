using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Items.Thresholds;

namespace FusionRpg.Core.Items.Activation;

/// <summary>
/// item/spec-set-requirement-reconciliation.md §"Full-set activation": one set's earned tiers, split
/// into the ones that may contribute and the ones held back. <see cref="Count"/> is carried through
/// UNCHANGED — the gate is an activation predicate over an existing count, never a second counter and
/// never a change to what <c>N / total</c> shows.
/// </summary>
public readonly record struct SetTierActivation(
    string SetId,
    long Count,
    bool Active,
    IReadOnlyList<string> ActiveContainerIds,
    IReadOnlyList<string> SuppressedContainerIds,
    SetTrialResult Trial,
    EquipmentActivationReason Reason)
{
    /// <summary>True when the set earned tiers that are currently held back by the set trial — the
    /// state module 20 must render as "complete, and not yet doing anything".</summary>
    public bool Suppressed => !Active && SuppressedContainerIds.Count > 0;
}

/// <summary>
/// The set-tier half of module 24's effect filter: "Until it passes, the tier bindings remain durable
/// derived state but are filtered by module 24 with <c>set_trial_unmet</c>; they grant no capability
/// or stat effect. When it passes, every wanted tier becomes active together. A later lapse suspends
/// the tier effects together but never removes member assignments, membership counts, or the visible
/// completion path."
///
/// <para>Pure, and it owns no state: it maps <see cref="ThresholdGrant"/> (the existing role-deduped
/// count) plus a <see cref="SetTrialResult"/> to the same list, filtered. That is why it cannot change
/// the count and cannot withdraw an assignment — it returns a *view* of an already-computed grant.</para>
///
/// <para><b>A set with no frozen envelope is NOT gated.</b> That is deliberate and it is the honest
/// default while module 25's envelopes do not yet reach production (filed as
/// <c>ITEM-activation-2</c>): gating an unreconciled set would suppress live tier effects on the
/// strength of a contract nobody minted. Once every set carries an envelope, the only reachable way
/// to have none is a corpus defect — and the Seedsmith metric (module 25's rule 7) is what closes
/// that, not this predicate.</para>
/// </summary>
public static class SetTierActivationPolicy
{
    public static SetTierActivation For(
        string setId,
        SetRequirementEnvelope? envelope,
        SetTrialResult trial,
        ThresholdGrant grant)
    {
        if (string.IsNullOrWhiteSpace(setId)) throw new ArgumentException("a set id is required", nameof(setId));

        if (envelope is null || trial.Outcome == SetTrialOutcome.Ready)
            return new SetTierActivation(
                setId, grant.Count, true, grant.WantedContainerIds, Array.Empty<string>(),
                trial, EquipmentActivationReason.None);

        // One held-back list for the whole set, never per tier: the spec's atomicity is the point —
        // "every wanted tier becomes active together" and lapses together, so a partial activation is
        // unrepresentable here rather than merely discouraged.
        return new SetTierActivation(
            setId, grant.Count, false, Array.Empty<string>(), grant.WantedContainerIds,
            trial, EquipmentActivationReason.SetTrialUnmet);
    }
}
