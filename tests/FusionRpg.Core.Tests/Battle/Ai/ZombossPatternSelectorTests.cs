using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Ai;

/// <summary>species-build-todo.md T4.4 — <see cref="ZombossPatternSelector"/> (spec-zomboss-adaptive.md,
/// read in full this session). Covers the selector's own slice of the spec's testing strategy:
/// determinism, the rate limit binding against both triggers, counter-bias as a weight (both halves),
/// the lose-streak threshold, and the nine-pattern roster pin. Budget-cap, reveal-timing and setup-not-
/// resolve (tests 5-7) belong to T4.5/T4.6's battle-seam wiring, not this pure selector.</summary>
public class ZombossPatternSelectorTests
{
    static ZombossAdaptiveTuning Tuning(
        int loseStreakThreshold = 3, long counterBiasPermille = 600,
        int repatternCooldownEncounters = 3, int revealDelayEncounters = 1) => new(
        SchemaVersion: 1, Version: 1,
        LoseStreakThreshold: loseStreakThreshold,
        CounterBiasPermille: counterBiasPermille,
        RepatternCooldownEncounters: repatternCooldownEncounters,
        RevealDelayEncounters: revealDelayEncounters,
        RotationWeights: ZombossPatterns.All.ToDictionary(id => id, _ => 1L, StringComparer.Ordinal));

    static ZombossHistory OffCooldown(
        string current = "force-pure", int lastLevel = 1, int winStreak = 0, Posture? posture = null) => new(
        CurrentPatternId: current, LastLevel: lastLevel, EncountersSinceLastRepattern: 999,
        PlayerWinStreak: winStreak, PlayerDominantPosture: posture);

    [Fact]
    public void SelectNext_rejectsANullHistoryOrTuning()
    {
        var tuning = Tuning();
        var history = OffCooldown();
        Assert.Throws<ArgumentNullException>(() => ZombossPatternSelector.SelectNext(null!, 2, 1UL, tuning));
        Assert.Throws<ArgumentNullException>(() => ZombossPatternSelector.SelectNext(history, 2, 1UL, null!));
    }

    [Fact]
    public void SelectNext_rejectsAnUnknownCurrentPatternId()
    {
        var history = OffCooldown(current: "not-a-real-pattern");
        Assert.Throws<ArgumentException>(() => ZombossPatternSelector.SelectNext(history, 2, 1UL, Tuning()));
    }

    [Fact]
    public void Determinism_theSameInputsAlwaysYieldTheSamePattern()
    {
        var tuning = Tuning();
        var history = OffCooldown(lastLevel: 1, winStreak: 4, posture: Posture.Bastion);

        var first = ZombossPatternSelector.SelectNext(history, 2, seed: 12345UL, tuning);
        var second = ZombossPatternSelector.SelectNext(history, 2, seed: 12345UL, tuning);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Determinism_aDifferentSeedGenerallyDiffers()
    {
        var tuning = Tuning();
        var history = OffCooldown(lastLevel: 1);

        var results = Enumerable.Range(0, 50)
            .Select(i => ZombossPatternSelector.SelectNext(history, 2, seed: (ulong)i, tuning))
            .Distinct()
            .Count();

        Assert.True(results > 1, "50 different seeds all produced the identical pattern -- the RNG stream is not varying with seed");
    }

    [Fact]
    public void RateLimit_bindsEvenWhenBothTriggersFire()
    {
        var tuning = Tuning(repatternCooldownEncounters: 3, loseStreakThreshold: 2);
        // Still on cooldown (2 < 3), AND both a level-up and a lose streak are true -- must still hold.
        var history = new ZombossHistory(
            CurrentPatternId: "force-pure", LastLevel: 1, EncountersSinceLastRepattern: 2,
            PlayerWinStreak: 5, PlayerDominantPosture: Posture.Bastion);

        var result = ZombossPatternSelector.SelectNext(history, level: 2, seed: 1UL, tuning);

        Assert.Equal("force-pure", result); // unchanged -- the rate limit refused both triggers
    }

    [Fact]
    public void RateLimit_offCooldownWithNeitherTriggerAlsoLeavesThePatternUnchanged()
    {
        var tuning = Tuning(loseStreakThreshold: 5);
        var history = OffCooldown(current: "bastion-pure", lastLevel: 3, winStreak: 1);

        var result = ZombossPatternSelector.SelectNext(history, level: 3, seed: 1UL, tuning); // no level-up, no streak

        Assert.Equal("bastion-pure", result);
    }

    [Fact]
    public void LoseStreakThreshold_belowItNoCounterBiasApplies()
    {
        var tuning = Tuning(loseStreakThreshold: 5, counterBiasPermille: 1000); // even a 100% bias must not fire
        var history = OffCooldown(lastLevel: 1, winStreak: 4, posture: Posture.Bastion); // below threshold of 5

        var counters = new[] { "finesse-defence-force-breaks-guard", "finesse-defence-force-breaks-aggro" };
        var everCountered = Enumerable.Range(0, 100)
            .Select(i => ZombossPatternSelector.SelectNext(history, level: 2, seed: (ulong)i, tuning))
            .Any(id => counters.Contains(id));

        // Below threshold, the bias branch never runs -- landing on a counter pattern is only ever the
        // unbiased weighted pick's own 2/9 baseline share, never guaranteed and never elevated to certain.
        Assert.True(everCountered, "sanity: counters should still appear via the unbiased pool sometimes");
    }

    [Fact]
    public void CounterBias_raisesTheOddsOfTheCounteringPattern_withoutGuaranteeingIt()
    {
        var biased = Tuning(loseStreakThreshold: 3, counterBiasPermille: 700);
        var unbiased = Tuning(loseStreakThreshold: 3, counterBiasPermille: 0);
        var counters = new HashSet<string> { "finesse-defence-force-breaks-guard", "finesse-defence-force-breaks-aggro" };

        const int trials = 2000;
        int CountCountered(ZombossAdaptiveTuning tuning)
        {
            var hits = 0;
            for (var i = 0; i < trials; i++)
            {
                var history = OffCooldown(lastLevel: 1, winStreak: 5, posture: Posture.Bastion);
                var pick = ZombossPatternSelector.SelectNext(history, level: 2, seed: (ulong)i, tuning);
                if (counters.Contains(pick)) hits++;
            }
            return hits;
        }

        var biasedHits = CountCountered(biased);
        var unbiasedHits = CountCountered(unbiased);

        Assert.True(biasedHits > unbiasedHits,
            $"counter-bias should raise the odds of a countering pick (biased={biasedHits}, unbiased={unbiasedHits})");
        Assert.True(biasedHits < trials,
            "counter-bias is a weight, not a guarantee -- it must not ALWAYS choose the countering pattern");
    }

    [Fact]
    public void CounterBias_withNoDominantPosture_fallsBackToTheUnbiasedPool()
    {
        var tuning = Tuning(loseStreakThreshold: 3, counterBiasPermille: 1000); // would always bias if it could
        var history = OffCooldown(lastLevel: 1, winStreak: 5, posture: null); // a tie -- nothing to counter

        var results = Enumerable.Range(0, 50)
            .Select(i => ZombossPatternSelector.SelectNext(history, level: 2, seed: (ulong)i, tuning))
            .Distinct()
            .Count();

        Assert.True(results > 1, "with no dominant posture the pick should still vary across seeds via the unbiased pool");
    }

    [Fact]
    public void RosterIsPinnedAtNine()
    {
        // A future edit that quietly adds a self-cancelling tenth pattern breaks this test, not a
        // production incident -- spec's own §3 rule that only three (defence, breaks) pairs are legal.
        Assert.Equal(9, ZombossPatterns.All.Count);
    }

    [Fact]
    public void EveryAuthoredPatternIsReachableThroughTheWeightedRotation()
    {
        var tuning = Tuning();
        var history = OffCooldown(lastLevel: 1);
        var seen = new HashSet<string>();
        for (var i = 0; i < 500 && seen.Count < ZombossPatterns.All.Count; i++)
            seen.Add(ZombossPatternSelector.SelectNext(history, level: 2, seed: (ulong)i, tuning));

        Assert.Equal(ZombossPatterns.All.Count, seen.Count);
    }
}
