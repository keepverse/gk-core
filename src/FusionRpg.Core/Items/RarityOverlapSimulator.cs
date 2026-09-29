using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Items;

/// <summary>
/// One rung's tier window plus its count-band floor, the shape this harness needs from `rarity`'s
/// seeded rows. <paramref name="AffixCount"/> is `PrefixRolls + SuffixRolls` — `chaff`'s is 0
/// ("the only rung with no pool", core.v1.json), and a rung with zero affixes contributes zero
/// magnitude to the channel family, never a rolled band sample.
/// </summary>
public readonly record struct RarityRungWindow(string RarityId, int MinTier, int MaxTier, int AffixCount);

/// <summary>
/// OD4's overlap mechanism, measured (ssot-rarity.md §3.5, item-ideal.md `rarity-bands` module 7).
/// Claimed here because the only would-be consumer (`spec-uniques.md`) declined to build a second
/// simulator, and the invariant belongs to the ladder this module seeds, not to any downstream lane.
///
/// All three variances act, at the precision the shipped schema allows: count uses the seeded FLOOR
/// only (`prefix_rolls + suffix_rolls`), never the true range, because `pool_rolls_max` does not exist
/// on the shipped schema yet (an ask-first, not decided — see `gk-data/packs/fusion/data/seed/rarity/README.md`). Using the
/// floor is the documented fallback (§5.3) and understates count separation, never overstates it.
/// Dropping count instead of flooring it is not a smaller version of the same measurement: two
/// `window`-step rungs share an identical tier window by design (`grafted`/`cultivated`,
/// `fused`/`chimeric`, `heirloom`/`firstseed`, `sunwoven`/`almanac`), so a two-variance model collapses
/// each of those pairs to a coin flip and the pooled invariant fails outright -- measured, not assumed.
///
/// Single channel family on purpose (SC4 forbids cross-family totals): every draw is `vitality`
/// `maxHp`, hp units, anchored on the two real numbers in atom-family-library.md §2a
/// (t1 = 10 fixed, t3 = 40-50 hp).
/// </summary>
public static class RarityOverlapSimulator
{
    /// <summary>§3.5's re-run seed — never change without re-measuring and re-pinning every CI band.</summary>
    public const ulong Seed = 20260822UL;

    /// <summary>§3.5's trial count, per rung.</summary>
    public const int RollsPerRung = 200_000;

    /// <summary>t1..t5 hp bands, §3.5's Method paragraph, verbatim.</summary>
    static readonly (int Min, int Max)[] TierBands =
    {
        (10, 12), (20, 25), (40, 50), (85, 100), (170, 205),
    };

    /// <summary>How many tiers the band table carries — five, matching the atom layer's `.t1`..`.t5`.</summary>
    public static int TierCount => TierBands.Length;

    /// <summary>
    /// One tier's inclusive magnitude band, in the same `vitality`/`maxHp` hp units every roll above
    /// uses. <b>Exposed, rather than copied</b>, because module 17's parity metric prices a unique's
    /// FIXED side against the rolled side and the two must be the same units by construction — a
    /// second copy of this table is how a comparison starts measuring nothing (spec-uniques.md's
    /// "never write a second parity simulator", applied to the data as well as the code).
    /// </summary>
    public static (int Min, int Max) TierBand(int tier)
    {
        if (tier < 1 || tier > TierBands.Length)
            throw new ArgumentOutOfRangeException(nameof(tier), $"tier {tier} is outside t1-t{TierBands.Length}");
        return TierBands[tier - 1];
    }

    /// <summary>
    /// A tier's band midpoint, rounded down. This is the "one rolled affix at the middle of the
    /// window" that the AE unit is defined against (ssot-sets.md §3.5, adopted by ssot-uniques.md
    /// §3.7), so it lives here beside the band it is the middle of.
    /// </summary>
    public static long TierMidpoint(int tier)
    {
        var (min, max) = TierBand(tier);
        return ((long)min + max) / 2;
    }

    /// <summary>
    /// Rolls <see cref="RollsPerRung"/> independent total magnitudes for one rung: the rung's
    /// <see cref="RarityRungWindow.AffixCount"/> (`PrefixRolls + SuffixRolls`, the count-band floor)
    /// independent affixes, each tier-uniform inside the window and magnitude-uniform inside its
    /// rolled tier, summed. All three of §3.5's variances (count, tier, magnitude) therefore act on
    /// every draw -- omitting count collapses `grafted` against `cultivated` (identical tier windows,
    /// one and two affixes respectively) to a coin flip, which is not what a shrinking overlap means.
    /// </summary>
    public static int[] RollMagnitudes(RarityRungWindow rung, ulong seed = Seed, int rolls = RollsPerRung)
    {
        // A rung with no affixes in its count band (chaff) never lands a roll in any family --
        // its total is a fixed zero, not a sample from its (otherwise inert) tier window.
        if (rung.AffixCount == 0)
            return new int[rolls];

        if (rung.MinTier < 1 || rung.MaxTier > TierBands.Length || rung.MinTier > rung.MaxTier)
            throw new ArgumentOutOfRangeException(nameof(rung), $"'{rung.RarityId}' window {rung.MinTier}-{rung.MaxTier} is outside t1-t{TierBands.Length}");

        var rng = SeededRng.DeriveStream(seed, $"rarity-overlap:{rung.RarityId}");
        var result = new int[rolls];
        var tierSpan = rung.MaxTier - rung.MinTier + 1;
        for (var i = 0; i < rolls; i++)
        {
            var total = 0;
            for (var affix = 0; affix < rung.AffixCount; affix++)
            {
                var tier = rung.MinTier + rng.NextInt(tierSpan);
                var (min, max) = TierBands[tier - 1];
                total += min + rng.NextInt(max - min + 1);
            }

            result[i] = total;
        }

        return result;
    }

    /// <summary>
    /// `U(n, k)`: the paired fraction of trials where a rung-`n` roll beats a rung-`n+k` roll — the
    /// same trial index on both sides, so this is `P(magnitude_n > magnitude_{n+k})` estimated over
    /// <see cref="RollsPerRung"/> independent draws per side, not a re-roll per pair.
    /// </summary>
    public static double UpsetRate(int[] rungNRolls, int[] rungNPlusKRolls)
    {
        if (rungNRolls.Length != rungNPlusKRolls.Length)
            throw new ArgumentException("paired comparison requires equal-length roll arrays");

        var wins = 0;
        for (var i = 0; i < rungNRolls.Length; i++)
            if (rungNRolls[i] > rungNPlusKRolls[i])
                wins++;

        return (double)wins / rungNRolls.Length;
    }
}
