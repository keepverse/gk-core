namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// spec-default-build.md — the result of resolving a <see cref="AllocationScope.UniqueCreature"/>
/// specimen's effective allocation (<c>RpgStore.EffectiveUniqueAllocation(Unlocked)</c>): the explicit
/// allocation wholesale when one exists, else the ladder's own suggestion at the specimen's own
/// budget. <see cref="IsDefault"/>/<see cref="DefaultRuleId"/> let the sheet say "Suggested build
/// (&lt;rule&gt;)" without re-deriving which rung won; <see cref="Skipped"/> carries every earlier
/// rung's own named reason, the same "no skip is silent" contract <see cref="AssignSuggestion"/> gives.
/// </summary>
public sealed record EffectiveAllocation(
    AptitudeAllocation Allocation,
    bool IsDefault,
    string? DefaultRuleId,
    IReadOnlyList<AssignSkip> Skipped);
