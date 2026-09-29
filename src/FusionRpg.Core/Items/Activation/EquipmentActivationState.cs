namespace FusionRpg.Core.Items.Activation;

/// <summary>
/// item/spec-equipment-activation.md §"Deployment-run status" — the closed activation vocabulary.
/// Three states, and only one of them contributes effects. A new state is a reviewed change to the
/// spec, the store, and the effect-read filter together, never one of the three alone.
/// </summary>
public enum EquipmentActivationState
{
    /// <summary>A trial clause is unmet. Non-blocking guidance: the assignment stands, the item
    /// simply contributes nothing yet.</summary>
    Trial = 0,

    /// <summary>Every trial condition passes and upkeep, if any, is current. The only state whose
    /// atoms reach the effect source.</summary>
    Active = 1,

    /// <summary>Upkeep failed, or an active build trial lapsed. The durable assignment is untouched;
    /// the item disappears from the effect source and can return at a later evaluation.</summary>
    Suspended = 2,
}

/// <summary>
/// The closed reason vocabulary from the same spec section. <see cref="SetTrialUnmet"/> is module
/// 25's set-level verdict, reached through <c>SetTierActivationPolicy</c> — the clause that produced
/// it travels beside it on the activation record, so this enum does not widen per clause.
/// </summary>
public enum EquipmentActivationReasonKind
{
    None = 0,
    BuildUnmet = 1,
    LevelUnmet = 2,
    UpkeepShortfall = 3,
    HpRecoveryLocked = 4,
    SetTrialUnmet = 5,
}

/// <summary>
/// A typed reason. <see cref="ResourceId"/> is carried only by the two upkeep reasons — "A shortfall
/// suspends only that item and records its resource id" — and is the actor-pool id the payment could
/// not preserve. It is never a free-text note: the reason kind is the contract module 20 renders.
/// </summary>
public readonly record struct EquipmentActivationReason(
    EquipmentActivationReasonKind Kind,
    string? ResourceId = null)
{
    public static readonly EquipmentActivationReason None = new(EquipmentActivationReasonKind.None);

    public static readonly EquipmentActivationReason BuildUnmet = new(EquipmentActivationReasonKind.BuildUnmet);

    public static readonly EquipmentActivationReason LevelUnmet = new(EquipmentActivationReasonKind.LevelUnmet);

    /// <summary>An upkeep payment could not preserve the pool's reserve (and, for <c>hp</c>, the
    /// ledger's own floor). The resource id is recorded so the shortfall is nameable.</summary>
    public static EquipmentActivationReason UpkeepShortfall(string resourceId) =>
        new(EquipmentActivationReasonKind.UpkeepShortfall, resourceId);

    /// <summary>An <c>hp</c> upkeep shortfall. The latch this reason accompanies clears only at an
    /// evaluation whose fresh derived snapshot reads full HP.</summary>
    public static readonly EquipmentActivationReason HpRecoveryLocked =
        new(EquipmentActivationReasonKind.HpRecoveryLocked, "hp");

    /// <summary>The whole set's tiers are held back until its one frozen envelope is satisfied —
    /// module 25's verdict, surfaced through module 24's filter. The finer clause travels on
    /// <see cref="SetTierActivation.Trial"/> rather than as a sixth kind, so this enum stays closed.</summary>
    public static readonly EquipmentActivationReason SetTrialUnmet =
        new(EquipmentActivationReasonKind.SetTrialUnmet);
}

/// <summary>
/// One item's activation state inside ONE deployment run. This is in-memory deployment state, never
/// an item fact: the frozen <c>RequirementProfile</c> and the durable assignment are the item facts,
/// and neither is written here.
///
/// <para><see cref="NextDueTick"/> is the deployment's own logical tick at which the next upkeep
/// payment is due, or <c>null</c> when the profile carries no upkeep, when the item is in trial, or
/// when it is suspended — a suspended item holds no schedule, so a later reactivation cannot
/// back-charge the intervals it missed.</para>
///
/// <para><see cref="HpRecoveryLocked"/> is a latch, not a state: it survives a trial/suspended
/// transition and clears only on a full-HP evaluation. <see cref="Revision"/> is monotonic per row
/// (per deployment, since rows never cross deployments) and moves whenever any other field changes,
/// which is what lets the host refresh its snapshot exactly once per real transition.</para>
/// </summary>
public readonly record struct EquipmentRunStatus(
    EquipmentActivationState State,
    EquipmentActivationReason Reason,
    long? NextDueTick,
    bool HpRecoveryLocked,
    long Revision)
{
    /// <summary>The status of an assignment that has not been evaluated yet in this deployment:
    /// in trial, no schedule, no latch. Revision 0 is the "nothing has happened" reading.</summary>
    public static readonly EquipmentRunStatus Initial = new(
        EquipmentActivationState.Trial, EquipmentActivationReason.None, null, false, 0);

    /// <summary>Whether this item's atoms belong in the effect source. The single predicate the
    /// equipment read filters on — never a second, parallel notion of "usable".</summary>
    public bool ContributesEffects => State == EquipmentActivationState.Active;
}
