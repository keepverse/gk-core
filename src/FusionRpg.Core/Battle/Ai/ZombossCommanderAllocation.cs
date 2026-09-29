using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Battle.Ai;

/// <summary>
/// aura-skill T9b: Zomboss's half of "each commander resolves an aptitude allocation." Nine authored
/// <see cref="ZombossPattern"/>s (<see cref="ZombossPatterns"/>) and a tested
/// <see cref="ZombossPattern.ToAllocation"/> conversion already existed with ZERO production callers —
/// this is the first one. Mirrors <c>CommanderAllocationSource</c>'s own shape (T5) for Dave: a small
/// cache the hot path reads, refreshed explicitly rather than reading the tuning/Θ pipeline per stat
/// resolve — the two differ only in WHERE the allocation comes from (a fetched store row for Dave, an
/// authored pattern converted through <see cref="PointBudget"/> for Zomboss), never in the shape the
/// hot path consumes.
/// </summary>
public sealed class ZombossCommanderAllocation
{
    string _activePatternId;
    AptitudeAllocation _cached = AptitudeAllocation.Empty;

    public ZombossCommanderAllocation(string initialPatternId)
    {
        if (!ZombossPatterns.IsKnown(initialPatternId))
            throw new ArgumentException($"'{initialPatternId}' is not a known Zomboss pattern.", nameof(initialPatternId));
        _activePatternId = initialPatternId;
    }

    public string ActivePatternId => _activePatternId;

    /// <summary>aura-skill T17: "Zomboss's aura resolves from his pattern" — a bare lookup, no AI
    /// logic, matching `ZombossPatterns`' own already-authored `AuraId` field.</summary>
    public string ActiveAuraId => ZombossPatterns.Resolve(_activePatternId).AuraId;

    /// <summary>Switching patterns is a deliberate, named event (a Zomboss "build" change) — never
    /// implicit, matching the same "explicit, never per-tick" discipline the T4 recompose seam
    /// established for derived channels.</summary>
    public void SetActivePattern(string patternId)
    {
        if (!ZombossPatterns.IsKnown(patternId))
            throw new ArgumentException($"'{patternId}' is not a known Zomboss pattern.", nameof(patternId));
        _activePatternId = patternId;
    }

    /// <summary>Recomputes the cached allocation from the active pattern, a Θ value, and the loaded
    /// tuning — called on a pattern change or a Θ revision, never on the hot path (mirrors
    /// `CommanderAllocationSource.Refresh`'s own contract exactly).
    ///
    /// <para><b><paramref name="scope"/> is an argument, never a hard-coded Commander constant</b>
    /// (species-build-todo.md T4.5, spec-zomboss-adaptive.md's own ⛔): a Zomboss pattern is a NAMED
    /// allocation, not a player's commander build, so whichever <see cref="AllocationScope"/> the
    /// battle seam resolves the enemy side under is the caller's call, not this type's.</para></summary>
    public void Refresh(AllocationScope scope, long theta, AptitudeTuning tuning)
    {
        var pattern = ZombossPatterns.Resolve(_activePatternId);
        var budget = PointBudget.PointsFor(scope, theta, tuning);
        _cached = pattern.ToAllocation(scope, budget);
    }

    /// <summary>The hot-path delegate shape — a bare field read, never a pattern lookup or a
    /// `PointBudget` computation.</summary>
    public AptitudeAllocation Resolve(Stats.StatContext _) => _cached;
}
