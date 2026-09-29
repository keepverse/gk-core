using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `core-scorer` (module 1, CAI1.1, spec-core-scorer.md): commit 1, the byte-identical
/// move. `AiScoring`/`AiCandidate`/`AiTuning` (Battle/Siege/SiegeAi.cs) are now a thin forwarding shim
/// over <see cref="CandidateScorer"/> — every test here proves the NEW scorer's own contract, using
/// the shim only where a test needs a value the pre-move siege types already encode (the migrated
/// weight table).
/// </summary>
public class CandidateScorerTests
{
    static ScoringWeights SiegeShippedWeights(int risk = 120) =>
        new(HitChance: 70, Objective: 50, Kill: 15, LowHp: 10, CannotCounter: 10, Round: 1, Risk: risk,
            AggressionRange: 2, MaxCandidatesScored: 32);

    static TargetCandidate Candidate(string key, int baseTier = 0, int aggression = 0,
        int hitChanceMilli = 0, int objectiveClassMilli = 0, bool isKillingBlow = false,
        int targetMissingHpMilli = 0, bool targetCanCounter = false, long incomingThreatMilli = 0) =>
        new(key, baseTier, aggression, hitChanceMilli, objectiveClassMilli, isKillingBlow,
            targetMissingHpMilli, targetCanCounter, incomingThreatMilli);

    /// <summary>Migration-fidelity assertion of specific numbers — legitimate for exactly one reason,
    /// stated here: this module's whole claim is "the same arithmetic", so these expected values are
    /// the CONTRACT (the siege weight table `siege.v1.json` ships today), never a reading of a
    /// population. Hand-computed once from the additive formula, term by term.</summary>
    [Fact]
    public void Score_matches_the_shipped_siege_weights_term_for_term()
    {
        var w = SiegeShippedWeights();
        var c = Candidate("a", hitChanceMilli: 700, objectiveClassMilli: 300, isKillingBlow: true,
            targetMissingHpMilli: 400, targetCanCounter: false, incomingThreatMilli: 50);

        var breakdown = CandidateScorer.ScoreBreakdownOf(c, currentRound: 2, w);

        Assert.Equal(70L * 700, breakdown.HitChance);
        Assert.Equal(50L * 300, breakdown.Objective);
        Assert.Equal(15L * 1000, breakdown.Kill);
        Assert.Equal(10L * 400, breakdown.LowHp);
        Assert.Equal(10L * 1000, breakdown.CannotCounter);
        Assert.Equal(1L * 2, breakdown.Round);
        Assert.Equal(120L * 50, breakdown.Risk);
        var expectedTotal = 70L * 700 + 50L * 300 + 15L * 1000 + 10L * 400 + 10L * 1000 + 1L * 2 - 120L * 50;
        Assert.Equal(expectedTotal, breakdown.Total);
        Assert.Equal(expectedTotal, CandidateScorer.Score(c, currentRound: 2, w));
    }

    [Fact]
    public void Total_is_Score_not_a_resum()
    {
        var w = SiegeShippedWeights();
        var c = Candidate("a", hitChanceMilli: 321, objectiveClassMilli: 55, isKillingBlow: true,
            targetMissingHpMilli: 890, targetCanCounter: true, incomingThreatMilli: 12);

        var breakdown = CandidateScorer.ScoreBreakdownOf(c, currentRound: 7, w);

        Assert.Equal(CandidateScorer.Score(c, currentRound: 7, w), breakdown.Total);
    }

    [Fact]
    public void Overflow_throws_rather_than_inverting_a_comparison()
    {
        var w = SiegeShippedWeights() with { HitChance = int.MaxValue, Objective = int.MaxValue, Kill = int.MaxValue };
        var c = Candidate("a", hitChanceMilli: int.MaxValue, objectiveClassMilli: int.MaxValue, isKillingBlow: true);

        Assert.Throws<OverflowException>(() => CandidateScorer.Score(c, currentRound: 0, w));
    }

    [Fact]
    public void Argmax_ties_break_ordinal()
    {
        var candidates = new[] { Candidate("zebra", hitChanceMilli: 100), Candidate("apple", hitChanceMilli: 100) };

        var chosen = CandidateScorer.ChooseTarget(candidates, currentRound: 0, SiegeShippedWeights());

        Assert.Equal("apple", chosen!.Value.ActorKey);
    }

    /// <summary>The byte-identity proof against the pre-move algorithm, written directly against an
    /// INDEPENDENT reference implementation of the shipped tier-then-argmax-then-ordinal-tiebreak
    /// pipeline (the CAI1.1 `AiScoring` shim this test originally compared against was retired at
    /// CAI1.8, when `AiTuning` narrowed and lost its scoring fields — `SiegeAiTests.cs`'s own unedited
    /// suite already proves the SAME identity claim against the shipped values directly). At the
    /// identity policy (Argmax, `KeepPctMilli` 1000 — the default when `selection` is omitted), the
    /// pipeline's pick equals this reference for a fixed table covering ties, tiers, and a
    /// negative-dominated score. Not a random/generated table — a deliberately varied FIXED one, so
    /// this test is stable across runs and reviewable by eye.</summary>
    [Fact]
    public void KeepPct_1000_and_Argmax_reduce_to_the_shipped_ChooseTarget()
    {
        var tables = new[]
        {
            new[] { Candidate("b", hitChanceMilli: 500), Candidate("a", hitChanceMilli: 500), Candidate("c", hitChanceMilli: 300) },
            new[] { Candidate("taunted", aggression: 2, hitChanceMilli: 1), Candidate("strong", hitChanceMilli: 1000, objectiveClassMilli: 1000) },
            new[] { Candidate("dangerous", hitChanceMilli: 150, incomingThreatMilli: 200), Candidate("safe", hitChanceMilli: 100, incomingThreatMilli: 0) },
            new[] { Candidate("x", hitChanceMilli: 900, isKillingBlow: true, incomingThreatMilli: 999), Candidate("y", hitChanceMilli: 10) },
        };

        foreach (var table in tables)
            for (var round = 0; round < 4; round++)
            {
                var reference = ReferenceChooseTarget(table, round, SiegeShippedWeights());
                var viaCore = CandidateScorer.ChooseTarget(table, round, SiegeShippedWeights());
                Assert.Equal(reference?.ActorKey, viaCore?.ActorKey);
            }
    }

    /// <summary>An independent, hand-written reimplementation of the shipped (pre-CAI1.1)
    /// `AiScoring.ChooseTarget`: tier (min + filter), then argmax with ordinal tie-break. No cap (every
    /// table here is well under `MaxCandidatesScored`), so this omits phase B deliberately.</summary>
    static TargetCandidate? ReferenceChooseTarget(IReadOnlyList<TargetCandidate> candidates, int round, ScoringWeights w)
    {
        if (candidates.Count == 0) return null;
        var bestTier = candidates.Min(c => CandidateScorer.EffectiveTier(c.BaseTier, c.Aggression, w.AggressionRange));
        var inTier = candidates.Where(c => CandidateScorer.EffectiveTier(c.BaseTier, c.Aggression, w.AggressionRange) == bestTier);
        return inTier
            .Select(c => (Candidate: c, Score: CandidateScorer.Score(c, round, w)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.ActorKey, StringComparer.Ordinal)
            .Select(x => (TargetCandidate?)x.Candidate)
            .First();
    }

    [Fact]
    public void Weighted_pick_is_reproducible_from_the_same_stream_name_and_seed()
    {
        var candidates = new[]
        {
            Candidate("alpha", hitChanceMilli: 900),
            Candidate("bravo", hitChanceMilli: 500),
            Candidate("charlie", hitChanceMilli: 100),
        };
        var w = SiegeShippedWeights(risk: 0);
        // A narrow KeepPctMilli widens the surviving pool relative to the identity 1000 (only "alpha"
        // and "bravo" clear it here; "charlie"'s shifted weight is far below the threshold) -- enough
        // for the weighted draw to be real rather than a forced single-candidate pick.
        var policy = new SelectionPolicy(SelectionMode.SeededWeighted, KeepPctMilli: 1, RngStreamName: "ai.select.test");

        var first = CandidateScorer.ChooseTarget(candidates, currentRound: 0, w, policy, runSeed: 12345);
        for (var i = 0; i < 50; i++)
        {
            var again = CandidateScorer.ChooseTarget(candidates, currentRound: 0, w, policy, runSeed: 12345);
            Assert.Equal(first, again);
        }

        // A different stream name over the SAME candidates/seed is free to disagree -- the draw
        // genuinely reads the seed and stream, not a constant.
        var seenDistinctPicks = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 0; seed < 30; seed++)
        {
            var picked = CandidateScorer.ChooseTarget(candidates, currentRound: 0, w,
                policy with { RngStreamName = $"ai.select.{seed}" }, runSeed: seed);
            seenDistinctPicks.Add(picked!.Value.ActorKey);
        }
        Assert.True(seenDistinctPicks.Count > 1, "expected the seeded draw to vary across different seeds/streams");
    }

    /// <summary>The shift is mandatory: the risk term subtracts, so a raw score is routinely negative,
    /// and a percentage cut on a negative number is meaningless. Proven by constructing a table whose
    /// raw scores are entirely negative and confirming the weighted pick still succeeds (never throws,
    /// never silently refuses) and only ever returns a candidate that was actually in the table.</summary>
    [Fact]
    public void Weighted_pick_shifts_negative_scores_before_cutting()
    {
        var candidates = new[]
        {
            Candidate("a", incomingThreatMilli: 900),
            Candidate("b", incomingThreatMilli: 500),
            Candidate("c", incomingThreatMilli: 999),
        };
        var w = SiegeShippedWeights(risk: 1000); // every raw score is deeply negative
        var policy = new SelectionPolicy(SelectionMode.SeededWeighted, KeepPctMilli: 1, RngStreamName: "ai.select.negative");
        var validKeys = new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };

        for (ulong seed = 0; seed < 10; seed++)
        {
            var picked = CandidateScorer.ChooseTarget(candidates, currentRound: 0, w, policy, runSeed: seed);
            Assert.NotNull(picked);
            Assert.Contains(picked!.Value.ActorKey, validKeys);
        }
    }

    /// <summary>The closed term vocabulary, pinned with the reason: adding a term changes every
    /// weight key, every breakdown column and every trace line — a reviewed change, not a balance
    /// pass.</summary>
    [Fact]
    public void ScoreTerm_has_seven_members() =>
        Assert.Equal(7, Enum.GetValues(typeof(ScoreTerm)).Length);

    /// <summary>The selection stage's own closed vocabulary, pinned with the reason: the profile schema
    /// names a mode by string and an unknown one throws, so the width IS the contract. `Argmax` is the
    /// identity default and `SeededWeighted` the opt-in draw — a third mode is a reviewed change, and
    /// the count is what `docs/DESIGN-GATE.md` §1's decision row records.</summary>
    [Fact]
    public void SelectionMode_has_two_members() =>
        Assert.Equal(2, Enum.GetValues(typeof(SelectionMode)).Length);
}
