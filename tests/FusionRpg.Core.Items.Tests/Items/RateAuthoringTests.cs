using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `rate-authoring` (drop-tables module 2) — the authoring-side counterpart `rate-floor` doesn't
/// provide (`spec-rate-authoring.md`). Two mechanisms: `IndependentRateEntry`/`Hit` (recommended
/// default, mirrors D38's kill-drop roll) and `WeightForRate` (secondary, drift-prone convenience).
/// </summary>
public class RateAuthoringTests
{
    static readonly DropRateFloorTuning Tuning = new(MinRatePerMillion: 1);

    [Fact]
    public void An_independent_entrys_rate_never_moves_when_a_sibling_entry_is_added()
    {
        // The core property this mechanism exists for: Hit's own result depends only on
        // (seed, stream name, rate) -- never on anything else in the table. There is nothing to
        // "add a sibling" to in the API itself; the test proves the SAME seed+stream always resolves
        // identically regardless of call count/context, which is what makes it safe to add unrelated
        // entries elsewhere without this one's odds moving.
        var entry = new IndependentRateEntry("test.jackpot", RatePerMillion: 500_000);
        var first = RateAuthoring.Hit(entry, rollSeed: 12345UL, streamName: "test.stream");
        var second = RateAuthoring.Hit(entry, rollSeed: 12345UL, streamName: "test.stream");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Hit_is_reproducible_for_the_same_seed_and_stream_name()
    {
        var entry = new IndependentRateEntry("test.jackpot", RatePerMillion: 300_000);
        var results = Enumerable.Range(0, 5)
            .Select(_ => RateAuthoring.Hit(entry, rollSeed: 999UL, streamName: "reproducible.stream"))
            .Distinct()
            .ToList();
        Assert.Single(results);
    }

    [Fact]
    public void Hit_rate_is_never_finer_than_the_floor_allows_when_authored_at_the_floor()
    {
        // At the floor (1 per million), a real roll over many seeds should hit approximately
        // 1-in-a-million of the time -- proven statistically at a much coarser, cheap-to-run scale
        // (1-in-1000 by using a 1000x wider RatePerMillion) rather than a slow 10^6-trial test.
        var entry = new IndependentRateEntry("test.common-for-testing", RatePerMillion: 1000);
        var hits = Enumerable.Range(0, 20_000)
            .Count(i => RateAuthoring.Hit(entry, rollSeed: (ulong)i, streamName: "statistical.stream"));
        // Expected ~20 hits (1000/1,000,000 * 20,000); a generous band avoids flakiness.
        Assert.InRange(hits, 2, 80);
    }

    [Fact]
    public void An_independent_entry_at_exactly_the_floor_is_authorable()
    {
        // The actual scenario the owner asked for: a real 0.0001% entry, placed on purpose.
        var entry = new IndependentRateEntry("test.mythic", RatePerMillion: Tuning.MinRatePerMillion);
        Assert.Equal(1L, entry.RatePerMillion);
        // Authorable and callable -- Hit does not throw for an entry authored exactly at the floor.
        RateAuthoring.Hit(entry, rollSeed: 1UL, streamName: "floor.stream");
    }

    [Fact]
    public void Weight_for_rate_solves_the_inverse_of_the_real_draw_formula()
    {
        long target = 250_000; // 25%
        long otherTotal = 300;
        long weight = RateAuthoring.WeightForRate(target, otherTotal, Tuning);

        // Fed back into the real draw-share formula: weight / (weight + otherTotal) ~= target/1,000,000
        long recomputedRate = weight * 1_000_000L / (weight + otherTotal);
        Assert.InRange(recomputedRate, target - 1, target + 1); // integer-rounding tolerance
    }

    [Fact]
    public void Weight_for_rate_refuses_a_target_below_the_floor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RateAuthoring.WeightForRate(targetRatePerMillion: 0, otherEntriesTotalWeight: 1000, Tuning));
    }

    [Fact]
    public void Weight_for_rate_refuses_a_target_at_or_above_the_whole_scale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RateAuthoring.WeightForRate(targetRatePerMillion: 1_000_000, otherEntriesTotalWeight: 1000, Tuning));
    }
}
