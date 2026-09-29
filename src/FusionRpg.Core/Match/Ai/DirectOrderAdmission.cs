using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Match.Ai;

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §2) — the
/// closed refusal vocabulary for an order that did not become a live order. Every member is a distinct
/// CAUSE, not a distinct message, because each one sends the player somewhere different.
///
/// <para><b>All five causes have their RULE here; three of them still lack their WIRING, and that is
/// stated rather than implied.</b> <see cref="StaleRun"/>, <see cref="SubjectMoved"/> and
/// <see cref="SubjectGone"/> are decided by <see cref="DirectOrderAdmission.CheckScope"/> and
/// <see cref="DirectOrderAdmission.CheckSubject"/>, which take the durable identity as ARGUMENTS. What
/// is still owed is the transport that supplies those arguments: the two additive
/// <c>SubjectId</c>/<c>ScopeId</c> fields on <c>Actions/DirectOrder.cs</c>, the injector host that
/// resolves a subject, the Server endpoint and the two <c>web/**</c> files — every one of them outside
/// this module's Core/Match half. <see cref="NotHeld"/> and <see cref="QueueFull"/> have both their rule
/// and their caller-facing producer (<see cref="DirectOrderAdmission.IsHeld"/>,
/// <see cref="LawnOrderQueue.Offer"/>).</para>
/// </summary>
public enum DirectOrderRefusal
{
    /// <summary>Not refused — the vocabulary's own zero, so a default-constructed result reads as "no
    /// refusal" rather than as a spurious <see cref="StaleRun"/>.</summary>
    None = 0,

    /// <summary>The order was issued in a different run. Producer owed: §2 step 1.</summary>
    StaleRun,

    /// <summary>The durable subject resolves to a different ptr than the order names — the ptr was reused,
    /// or the player's observation is stale. Producer owed: §2 step 3.</summary>
    SubjectMoved,

    /// <summary>No <c>Bound</c> row, or the ptr is not live. Admission cannot invent a subject.
    /// Producer owed: §2 steps 2 and 4.</summary>
    SubjectGone,

    /// <summary>The action is not in that actor's held set — the creature cannot cast it at all.</summary>
    NotHeld,

    /// <summary>The queue is at <see cref="LawnOrderQueue.Cap"/>. A retryable condition, and the only
    /// refusal that does not blame the order.</summary>
    QueueFull,
}

/// <summary>
/// How a refusal relates to TIME — the split `UsabilityReason`'s own doc already encodes ("`OnCooldown`
/// and `CannotAfford` become true with time, `NotBound` never does") and the one an order needs, because
/// a refusal that silently cancels itself is indistinguishable from a bug the first time a cooldown is
/// one tick out.
/// </summary>
public enum DirectOrderRefusalClass
{
    /// <summary>The order stays live and is reconsidered on every later edge.</summary>
    Retryable,

    /// <summary>The order is removed at once — retrying cannot help.</summary>
    Terminal,
}

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9) — the pure admission rules that do not need
/// the order's durable identity, plus the retryable/terminal projection of
/// <see cref="UsabilityReason"/>.
///
/// <para><b>This is a projection of the existing closed enum, not a second refusal vocabulary.</b> The
/// lawn's decision inspector already carries a per-candidate <see cref="UsabilityResult"/>, and a second
/// vocabulary for the same fact is the defect the atom/action programs exist to stop. A new
/// <see cref="UsabilityReason"/> member added later must be classified here in the same commit, and the
/// test walks the whole enum so an unclassified member fails rather than defaulting to "retryable
/// forever".</para>
/// </summary>
/// <summary>
/// What the caller resolved for an order's durable subject, before admission judges it. Three facts and
/// no lookup: <see cref="Ptr"/> is the <c>Bound</c> row's own address, <see cref="IsBound"/> is that row's
/// phase, and <see cref="IsLive"/> is the ptr's presence in the live registry. A caller that has not
/// resolved all three passes <c>default</c>, which is not bound and therefore refuses
/// <see cref="DirectOrderRefusal.SubjectGone"/> rather than admitting on a guess.
/// </summary>
/// <param name="SubjectId">The durable Bound instance id the caller resolved — the SAME value the order
/// carries, so an admitted order and its subject cannot disagree. Trailing and optional, so every
/// existing construction compiles; the RULES read only <c>IsBound</c>/<c>Ptr</c>, which is why a caller
/// that has not resolved one can still ask them.</param>
public readonly record struct OrderSubject(string? Ptr, bool IsBound, bool IsLive, string? SubjectId = null);

public static class DirectOrderAdmission
{
    /// <summary>
    /// §2 step 5: the action must be in the ptr's held set. Takes module 16's compiled list rather than a
    /// view, so admission holds no board read of its own; indexed, never LINQ.
    /// </summary>
    public static bool IsHeld(string actionId, IReadOnlyList<CompiledAction> heldActions)
    {
        if (string.IsNullOrEmpty(actionId)) return false;
        if (heldActions is null) return false;

        for (var i = 0; i < heldActions.Count; i++)
        {
            if (string.Equals(heldActions[i].ActionId, actionId, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// §2 step 1: the order was issued in the run the caller is living in. Takes both ids as ARGUMENTS
    /// rather than reading one off the order struct, so the rule is complete and testable here while the
    /// struct's own field is still owed — and so there is exactly one implementation of the comparison
    /// when that field lands.
    /// </summary>
    public static DirectOrderRefusal CheckScope(string orderScopeId, string liveScopeId) =>
        string.Equals(orderScopeId, liveScopeId, StringComparison.Ordinal)
            ? DirectOrderRefusal.None
            : DirectOrderRefusal.StaleRun;

    /// <summary>
    /// §2 steps 2 to 4, over what the caller ALREADY resolved: the durable subject's own row and ptr.
    /// Admission performs no lookup of its own — it cannot invent a subject, which is the debug-scope rule
    /// ("an id must resolve to a row real gameplay could have created, never one the call invents") held
    /// by construction rather than by review.
    ///
    /// <para>Step 3 is the whole ptr-reuse answer: the row's ptr must be the one the order names. A
    /// mismatch means the address was reused, or the FE's observation is stale — either way the order is
    /// about a different creature than the one it would command.</para>
    /// </summary>
    public static DirectOrderRefusal CheckSubject(in OrderSubject subject, string orderActorKey)
    {
        // §2 step 2: no `Bound` row means the creature this order was issued against is not (or no
        // longer) a bound specimen. A pending or cleared binding chooses a different subject, so it is
        // gone for this order's purposes — never admitted against a ptr the FE supplied.
        if (!subject.IsBound || subject.Ptr is null) return DirectOrderRefusal.SubjectGone;

        // §2 step 3: the durable identity resolves, but to a DIFFERENT address than the order names.
        if (!string.Equals(subject.Ptr, orderActorKey, StringComparison.Ordinal))
            return DirectOrderRefusal.SubjectMoved;

        // §2 step 4: the right address, but nothing live is holding it.
        if (!subject.IsLive) return DirectOrderRefusal.SubjectGone;

        return DirectOrderRefusal.None;
    }

    /// <summary>
    /// Whether a gate refusal leaves the order live or removes it. Throws for
    /// <see cref="UsabilityReason.Usable"/>, which is not a refusal at all — a caller that classifies it
    /// has asked the wrong question, and answering it would invent a third class.
    /// </summary>
    public static DirectOrderRefusalClass Classify(UsabilityResult result) => result.Reason switch
    {
        UsabilityReason.OnCooldown => DirectOrderRefusalClass.Retryable,
        UsabilityReason.CannotAfford => DirectOrderRefusalClass.Retryable,
        UsabilityReason.MissingStock => DirectOrderRefusalClass.Retryable,
        UsabilityReason.OutOfRange => DirectOrderRefusalClass.Retryable,
        UsabilityReason.TooClose => DirectOrderRefusalClass.Retryable,
        UsabilityReason.NoValidTarget => DirectOrderRefusalClass.Retryable,
        UsabilityReason.ConditionFailed => DirectOrderRefusalClass.Retryable,
        UsabilityReason.StanceHeld => DirectOrderRefusalClass.Retryable,
        UsabilityReason.NotBound => DirectOrderRefusalClass.Terminal,
        UsabilityReason.NotEquipped => DirectOrderRefusalClass.Terminal,
        UsabilityReason.AlreadyActive => DirectOrderRefusalClass.Terminal,
        UsabilityReason.Usable => throw new ArgumentException(
            "'Usable' is not a refusal; classify only a gate that refused", nameof(result)),
        _ => throw new ArgumentOutOfRangeException(
            nameof(result), result.Reason,
            "unclassified UsabilityReason — a new member must be classified in DirectOrderAdmission.Classify"),
    };
}
