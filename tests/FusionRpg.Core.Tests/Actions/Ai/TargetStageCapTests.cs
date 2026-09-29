using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `core-scorer` (module 1, CAI1.2, spec-core-scorer.md §3): proves the cap-before-work
/// placement moves no decision. `TargetStage` takes plain delegates rather than an `IBattleView`, so
/// these tests need no fake view — the identity is provable from the stage's own contract.
/// </summary>
public class TargetStageCapTests
{
    static ScoringWeights Weights() =>
        new(HitChance: 70, Objective: 50, Kill: 15, LowHp: 10, CannotCounter: 10, Round: 1, Risk: 120,
            AggressionRange: 2, MaxCandidatesScored: 32);

    static TargetCandidate Synthetic(string key, int index) => new(
        key, BaseTier: 0, Aggression: 0,
        HitChanceMilli: (index * 37) % 1000, ObjectiveClassMilli: (index * 13) % 1000,
        IsKillingBlow: index % 5 == 0, TargetMissingHpMilli: (index * 7) % 1000,
        TargetCanCounter: index % 3 == 0, IncomingThreatMilli: (index * 11) % 500);

    /// <summary>The byte-identity proof for the cap move: 40 live enemies, cap 32. Building every
    /// candidate THEN truncating (the pre-CAI1.2 shape) and truncating THEN building only the capped
    /// set (this stage) must choose the identical winner with the identical breakdown — because phase
    /// A preserves view order, the two are the SAME set built in the SAME order, just at a different
    /// point in the pipeline.</summary>
    [Fact]
    public void Cap_before_work_selects_the_same_candidate_as_cap_after_work()
    {
        const int total = 40;
        const int cap = 32;
        var enemyKeys = Enumerable.Range(0, total).Select(i => $"e{i:D2}").ToList();
        var liveActorKeys = new List<string> { "me" };
        liveActorKeys.AddRange(enemyKeys);

        int SideOf(string k) => k == "me" ? 0 : 1;
        bool IsReadable(string k) => true;

        // Cap AFTER work: build every candidate, then truncate -- the shape `AiScoring.ChooseTarget`
        // used before this stage existed.
        var buildAll = new List<TargetCandidate>(total);
        for (var i = 0; i < enemyKeys.Count; i++)
            buildAll.Add(Synthetic(enemyKeys[i], i));
        var afterWork = buildAll.Count > cap ? buildAll.GetRange(0, cap) : buildAll;

        // Cap BEFORE work: TargetStage only ever builds the capped set.
        var beforeWork = TargetStage.BuildCapped("me", mySide: 0, liveActorKeys, SideOf, IsReadable, cap,
            (string k, out TargetCandidate c) => { c = Synthetic(k, enemyKeys.IndexOf(k)); return true; });

        Assert.Equal(afterWork, beforeWork);

        var w = Weights();
        var afterChosen = CandidateScorer.ChooseTarget(afterWork, currentRound: 3, w);
        var beforeChosen = CandidateScorer.ChooseTarget(beforeWork, currentRound: 3, w);
        Assert.NotNull(afterChosen);
        Assert.Equal(afterChosen, beforeChosen);
        Assert.Equal(
            CandidateScorer.ScoreBreakdownOf(afterChosen!.Value, 3, w),
            CandidateScorer.ScoreBreakdownOf(beforeChosen!.Value, 3, w));
    }

    [Fact]
    public void Phase_A_filters_run_in_view_order()
    {
        var liveActorKeys = new[] { "me", "ally1", "enemy1", "fogged1", "enemy2", "ally2", "enemy3" };
        int SideOf(string k) => k is "me" or "ally1" or "ally2" ? 0 : 1;
        bool IsReadable(string k) => k != "fogged1";

        var seenInOrder = new List<string>();
        var built = TargetStage.BuildCapped("me", mySide: 0, liveActorKeys, SideOf, IsReadable, maxCandidatesScored: 10,
            (string k, out TargetCandidate c) => { seenInOrder.Add(k); c = Synthetic(k, seenInOrder.Count); return true; });

        // Self excluded, both allies excluded, the fogged enemy excluded, and the survivors keep the
        // view's own listed order -- never reordered by side, readability, or anything else.
        Assert.Equal(new[] { "enemy1", "enemy2", "enemy3" }, seenInOrder);
        Assert.Equal(3, built.Count);
    }

    /// <summary>The work bound is applied BEFORE the expensive build, not after: a counting fake for
    /// the per-candidate build is invoked at most `MaxCandidatesScored` times, counted -- never timed.</summary>
    [Fact]
    public void Per_candidate_inputs_are_computed_only_for_the_capped_set()
    {
        const int total = 50;
        const int cap = 10;
        var liveActorKeys = new List<string> { "me" };
        liveActorKeys.AddRange(Enumerable.Range(0, total).Select(i => $"e{i}"));

        var callCount = 0;
        var built = TargetStage.BuildCapped("me", mySide: 0, liveActorKeys,
            sideOf: k => k == "me" ? 0 : 1, isReadable: _ => true, cap,
            (string k, out TargetCandidate c) => { callCount++; c = Synthetic(k, callCount); return true; });

        Assert.True(callCount <= cap, $"expected at most {cap} builds, got {callCount}");
        Assert.Equal(cap, callCount);
        Assert.Equal(cap, built.Count);
    }
}
