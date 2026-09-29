namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// species-progression SP3.8 (species-layer-delivery step 6.1's own prerequisite) — the ONE
/// <see cref="AllocationScope"/>&lt;-&gt;text vocabulary, moved here from
/// <c>RpgStore.ScopeToText</c>/<c>.ScopeFromText</c> (<c>RpgStore.Aptitudes.cs:53-67</c>) so a
/// Core-only consumer (<see cref="Stats.Derived.ContributionSourceIds.Aptitude(AllocationScope, string)"/>,
/// step 6.1's per-scope SourceId) reads the SAME scope text a persisted allocation row already
/// carries, rather than a second, independently-drifting copy. <c>RpgStore</c> keeps its own
/// `ScopeToText`/`ScopeFromText` methods (existing signature, existing callers unchanged) as thin
/// delegates to this type.
/// </summary>
public static class AllocationScopeText
{
    /// <summary>class-system-todo.md P6.2's own "unknown scope rejects" (§7 test 7) — the
    /// TEXT&lt;-&gt;<see cref="AllocationScope"/> boundary every row crosses in both directions. Throws
    /// naming the bad value rather than defaulting.</summary>
    public static string ToText(AllocationScope scope) => scope switch
    {
        AllocationScope.Commander => "commander",
        AllocationScope.CreatureType => "creatureType",
        AllocationScope.Aspect => "aspect",
        AllocationScope.UniqueCreature => "uniqueCreature",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "unknown AllocationScope"),
    };

    public static AllocationScope FromText(string text) => text switch
    {
        "commander" => AllocationScope.Commander,
        "creatureType" => AllocationScope.CreatureType,
        "aspect" => AllocationScope.Aspect,
        "uniqueCreature" => AllocationScope.UniqueCreature,
        _ => throw new ArgumentException(
            $"'{text}' is not a known allocation scope (commander/creatureType/aspect/uniqueCreature)", nameof(text)),
    };
}
