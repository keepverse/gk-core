using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Shield;

/// <summary>One shield pool on one actor — shield-system-spec.md §2.1.</summary>
public sealed class ShieldInstance
{
    public string ShieldId { get; init; } = "";           // "{sourceId}:{elementId|none}"
    public string OwnerKey { get; init; } = "";
    public ElementTypeId? Element { get; init; }          // null = untyped: omni-only reads, elemMod 0
    public long MaxHp { get; set; }
    public long Hp { get; set; }
    public int Priority { get; init; }                    // higher drains first (aura 30 → skill 20 → innate 10)
    public long CreatedSeq { get; init; }
    public long? ExpiresAtTick { get; set; }              // shield ticks; innate default none
    public string SourceId { get; init; } = "";           // grantId or "innate:{typeId}"
    public bool RefillOnMerge { get; init; }              // skill/effect true, aura re-assert false
    public bool IsInnate { get; init; }
}

/// <summary>Grant request — the only way shields come into being.</summary>
public sealed class ShieldGrant
{
    public string OwnerKey { get; init; } = "";
    public string SourceId { get; init; } = "";
    public ElementTypeId? Element { get; init; }
    public long BaseHp { get; init; }
    public int Priority { get; init; } = ShieldPolicy.PrioritySkill;
    public long? DurationTicks { get; init; }
    public bool RefillOnMerge { get; init; } = true;
    public bool IsInnate { get; init; }
}

public enum ShieldApplyOutcome
{
    Applied,
    Merged,
    /// <summary>Dropped at cap: not stronger than the weakest existing shield (debug line, no event).</summary>
    DroppedWeaker,
    /// <summary>maxHp ≤ 0 after capacity contributions (debug line, no instance, no event).</summary>
    Rejected,
    /// <summary>Merge recompute drove maxHp ≤ 0 — instance removed as shield.expired.</summary>
    MergedRemoved
}

public readonly record struct ShieldApplyResult(
    ShieldApplyOutcome Outcome, ShieldInstance? Instance, ShieldInstance? Evicted);
