namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// passive-tree G7 (spec-species-tree.md §8.1 point 2) — makes <see cref="AllocationScope.UniqueCreature"/>
/// real, the exact `UniqueCreature`-scope mirror of <see cref="SpeciesAllocation"/>'s own `CreatureType`
/// binding (<c>SpeciesAllocation.cs:35,62</c>). Before this type, nothing in `src/` passed
/// <see cref="AllocationScope.UniqueCreature"/> to <see cref="PointBudget.PointsFor"/> or
/// <see cref="PointBudget.CheckScope"/> — every real call site read `Commander` or `CreatureType`
/// (`AptitudeEndpoints.cs`, `SpeciesBuildEndpoints.cs`, `SpeciesAllocation.cs`,
/// `ZombossCommanderAllocation.cs`), so a species tree gated on a specimen's own aptitude points read
/// zero regardless of level — the exact defect this type exists to close.
///
/// <para><b>Why a sibling file, not a method added to <see cref="SpeciesAllocation"/>.</b>
/// `SpeciesAllocation` is keyed by `(playerId, speciesId)` — a species TYPE, shared by every specimen
/// of it. `UniqueCreature` is keyed by `instanceId` — one specimen. Different identity grammar
/// (`spec-point-economy.md` §2: "unique creature | keyed by instanceId | specimen level"), so this is a
/// new file rather than a scope parameter bolted onto the type that already exists for a different
/// key shape, matching this repo's own naming symmetry (one allocation type per scope's own identity).
/// </para>
///
/// <para><b>Deliberately NOT merged with <see cref="SpeciesAllocation.Baseline"/> into one generic,
/// scope-parameterized method.</b> `PointBudget.cs` itself already sets this precedent —
/// <see cref="PointBudget.PointsFor"/> and <see cref="PointBudget.SkillPointsFor"/> are near-identical
/// in shape and are NOT unified, because they diverge in what happens on a missing rate. The two
/// `Baseline` methods here and in `SpeciesAllocation` are kept equally explicit and equally
/// independently readable, at the cost of textual duplication — an intentional trade this codebase has
/// already made once.</para>
/// </summary>
public static class UniqueCreatureAllocation
{
    /// <summary>
    /// The baseline — computed, never persisted (same rule <see cref="SpeciesAllocation.Baseline"/>
    /// follows, species-build audit finding A9): the plan's share vector (permille, summing to 1000)
    /// scaled by the UniqueCreature budget at this SPECIMEN's own level. Zero shares →
    /// <see cref="AptitudeAllocation.Empty"/>, zero budget → <see cref="AptitudeAllocation.Empty"/>
    /// (never a thrown error) — a specimen missing a plan, or a never-levelled specimen
    /// (`specimenLevel &lt;= 1` ⇒ <see cref="PointBudget.UniqueCreatureSourceFromLevel"/> = 0 ⇒ budget = 0),
    /// both legitimately have no baseline yet — a fresh roster entry must never read a non-empty
    /// allocation nobody earned. Widened before multiplying, largest-remainder rounding (identical
    /// discipline to the `CreatureType` twin) so the shares' points sum to exactly the budget rather than
    /// losing a few points to integer-division truncation on every read.
    /// </summary>
    public static AptitudeAllocation Baseline(
        IReadOnlyDictionary<string, long> planSharePermille, long specimenLevel, AptitudeTuning tuning)
    {
        if (planSharePermille is null) throw new ArgumentNullException(nameof(planSharePermille));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (planSharePermille.Count == 0) return AptitudeAllocation.Empty;

        var source = PointBudget.UniqueCreatureSourceFromLevel(specimenLevel);
        var budget = PointBudget.PointsFor(AllocationScope.UniqueCreature, source, tuning);
        if (budget == 0) return AptitudeAllocation.Empty;

        var baseShares = new Dictionary<string, long>(StringComparer.Ordinal);
        var remainders = new Dictionary<string, long>(StringComparer.Ordinal);
        long allocated = 0;
        foreach (var (aptId, sharePermille) in planSharePermille)
        {
            if (!AptitudeCatalog.IsAptitudeId(aptId))
                throw new ArgumentException($"plan share names unknown aptitude id '{aptId}'", nameof(planSharePermille));
            long product;
            checked { product = budget * sharePermille; }
            var baseShare = product / 1000;
            baseShares[aptId] = baseShare;
            remainders[aptId] = product % 1000;
            checked { allocated += baseShare; }
        }

        var leftover = budget - allocated; // always in [0, planSharePermille.Count) by construction
        var allocation = AptitudeAllocation.Empty;
        foreach (var aptId in remainders.Keys
                     .OrderByDescending(id => remainders[id])
                     .ThenBy(id => id, StringComparer.Ordinal))
        {
            var points = baseShares[aptId];
            if (leftover > 0) { points++; leftover--; }
            if (points > 0)
                allocation += AptitudeAllocation.Single(AllocationScope.UniqueCreature, aptId, points);
        }
        return allocation;
    }
}
