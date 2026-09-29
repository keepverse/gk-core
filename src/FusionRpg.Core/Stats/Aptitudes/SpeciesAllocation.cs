namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// `creature-type-allocation` (module 5) — makes <see cref="AllocationScope.CreatureType"/> real
/// (spec-creature-type-allocation.md). Pure math only: the DB-facing compose-at-read entry point lives in
/// `RpgStore.Aptitudes.cs` (`EffectiveSpeciesAllocation`), which calls <see cref="Baseline"/> here —
/// this type never touches a store, matching `SpeciesBuildPlanner`'s own Core-only discipline.
/// </summary>
public static class SpeciesAllocation
{
    /// <summary>The one place the CreatureType `scope_key` is encoded (spec's own "one place beside the
    /// Commander encoding" rule) — mirrors `AptitudeEndpoints.ScopeKey(playerId)`'s
    /// <c>"player:{id}"</c> shape, extended with the species so two players (decision 10) or two
    /// species never collide.
    ///
    /// <para><b>And, since solid-remediation T4.1 (S1/S3), with the EMPIRE.</b> The key carried a
    /// player and a species and no empire, so every species lookup resolved to whoever asked: a lawn
    /// zombie's progression was read under the human player's id, and Zomboss's empire had no key of
    /// its own for anything to credit. Its previous doc anticipated "two players … or two species" —
    /// two empires was the dimension nobody had needed yet.</para>
    ///
    /// <para><b>Dave keeps the exact old string, which is why this needs no migration.</b> Every row
    /// ever written under the two-argument form was written for the player's own empire, so mapping
    /// <see cref="Commanders.EmpireId.Dave"/> onto the unchanged shape is the historically accurate
    /// reading rather than a compatibility hack — a live database keeps resolving every override it
    /// already holds, and only a non-player empire mints a new key.
    /// </summary>
    public static string ScopeKey(long playerId, Commanders.EmpireId empire, string speciesId) =>
        empire == Commanders.EmpireId.Dave
            ? $"player:{playerId}:species:{speciesId}"
            : $"player:{playerId}:empire:{EmpireToken(empire)}:species:{speciesId}";

    /// <summary>
    /// Which empire owns a side's species progression. The lawn's two sides ARE two empires: plants are
    /// the player's (Dave), zombies are Zomboss's. This is the whole of S1 — the resolve had no way to
    /// ask the question, so it always answered "the player".
    /// </summary>
    public static Commanders.EmpireId EmpireForSide(StatSide side) =>
        side == StatSide.Zombie ? Commanders.EmpireId.Zomboss : Commanders.EmpireId.Dave;

    /// <summary>
    /// The empire's own token in a species scope key. <see cref="Commanders.EmpireId"/>'s value is the
    /// authored, lower-cased faction id, so the persisted strings for the two well-known empires are
    /// byte-identical to the retired enum's switch, and a new empire (open by design) mints its own
    /// token instead of throwing.
    /// </summary>
    static string EmpireToken(Commanders.EmpireId empire) => empire.Value;

    /// <summary>
    /// The baseline — computed, never persisted (audit finding A9): the plan's share vector (permille,
    /// summing to 1000) scaled by the CreatureType budget at this species level. **Zero shares →
    /// <see cref="AptitudeAllocation.Empty"/>, zero budget → <see cref="AptitudeAllocation.Empty"/>**
    /// (never a thrown error) — a species missing from the plan, or a never-levelled species
    /// (`speciesLevel &lt;= 1` ⇒ `PointBudget.CreatureTypeSourceFromLevel` = 0 ⇒ budget = 0), both
    /// legitimately have no baseline yet. Widened before multiplying, largest-remainder rounding (same
    /// rule `SpeciesBuildPlanner` uses) so the twelve shares' points sum to exactly the budget rather
    /// than losing a few points to integer-division truncation on every read.
    /// </summary>
    public static AptitudeAllocation Baseline(
        IReadOnlyDictionary<string, long> planSharePermille, long speciesLevel, AptitudeTuning tuning)
    {
        if (planSharePermille is null) throw new ArgumentNullException(nameof(planSharePermille));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (planSharePermille.Count == 0) return AptitudeAllocation.Empty;

        var source = PointBudget.CreatureTypeSourceFromLevel(speciesLevel);
        var budget = PointBudget.PointsFor(AllocationScope.CreatureType, source, tuning);
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
                allocation += AptitudeAllocation.Single(AllocationScope.CreatureType, aptId, points);
        }
        return allocation;
    }
}
