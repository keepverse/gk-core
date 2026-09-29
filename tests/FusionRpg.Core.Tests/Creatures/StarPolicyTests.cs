using System;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>F1: star caps, sacrifice curve, per-star bonuses, promotion gate, cost table —
/// the locked numbers from spec-creature-fusion.md (tuning is ask-first).</summary>
public class StarPolicyTests
{
    [Theory]
    // Doubled 2026-09-05 alongside MaxStar 5 -> 10, preserving the rarity shape exactly (3/4/5 -> 6/8/10).
    [InlineData(CreatureRarity.Chaff, 6)]
    [InlineData(CreatureRarity.Cultivated, 8)]
    [InlineData(CreatureRarity.Heirloom, 10)]
    [InlineData(CreatureRarity.Sunwoven, 10)]
    public void Star_caps_by_rarity(CreatureRarity rarity, int cap) =>
        Assert.Equal(cap, StarPolicy.StarCap(rarity));

    [Fact]
    public void Sacrifice_curve_is_n_plus_one()
    {
        Assert.Equal(2, StarPolicy.SacrificesForStar(1));
        Assert.Equal(4, StarPolicy.SacrificesForStar(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => StarPolicy.SacrificesForStar(0));
        // Stars run 1..MaxStar, raised 5 -> 10 by the owner on 2026-09-05. Asserted against the
        // constant rather than a literal, so the next range change moves one number, not two.
        Assert.Equal(StarPolicy.MaxStar + 1, StarPolicy.SacrificesForStar(StarPolicy.MaxStar));
        Assert.Throws<ArgumentOutOfRangeException>(() => StarPolicy.SacrificesForStar(StarPolicy.MaxStar + 1));
    }

    [Fact]
    public void Per_star_bonuses_are_locked()
    {
        Assert.Equal(30, StarPolicy.PerStarPowerMilli);
        Assert.Equal(30, StarPolicy.PerStarDefenseMilli);
    }

    [Fact]
    public void Promotion_gates_on_max_star_once_below_the_top_rung()
    {
        // "The top rung" is Almanac (ordinal 100, the TRUE top of the ten-rung ladder) — not
        // Sunwoven, which is merely where Legendary's species happened to migrate to
        // (seed-to-concrete T4.1). A Sunwoven creature CAN now promote further, to Almanac; only
        // Almanac itself has no ceiling above it.
        // Read each cap rather than repeating it: the ladder doubled on 2026-09-05 (3/4/5 -> 6/8/10)
        // and a literal here would have to be chased every time it moves again.
        var chaffCap = StarPolicy.StarCap(CreatureRarity.Chaff);
        Assert.True(StarPolicy.CanPromote(CreatureRarity.Chaff, chaffCap, promoted: false));
        Assert.False(StarPolicy.CanPromote(CreatureRarity.Chaff, chaffCap - 1, promoted: false)); // not maxed
        Assert.False(StarPolicy.CanPromote(CreatureRarity.Chaff, chaffCap, promoted: true));  // once only
        Assert.True(StarPolicy.CanPromote(
            CreatureRarity.Sunwoven, StarPolicy.StarCap(CreatureRarity.Sunwoven), promoted: false)); // still promotes to Almanac
        Assert.False(StarPolicy.CanPromote(
            CreatureRarity.Almanac, StarPolicy.StarCap(CreatureRarity.Almanac), promoted: false)); // no ceiling above the true top
        Assert.True(StarPolicy.CanPromote(
            CreatureRarity.Heirloom, StarPolicy.StarCap(CreatureRarity.Heirloom), promoted: false));
    }

    [Fact]
    public void Star_reward_is_paired_to_the_triangular_sacrifice_cost()
    {
        // The defect (effort-power reconciliation M4): cumulative sacrifices to star n are
        // C(n) = n(n+3)/2, triangular, while the reward was perStar * n, linear. Reward per
        // sacrifice was 60/(n+3), so star 5 was an exactly 2x worse deal than star 1.
        // The fix indexes the reward on the cost, anchored so star 5 keeps its old value.
        static int Cumulative(int star)
        {
            var total = 0;
            for (var k = 1; k <= star; k++) total += StarPolicy.SacrificesForStar(k);
            return total;
        }

        // The anchor holds: nobody's existing star-5 creature changes value.
        Assert.Equal(150, StarPolicy.StarPowerMilli(StarPolicy.ReferenceStar));
        Assert.Equal(150, StarPolicy.StarDefenseMilli(StarPolicy.ReferenceStar));
        Assert.Equal(0, StarPolicy.StarPowerMilli(0));

        // Reward per sacrifice is now FLAT, which is the property the old curve did not have.
        // Computed, not copied: ratio(n) = R(n)/C(n) should be constant across the whole ladder.
        var reference = (double)StarPolicy.StarPowerMilli(1) / Cumulative(1);
        for (var star = 1; star <= StarPolicy.MaxStar; star++)
        {
            var ratio = (double)StarPolicy.StarPowerMilli(star) / Cumulative(star);
            // Within half a per-mille of flat -- the only drift is integer rounding of R(n).
            Assert.True(Math.Abs(ratio - reference) < 0.5,
                $"star {star}: reward/sacrifice {ratio:0.###} drifted from {reference:0.###}");
        }

        // And it is monotonic, so a deeper star is never a smaller bonus.
        for (var star = 2; star <= StarPolicy.MaxStar; star++)
            Assert.True(StarPolicy.StarPowerMilli(star) > StarPolicy.StarPowerMilli(star - 1));
    }

    [Fact]
    public void Cost_table_matches_the_spec()
    {
        var merge = FusionCostTable.StarMerge(CreatureRarity.Cultivated);
        Assert.Equal((50L, CreatureRarity.Cultivated, 1, 1), (merge.Souls, merge.ShardRarity, merge.ShardCount, merge.EssenceCount));

        // Promotion escalates per rung since 2026-09-05 (effort-power M5) instead of a flat 200 --
        // it sat flat while Recipe escalated 150 -> 1000 in the same file.
        var promo = FusionCostTable.Promotion(CreatureRarity.Heirloom);
        Assert.Equal((820L, CreatureRarity.Heirloom, 4, 7), (promo.Souls, promo.ShardRarity, promo.ShardCount, promo.EssenceCount));
        var promoLow = FusionCostTable.Promotion(CreatureRarity.Chaff);
        Assert.Equal((150L, CreatureRarity.Chaff, 2, 2), (promoLow.Souls, promoLow.ShardRarity, promoLow.ShardCount, promoLow.EssenceCount));
        Assert.True(promo.Souls > promoLow.Souls, "promotion must cost more at a higher rung");

        Assert.Equal((150L, CreatureRarity.Chaff, 2, 2), Tuple(FusionCostTable.Recipe(CreatureRarity.Cultivated)));
        Assert.Equal((400L, CreatureRarity.Cultivated, 3, 4), Tuple(FusionCostTable.Recipe(CreatureRarity.Heirloom)));
        Assert.Equal((1000L, CreatureRarity.Heirloom, 4, 8), Tuple(FusionCostTable.Recipe(CreatureRarity.Sunwoven)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FusionCostTable.Recipe(CreatureRarity.Chaff));

        static (long, CreatureRarity, int, int) Tuple(FusionCost c) =>
            (c.Souls, c.ShardRarity, c.ShardCount, c.EssenceCount);
    }

    [Fact]
    public void Shard_ids_round_trip_the_material_catalog()
    {
        foreach (var rarity in new[] { CreatureRarity.Chaff, CreatureRarity.Cultivated, CreatureRarity.Heirloom, CreatureRarity.Sunwoven })
            Assert.True(CreatureMaterialCatalog.IsKnown("shard." + rarity.ToId()));
    }
}
