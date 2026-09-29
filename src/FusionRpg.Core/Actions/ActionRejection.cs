namespace FusionRpg.Core.Actions;

/// <summary>
/// Why an <c>rpg_action</c> row, cost row, scope row or grant was refused at authoring time.
///
/// <para>Its own closed list, separate from <c>AtomRejectionReason</c> — that one is fixed at 33
/// members for the effect-atom program specifically (definitions.md §10). The action program is a
/// different layer with its own authoring mistakes, and folding action-shaped reasons into the atom
/// program's list would make that guard-test count assert something it was never about.</para>
/// </summary>
public enum ActionRejectionReason
{
    None = 0,

    /// <summary>`container_id` does not name a loaded `effect_container` (A1 §2, §9).</summary>
    UnknownContainer,

    /// <summary>`resource_id` is not one of the six shipped resources (A1 §3, `DerivedStatChannels.ResourceIds`).</summary>
    UnknownResource,

    /// <summary>`min_range > max_range` (A1 §2, §9).</summary>
    InvalidRange,

    /// <summary>Tag is not one of the eight closed action tags (A1 §2).</summary>
    UnknownTag,

    /// <summary>`kind` is not `basic` | `innate` | `skill` (A1 §2).</summary>
    UnknownKind,

    /// <summary>`scope` is not `caster` | `primaryTarget` | `eachTarget` | `casterAllies` (A1 §4).</summary>
    UnknownScope,

    /// <summary>An effect-scope row names an atom its own container does not hold (A1 §4, §9).</summary>
    ScopeAtomNotInContainer,

    /// <summary>`action_id` is empty, or does not match the grammar a caller expects.</summary>
    BadActionId,

    /// <summary>A `ValueSpec` column is malformed — see `ValueSpec.Validate()` (A1 §2).</summary>
    BadValueSpec,

    /// <summary>A `when` value is not `onCommit` | `perTick` (A1 §3).</summary>
    BadCostTiming,

    /// <summary>A species-basics row omits one of the three required basics (A1 §1, §9).</summary>
    MissingSpeciesBasic,

    /// <summary>A basics slot names an action whose `kind` does not match the slot (A1 §1).</summary>
    BasicKindMismatch,

    /// <summary>A grant's `action_id` collides with a species basic — never double-counted (A1 §1, §9).</summary>
    BasicCollision,

    /// <summary>An item/passive/variant tried to grant an action with `grantable = 0` (A1 §2.1, §9).</summary>
    ActionNotGrantable,

    /// <summary>An action with `default_attack_eligible = 0` was proposed as a default-attack replacement (A1 §2.1).</summary>
    ActionNotDefaultAttackEligible,

    /// <summary>Owner key does not match its scope's grammar — reuses `OwnerScope.TryParse` (A1 §5).</summary>
    BadOwnerKey,

    /// <summary>`rung` does not index a loaded rung row (A12).</summary>
    UnknownRung,

    /// <summary>`Mode = Area` with no board — rejected loudly rather than silently (A1 §8, A2).</summary>
    AreaRequiresBoard,

    /// <summary>The action's authored structure spends more axes than its rung's `structureBudget`
    /// allows (T30, spec-action-catalog.md R1, spec-rung-table.md §4) — "an authored or seeded action
    /// whose structure exceeds its rung's budget is rejected at load, naming the rung and the axis."</summary>
    StructureExceedsBudget,

    /// <summary>`conditions_json` failed E3's own predicate validation (bad leaf, depth, or node
    /// count) — never silently treated as "always" (T30, A6 §2).</summary>
    BadConditionsJson,

    /// <summary>The action's condition authors `holdsStock` (a consumable precondition), but the
    /// bind mode does not support it — PvZ lawn mode is a stateless observer and never reads current
    /// inventory (T10, spec-usability-conditions.md's mode matrix). "An unsupported mode named is
    /// fine; an unstated one is the `resource.delta` defect again."</summary>
    ConsumableUnsupportedInMode,

    /// <summary>The action's condition authors a `holdsStock` leaf in a position where the action can
    /// still fire without that leaf being true — under an `or`, or negated by a `not`. There is then
    /// no defined quantity to take at commit, and the honest options are "spend nothing" (a free
    /// consumable, which is the exact defect `IStockLedger` exists to close) or "guess". Refused by
    /// name instead: a `holdsStock` leaf must sit in conjunctive position — the root, or reachable
    /// from it through `and` alone — so that firing PROVES the demand held.</summary>
    ConsumableStockDemandNotGuaranteed,

    /// <summary>A-G1 (spec-tier-access-gate.md §3.2): the container an action's rung reaches spends
    /// more power than that rung's `powerBudgetMilli` allows (`ContentValidation.Budget`'s rung-keyed
    /// overload). A finding, never a clamp — the action is refused whole, naming the container and
    /// the overage, the same "reject, never coerce" law every other reason on this list already
    /// follows.</summary>
    PowerBudgetExceeded,

    /// <summary>A-M1 (spec-movement-payload.md §2, §4): a `category = Movement` action whose compiled
    /// container carries no bound effect atom — `MovementPayloadPolicy.HasStandalonePayload` is false.
    /// Invariant 9 (standalone-first) cuts the OTHER way: the reposition itself is never required, but
    /// the RPG-layer payload is — "a movement action must do something with the game closed."</summary>
    MovementActionHasNoStandalonePayload,
}

/// <summary>One refusal: the rule that fired, plus enough detail to fix the row.</summary>
public readonly record struct ActionRejection(ActionRejectionReason Reason, string Detail)
{
    public static ActionRejection Ok => new(ActionRejectionReason.None, "");

    public bool IsOk => Reason == ActionRejectionReason.None;

    public static ActionRejection Fail(ActionRejectionReason reason, string detail) => new(reason, detail);

    public override string ToString() => IsOk ? "ok" : $"{Reason}: {Detail}";
}
