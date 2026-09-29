using FusionRpg.Core.Items.Uniques;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>D4.26 (spec-unique-pipeline.md §4): the rung-80 per-million gate for the extend-action-slot
/// grant — the first per-million integer RNG gate in this codebase (confirmed by a dedicated search:
/// `IAtomRandom.NextPerMille` tops out at 1000 and cannot express a 100-per-million rate).</summary>
public class ExtendSlotRollTests
{
    [Fact]
    public void A_zero_chance_never_hits()
    {
        for (long seed = 0; seed < 200; seed++)
            Assert.False(ExtendSlotRoll.Hit(seed, chanceMicro: 0));
    }

    [Fact]
    public void A_million_in_a_million_chance_always_hits()
    {
        for (long seed = 0; seed < 200; seed++)
            Assert.True(ExtendSlotRoll.Hit(seed, chanceMicro: 1_000_000));
    }

    [Fact]
    public void Same_seed_and_chance_reproduce_the_same_result()
    {
        var first = ExtendSlotRoll.Hit(424242, chanceMicro: 100);
        var second = ExtendSlotRoll.Hit(424242, chanceMicro: 100);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_seeds_do_not_all_agree_at_a_mid_range_chance()
    {
        // Not every seed can hit the SAME way at a genuinely mid-range chance -- proves the draw
        // actually reads the seed rather than returning a constant.
        var results = new HashSet<bool>();
        for (long seed = 0; seed < 500 && results.Count < 2; seed++)
            results.Add(ExtendSlotRoll.Hit(seed, chanceMicro: 500_000));

        Assert.Equal(2, results.Count);
    }

    /// <summary>The shipped tunable's own real rate (`gk-core/data/tuning/dungeon.v1.json`'s
    /// `loot.extendSlotChanceMicro: 100`) — 100 per million over a large sample lands within a wide,
    /// generous band around the theoretical rate, matching this program's own established "sampled,
    /// not pinned" precedent (`SlotFillTests`, `EventDrawTests`) rather than asserting an exact count.</summary>
    [Fact]
    public void The_shipped_100_per_million_rate_lands_in_a_generous_band_over_a_large_sample()
    {
        const long chanceMicro = 100;
        const int trials = 200_000;
        var hits = 0;
        for (long seed = 0; seed < trials; seed++)
            if (ExtendSlotRoll.Hit(seed, chanceMicro)) hits++;

        // Expected ~20 hits over 200,000 trials at 100-per-million; a generous 3x band either side
        // absorbs sampling noise without being wide enough to pass a badly broken gate (e.g. one that
        // silently reads per-mille instead of per-million, which would land ~2,000x too high).
        Assert.InRange(hits, 2, 80);
    }

    [Fact]
    public void Negative_rollSeed_does_not_throw()
    {
        // rollSeed casts to ulong internally (unchecked) -- a negative long is a legal input this
        // codebase's other Instantiator-adjacent rolls already accept without special-casing.
        var result = ExtendSlotRoll.Hit(-1, chanceMicro: 1_000_000);
        Assert.True(result);
    }
}
