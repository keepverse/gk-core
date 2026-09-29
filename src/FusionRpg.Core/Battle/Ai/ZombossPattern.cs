using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Battle.Ai;

/// <summary>
/// class-system-todo.md P7.5 — one named allocation shape (spec-zomboss-patterns.md, read in full
/// this session). SHARES only, never point counts (§6: "Authoring counts would make every pattern
/// Θ-specific and re-create the per-level authoring §2 point 2 exists to avoid") — <see cref="Shares"/>
/// is a proportion per aptitude id, and <see cref="ToAllocation"/> is what turns a proportion into a
/// real, Θ-scaled spend, going through `point-economy`'s own <see cref="PointBudget"/> rather than any
/// private math (PS-3, ideal §4.1 rule 2: a pattern is a mechanism, never its own scale function).
/// </summary>
/// <param name="AuraId">aura-skill T17: which of the twelve `AuraContentCatalog` auras this pattern
/// runs — "authored data, tunable, no AI logic" (owner's own framing). Derived from each pattern's
/// OWN already-authored `SharePermille` (the highest-weighted aptitude in that pattern, ties broken
/// alphabetically) rather than a second, independent hand-pick — the aura a pattern runs matches the
/// aptitude it already invests in most, which is what "the aggressive pattern runs Might" means in
/// practice once a real weight table exists to check it against.</param>
public sealed record ZombossPattern(string Id, IReadOnlyDictionary<string, long> SharePermille, string AuraId)
{
    /// <summary>Converts this pattern's shares into a real <see cref="AptitudeAllocation"/> spending
    /// AT MOST <paramref name="budget"/> points total — never more (spec §2 point 4, "the anti-cheat":
    /// a pattern is an allocation from the SAME finite pool the player draws on). Each aptitude's spend
    /// is <c>budget × sharePermille / 1000</c>, widened before multiplying, divided last (docs/architecture/numeric-types.md's
    /// overflow discipline) — integer division means the sum can fall slightly short of
    /// <paramref name="budget"/> when it does not divide evenly, but can never exceed it.</summary>
    public AptitudeAllocation ToAllocation(AllocationScope scope, long budget)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget), budget, "budget cannot be negative");

        var allocation = AptitudeAllocation.Empty;
        foreach (var (aptitudeId, permille) in SharePermille)
        {
            long points;
            checked { points = budget * permille / 1000; }
            if (points == 0) continue;
            allocation += AptitudeAllocation.Single(scope, aptitudeId, points);
        }
        return allocation;
    }
}
