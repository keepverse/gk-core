using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Loam;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-map W55 (spec-sector-development.md §3, empire-economy-ssot.md A8): "development must
/// raise yield faster than it raises upkeep, or nobody will ever develop." Both halves of that
/// comparison are plain per-level rates (`loam.v2.json`'s `development.yieldPerLevel` and
/// `upkeep.developmentUpkeepPerLevel`), not per-mille quantities — there is no division anywhere on
/// this path, confirmed by reading both formulas rather than assumed, so the acceptance's own
/// "divides by 1000 exactly once, last" clause is vacuously satisfied here (there is nothing to
/// divide) rather than genuinely exercised.
/// </summary>
public class DevelopmentYieldTests
{
    [Fact]
    public void DevelopmentYield_is_the_level_times_the_given_rate()
    {
        Assert.Equal(0, DevelopmentYield.For(0, yieldPerLevel: 8));
        Assert.Equal(8, DevelopmentYield.For(1, yieldPerLevel: 8));
        Assert.Equal(40, DevelopmentYield.For(5, yieldPerLevel: 8));
    }

    [Fact]
    public void The_real_shipped_rate_already_satisfies_A8()
    {
        Assert.True(LoamPolicy.DevelopmentYieldPerLevel > LoamPolicy.DevelopmentUpkeepPerLevel,
            "A8 (empire-economy-ssot.md): development must raise yield faster than it raises upkeep.");
    }

    /// <summary>
    /// The acceptance's own "asserted across the whole authored level range, not at one sample
    /// point" — a generous, deliberately large bound (there is no engine-enforced cap on
    /// `DevelopmentLevel`, AGENTS.md's no-hard-progression-ceiling rule), walking every level's own
    /// *marginal* yield against its own *marginal* upkeep, one level at a time, rather than deriving
    /// the comparison once algebraically and trusting it to hold everywhere. A future edit that made
    /// either formula non-linear would still be caught here, at the exact level it broke. Reads the
    /// real, configured <see cref="LoamPolicy.DevelopmentYieldPerLevel"/>/<see cref="LoamPolicy.DevelopmentUpkeepPerLevel"/>
    /// — the actual shipped numbers, not a hand-picked local pair that could drift from them unnoticed.
    /// </summary>
    [Fact]
    public void A8_holds_for_every_level_across_a_generous_authored_range_marginal_yield_beats_marginal_upkeep()
    {
        const int upperBound = 2000;
        var yieldPerLevel = LoamPolicy.DevelopmentYieldPerLevel;

        for (var level = 1; level <= upperBound; level++)
        {
            var marginalYield = DevelopmentYield.For(level, yieldPerLevel) - DevelopmentYield.For(level - 1, yieldPerLevel);
            var marginalUpkeep =
                LoamPolicy.DevelopmentAndDangerUpkeep(level, dangerBand: 0)
                - LoamPolicy.DevelopmentAndDangerUpkeep(level - 1, dangerBand: 0);

            Assert.True(marginalYield > marginalUpkeep,
                $"A8 (empire-economy-ssot.md): level {level}'s marginal yield ({marginalYield}) must exceed " +
                $"its marginal upkeep ({marginalUpkeep}), or nobody will ever develop.");
        }
    }

    [Fact]
    public void Every_magnitude_on_the_path_is_long_never_int_or_float()
    {
        Assert.IsType<long>(DevelopmentYield.For(1_000_000, 8));
        Assert.IsType<long>(LoamPolicy.DevelopmentYieldPerLevel);
        Assert.IsType<long>(LoamPolicy.DevelopmentAndDangerUpkeep(1_000_000, 0));
    }

    [Fact]
    public void A_combination_large_enough_to_overflow_throws_rather_than_wraps()
    {
        // The real shipped rate (8) can never overflow within `developmentLevel`'s own `int` range
        // (int.MaxValue * 8 sits far below long.MaxValue) — parameterizing lets this test supply a
        // genuinely huge local rate instead, without touching the shared `LoamPolicy` singleton,
        // proving `checked` actually guards this path rather than merely being documented to.
        Assert.Throws<OverflowException>(() => DevelopmentYield.For(int.MaxValue, yieldPerLevel: long.MaxValue / 2));
    }
}
