namespace FusionRpg.Core.Actions;

/// <summary>
/// Every answer to "may this actor use this action against this target, right now?"
/// (spec-usability-conditions.md §2). Never a bare boolean: the FE needs to explain a greyed
/// button, and `A7` needs to know whether re-checking next tick could change the answer —
/// `OnCooldown` and `CannotAfford` become true with time, `NotBound` never does.
/// </summary>
public enum UsabilityReason
{
    Usable = 0,
    StanceHeld,
    NotBound,
    OnCooldown,
    CannotAfford,
    TooClose,
    OutOfRange,
    ConditionFailed,
    NoValidTarget,
    MissingStock,

    /// <summary>aura-skill T13: toggling on an aura the commander does not carry in their 5-slot
    /// loadout — equipped and active are independent scarcities (spec-aura-action-shape.md §1).</summary>
    NotEquipped,

    /// <summary>aura-skill T13: toggling on an aura that is already active — a no-op that reports it,
    /// never a refresh that resets the aura's age in the FIFO eviction order.</summary>
    AlreadyActive,
}

/// <summary>
/// One typed refusal. <see cref="Detail"/> carries the payload for the two parameterised reasons —
/// the resource id for <see cref="UsabilityReason.CannotAfford"/>, the stock id for
/// <see cref="UsabilityReason.MissingStock"/> — rather than a second discriminated-union type.
/// </summary>
public readonly record struct UsabilityResult(UsabilityReason Reason, string? Detail = null)
{
    public static readonly UsabilityResult Usable = new(UsabilityReason.Usable);

    public bool IsUsable => Reason == UsabilityReason.Usable;

    public static UsabilityResult Refuse(UsabilityReason reason, string? detail = null) =>
        new(reason, detail);

    public override string ToString() => IsUsable ? "usable" : $"{Reason}" + (Detail is null ? "" : $"({Detail})");
}
