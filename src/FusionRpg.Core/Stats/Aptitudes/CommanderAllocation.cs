namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// `ai-empire-species` EP4.17 (R23) — the `Commander`-scope baseline: an empire's commander pool
/// computed from the assign ladder's distribution at that empire's own point budget, never persisted
/// (map D1: "computed at read").
///
/// <para><b>Why a third `Baseline` and not a merged generic.</b> <see cref="SpeciesAllocation"/>'s
/// `CreatureType` and <see cref="UniqueCreatureAllocation"/>'s `UniqueCreature` baselines are
/// deliberately two explicit copies rather than one scope-parameterized method — that trade is
/// documented in <see cref="UniqueCreatureAllocation"/>'s own type doc, and this is the third scope's
/// turn. What must NOT be duplicated is the ROUNDING SCHEME: this is the same largest-remainder math,
/// so a pool's points sum to exactly its budget rather than losing the integer-division remainder on
/// every read.</para>
///
/// <para><b>The one input that differs from its twins:</b> a commander's budget source is its Theta
/// itself (<see cref="PointBudget.PointsFor"/> over the value the caller composed — EP4.16's
/// `ActorIndexFor`), not a level fed through a source-from-level function. That is exactly how
/// `AptitudeEndpoints` already checks a player's commander allocation
/// (<c>PointBudget.CheckScope(Commander, allocation, theta, …)</c>), so this adds no new budget rule.</para>
/// </summary>
public static class CommanderAllocation
{
    /// <summary>
    /// The pool an empire's commander ladder distribution pays for at <paramref name="theta"/>: the
    /// permille vector (summing to 1000) scaled by the <see cref="AllocationScope.Commander"/> budget.
    /// Zero shares or a zero budget → <see cref="AptitudeAllocation.Empty"/> (never a thrown error): a
    /// never-credited AI empire has no pool yet, which is the honest answer, not an error.
    /// </summary>
    public static AptitudeAllocation Baseline(
        IReadOnlyDictionary<string, long> planSharePermille, long theta, AptitudeTuning tuning)
    {
        if (planSharePermille is null) throw new ArgumentNullException(nameof(planSharePermille));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (planSharePermille.Count == 0) return AptitudeAllocation.Empty;

        var budget = PointBudget.PointsFor(AllocationScope.Commander, theta, tuning);
        if (budget == 0) return AptitudeAllocation.Empty;

        var baseShares = new Dictionary<string, long>(StringComparer.Ordinal);
        var remainders = new Dictionary<string, long>(StringComparer.Ordinal);
        long allocated = 0;
        foreach (var (aptId, sharePermille) in planSharePermille)
        {
            if (!AptitudeCatalog.IsAptitudeId(aptId))
                throw new ArgumentException($"pool share names unknown aptitude id '{aptId}'", nameof(planSharePermille));
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
                allocation += AptitudeAllocation.Single(AllocationScope.Commander, aptId, points);
        }
        return allocation;
    }
}
