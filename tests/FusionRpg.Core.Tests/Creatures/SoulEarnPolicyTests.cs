using FusionRpg.Core.Creatures;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// The v2 earn policy, post-T3.6 (spec-caps-reconcile.md §2.3, SSOT §11.7/§11.7a, 2026-08-24): the
/// flat per-match kill cap and the daily-victory decay are gone, replaced by
/// <c>amount × contentScale(Theta)</c> — the same unchanged constants, byte-identical at the pin.
/// </summary>
public class SoulEarnPolicyTests
{
    static PowerTuning TuningAt(long bMilli) => PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    const int Pin = 20; // contentScale(20) == 1.000 for any B — T1.1/T1.2's own pin proof

    [Fact]
    public void Kill_earn_is_byte_identical_to_KillDelta_at_the_pin()
    {
        foreach (var bMilli in new long[] { 0, 200, 400, 1000 })
            Assert.Equal(SoulEarnPolicy.KillDelta, SoulEarnPolicy.KillEarn(Pin, TuningAt(bMilli)));
    }

    [Fact]
    public void Kill_earn_is_uncapped_past_the_old_fifty_per_match_ceiling()
    {
        // T3.6: KillCapPerMatch is deleted. The 51st, 100th, 500th earning kill in one match all pay
        // the same as the 1st — no plateau, matching "kill income uncapped" (spec's own testing table).
        var tuning = TuningAt(400);
        Assert.Equal(SoulEarnPolicy.KillEarn(Pin, tuning), SoulEarnPolicy.KillEarn(Pin, tuning));
        for (var i = 0; i < 500; i++)
            Assert.Equal(SoulEarnPolicy.KillDelta, SoulEarnPolicy.KillEarn(Pin, tuning));
    }

    [Theory]
    [InlineData(50, 3)]   // KillDelta=1 x contentScale(50)=2.764 -> round-half-away-from-zero = 3
    [InlineData(100, 7)]  // KillDelta=1 x contentScale(100)=6.882 -> 7
    public void Kill_earn_scales_with_content_depth(int thetaEnemy, int expected)
    {
        // Expected values hand-derived from T3.4's own independently-verified ContentScale.Milli
        // figures (ScalingTable_AtTheDecidedDial_MatchesSsotWithinRounding: 2764/6882 at B=400),
        // not read off the implementation under test.
        Assert.Equal(expected, SoulEarnPolicy.KillEarn(thetaEnemy, TuningAt(400)));
    }

    [Fact]
    public void Match_end_earn_is_byte_identical_at_the_pin_no_more_daily_decay()
    {
        var tuning = TuningAt(400);
        Assert.Equal(SoulEarnPolicy.VictoryDelta, SoulEarnPolicy.MatchEndEarn(true, Pin, tuning));
        Assert.Equal(SoulEarnPolicy.DefeatDelta, SoulEarnPolicy.MatchEndEarn(false, Pin, tuning));

        // T3.6: VictoryFullPerDay is deleted -- the 1st and the 100th victory of the day pay the same.
        for (var i = 0; i < 100; i++)
            Assert.Equal(SoulEarnPolicy.VictoryDelta, SoulEarnPolicy.MatchEndEarn(true, Pin, tuning));
    }

    [Fact]
    public void Match_end_earn_scales_with_run_depth()
    {
        var tuning = TuningAt(400);
        var scaleMilli = ContentScale.Milli(100, tuning);
        Assert.Equal(ContentScale.Apply(SoulEarnPolicy.VictoryDelta, scaleMilli), SoulEarnPolicy.MatchEndEarn(true, 100, tuning));
        Assert.Equal(ContentScale.Apply(SoulEarnPolicy.DefeatDelta, scaleMilli), SoulEarnPolicy.MatchEndEarn(false, 100, tuning));
    }

    // ---- the stall-farm regression (SSOT §11.7a, spec-caps-reconcile.md §5) --------------------------

    [Fact]
    public void Stall_farm_regression_clean_win_beats_stall_defeat_on_souls_per_minute()
    {
        // SSOT §11.7a's own worked scenario: a clean win (40 kills, victory, 3 min) against a stall
        // defeat farming weak early spawns (80 or 200 kills at a much lower Theta, 12/30 min). The
        // regression asserts souls PER MINUTE -- what the original +2/kill incident was actually
        // measured in -- not souls per match, which a cap could satisfy without fixing the real rate.
        //
        // Exact figures differ from the SSOT's own illustrative table (140/50/88): that table's
        // per-scenario totals read as continuous arithmetic for the write-up, while this formula
        // rounds PER KILL (SSOT §11.7a's own text: "soulsPerKill = KillDelta x contentScale(Theta)",
        // singular). At KillDelta=1 and Theta_enemy=5 (weak farmed spawns), contentScale(5) is small
        // enough that ContentScale.Apply(1, ...) rounds every individual kill to exactly zero -- an
        // even stronger deterrent than a small positive value would be, not a weaker one, and it does
        // not change the property under test.
        var tuning = TuningAt(400);
        const int thetaCleanRun = 20;  // the pin -- a normal run, unscaled
        const int thetaStallEnemy = 5; // deliberately weak, farmed spawns

        Assert.Equal(0, SoulEarnPolicy.KillEarn(thetaStallEnemy, tuning)); // the deterrent, made explicit

        var cleanWinSouls = 40 * SoulEarnPolicy.KillEarn(thetaCleanRun, tuning)
                             + SoulEarnPolicy.MatchEndEarn(true, thetaCleanRun, tuning);
        var stallDefeat80Souls = 80 * SoulEarnPolicy.KillEarn(thetaStallEnemy, tuning)
                                  + SoulEarnPolicy.MatchEndEarn(false, thetaCleanRun, tuning);
        var stallDefeat200Souls = 200 * SoulEarnPolicy.KillEarn(thetaStallEnemy, tuning)
                                   + SoulEarnPolicy.MatchEndEarn(false, thetaCleanRun, tuning);

        Assert.Equal(140, cleanWinSouls);
        Assert.Equal(25, stallDefeat80Souls);
        Assert.Equal(25, stallDefeat200Souls);

        var cleanWinPerMinute = cleanWinSouls / 3.0;
        var stall80PerMinute = stallDefeat80Souls / 12.0;
        var stall200PerMinute = stallDefeat200Souls / 30.0;

        Assert.True(cleanWinPerMinute > stall80PerMinute,
            $"clean win {cleanWinPerMinute}/min must beat stall-defeat {stall80PerMinute}/min");
        Assert.True(cleanWinPerMinute > stall200PerMinute,
            $"clean win {cleanWinPerMinute}/min must beat stall-defeat {stall200PerMinute}/min");
        // The longer stall-farm run pays the SAME total for MORE time -- strictly worse per minute,
        // reinforcing that grinding harder at the wrong depth never recovers the rate.
        Assert.True(stall200PerMinute < stall80PerMinute);
    }

    [Theory]
    [InlineData(CreatureRarity.Chaff, 25)]
    [InlineData(CreatureRarity.Cultivated, 75)]
    [InlineData(CreatureRarity.Heirloom, 200)]
    [InlineData(CreatureRarity.Sunwoven, 500)]
    public void Discovery_faucet_by_rarity(CreatureRarity rarity, int expected)
    {
        Assert.Equal(expected, SoulEarnPolicy.DiscoveryDelta(rarity));
    }
}
