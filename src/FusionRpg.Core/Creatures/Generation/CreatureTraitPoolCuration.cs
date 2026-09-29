namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// Curates <see cref="CreatureSpeciesDef.TraitPool"/> — <see cref="CreatureTraitCatalog"/>'s closed ~14-id
/// gameplay vocabulary — for the anchor-pipeline roster. <see cref="ConcreteSpecies.TraitPool"/>
/// deliberately keeps carrying the anchor's own OPEN, free-form flavor text (never read by this
/// class; see <see cref="ConcreteSpeciesMapper.ToCreatureSpeciesDef"/>'s own comment) — this is the one
/// place that bridges to the closed vocabulary instead, called only from that mapper so both hosts
/// (the Server's DB-backed snapshot and the Injector's seed-backed one) curate identically.
///
/// <para>Two sources, in priority order:</para>
/// <para>1. <see cref="CreatureSpeciesLegacyTraitPoolOverlap.BySpeciesId"/> — a frozen, one-time extraction
/// (2026-09-06) of the legacy, pre-anchor 84-species compiled catalog's own real, hand-authored
/// <c>TraitPool</c> arrays, taken immediately before that catalog's own T4.8-step-7 deletion — ported
/// forward verbatim for every species still present in the anchor-pipeline roster today, rather than
/// re-deriving what a human already picked once. Deliberately NOT a live read of the (now-deleted)
/// legacy catalog: this class must keep working after that catalog is gone.</para>
/// <para>2. Every other species gets a deterministic pick from a private FNV-1a hash — copied from
/// (not referenced from) the legacy <c>CreatureSpeciesGenerator.Hash</c>/<c>TraitsFor</c> shape for the
/// same reason: that generator is also deleted under T4.8 step 7. Pool membership is a closed
/// vocabulary tied 1:1 to <see cref="CreatureTraitCatalog"/>'s own 14 hardcoded ids (adding a trait
/// already means a code change there too), so it stays a structural constant here, not a
/// <c>gk-core/data/tuning</c> file — the same reasoning the legacy generator already applied to this exact
/// data.</para>
/// </summary>
public static class CreatureTraitPoolCuration
{
    static readonly string[] Combat =
        { "berserker", "regenerator", "soul-eater", "critical-hunter", "guardian", "swift" };
    static readonly string[] Personality =
        { "loyal", "greedy", "bloodthirsty", "coward", "genius" };

    public static IReadOnlyList<string> PickFor(string speciesId, CreatureRarity rarity, int gameTypeId)
    {
        var normalizedId = speciesId.Trim().ToLowerInvariant();
        if (CreatureSpeciesLegacyTraitPoolOverlap.BySpeciesId.TryGetValue(normalizedId, out var legacy))
            return legacy;

        var pool = new List<string>
        {
            Combat[(int)(Hash(gameTypeId, "curate-t1") % (uint)Combat.Length)],
            Personality[(int)(Hash(gameTypeId, "curate-t2") % (uint)Personality.Length)],
        };
        var third = Combat[(int)(Hash(gameTypeId, "curate-t3") % (uint)Combat.Length)];
        if (!pool.Contains(third)) pool.Add(third);

        if (CreatureRarityLadder.AtLeast(rarity, CreatureRarity.Heirloom))
            pool.Add(Hash(gameTypeId, "curate-essence") % 2 == 0 ? "void-touched" : "chaos-marked");
        if (CreatureRarityLadder.IsTopRung(rarity))
            pool.Add("immortal");

        return pool;
    }

    /// <summary>FNV-1a over (typeId, salt) — copied from the legacy, now-deleted
    /// <c>CreatureSpeciesGenerator.Hash</c> verbatim (same algorithm, same shape), not referenced from it,
    /// since that class is also removed under T4.8 step 7.</summary>
    static uint Hash(int typeId, string salt)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in salt)
            {
                h ^= ch;
                h *= 16777619u;
            }

            h ^= (uint)typeId;
            h *= 16777619u;
            return h;
        }
    }
}
