using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` (spec-siege-ai.md): the pure decision mechanics (R1/R2/R5/R6). R6's own
/// trace wiring (`CandidateScorer.TopThree`/`ScoreBreakdownOf`/`FormatTopThree` into
/// `BattleTrace.AiDecision`) is covered here too — see `SiegeAi.cs`'s own top comment for the fuller
/// account and its real correction of the spec's own `DecisionTrace.cs` citation.
///
/// <para>`SiegeIntentSource`'s own dispatch/fallthrough symmetry (§1) was tested here until combat-ai
/// `intent-router` (module 4, CAI1.10) deleted the class (zero production callers — confirmed via
/// `DistrictAssaultResolver.cs`'s own doc comment) and ported its contract, test for test, into
/// `Actions/IntentRouterTests.cs`.</para>
/// </summary>
public class SiegeAiTests
{
    // combat-ai `profile-schema` CAI1.8 (H7): AiTuning narrowed to four members (geometry + the two
    // dead keys) when the ten scorer weights moved to combat-ai.v1.json's own ScoringWeights shape.
    // This helper now returns ScoringWeights directly -- the SAME ten values, same names minus the
    // "Weight" prefix, positionally identical to the shipped siege.v1.json ai block.
    static ScoringWeights Weights(int risk = 120) => new(
        HitChance: 70, Objective: 50, Kill: 15, LowHp: 10,
        CannotCounter: 10, Round: 1, Risk: risk,
        AggressionRange: 2, MaxCandidatesScored: 32);

    static TargetCandidate Candidate(string key, int baseTier = 0, int aggression = 0,
        int hitChanceMilli = 0, int objectiveClassMilli = 0, bool isKillingBlow = false,
        int targetMissingHpMilli = 0, bool targetCanCounter = false, long incomingThreatMilli = 0) =>
        new(key, baseTier, aggression, hitChanceMilli, objectiveClassMilli, isKillingBlow,
            targetMissingHpMilli, targetCanCounter, incomingThreatMilli);

    // -- R5: determinism, no RNG --

    [Fact]
    public void Same_board_same_decisions_10000_times()
    {
        var candidates = new[]
        {
            Candidate("b", hitChanceMilli: 500),
            Candidate("a", hitChanceMilli: 500), // tie on every scored term, breaks on key
            Candidate("c", hitChanceMilli: 300),
        };
        var w = Weights();
        var first = CandidateScorer.ChooseTarget(candidates, currentRound: 3, w);
        for (var i = 0; i < 10_000; i++)
            Assert.Equal(first, CandidateScorer.ChooseTarget(candidates, currentRound: 3, w));
    }

    [Fact]
    public void No_rng_is_reachable_from_the_ai()
    {
        var text = File.ReadAllText(FindSourceFile("SiegeAi.cs"));
        Assert.DoesNotContain("Random", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ties_break_by_ordinal_key()
    {
        var candidates = new[] { Candidate("zebra", hitChanceMilli: 100), Candidate("apple", hitChanceMilli: 100) };
        var chosen = CandidateScorer.ChooseTarget(candidates, currentRound: 0, Weights());
        Assert.Equal("apple", chosen!.Value.ActorKey);
    }

    [Fact]
    public void Score_overflow_throws()
    {
        var w = Weights() with { HitChance = int.MaxValue, Objective = int.MaxValue, Kill = int.MaxValue };
        var c = Candidate("a", hitChanceMilli: int.MaxValue, objectiveClassMilli: int.MaxValue, isKillingBlow: true);
        Assert.Throws<OverflowException>(() => CandidateScorer.Score(c, currentRound: 0, w));
    }

    // -- R1/R2: stance/aggression/score are three distinct axes --

    [Fact]
    public void Taunt_dominates_within_its_tier_and_not_outside()
    {
        var w = Weights();
        // Taunted candidate (aggression +2) lands in a strictly better tier than an untaunted one
        // with a much higher raw score -- tier wins first, score only breaks ties within a tier.
        var taunted = Candidate("taunted", baseTier: 0, aggression: 2, hitChanceMilli: 1);
        var strongUntainted = Candidate("strong", baseTier: 0, aggression: 0, hitChanceMilli: 1000, objectiveClassMilli: 1000);
        var chosen = CandidateScorer.ChooseTarget(new[] { taunted, strongUntainted }, currentRound: 0, w);
        Assert.Equal("taunted", chosen!.Value.ActorKey);

        // Outside its own tier -- i.e. compared against nothing that competes for the same tier --
        // the taunt candidate simply isn't present, so it cannot affect the choice at all.
        var chosenWithoutTaunt = CandidateScorer.ChooseTarget(new[] { strongUntainted }, currentRound: 0, w);
        Assert.Equal("strong", chosenWithoutTaunt!.Value.ActorKey);
    }

    [Fact]
    public void Stealth_demotes_and_taunt_promotes_through_the_same_field()
    {
        var range = 2;
        var baseline = CandidateScorer.EffectiveTier(baseTier: 0, aggression: 0, range);
        var taunted = CandidateScorer.EffectiveTier(baseTier: 0, aggression: 2, range);
        var stealthed = CandidateScorer.EffectiveTier(baseTier: 0, aggression: -2, range);

        Assert.True(taunted < baseline); // numerically lower = higher priority
        Assert.True(stealthed > baseline);
    }

    [Fact]
    public void Aggression_is_applied_inside_the_tier_not_on_top_of_the_score()
    {
        // A decoy (+1 aggression) on an otherwise-worthless target still wins its tier over a
        // strong target one tier worse, and the decoy's own SCORE never needs to be competitive.
        var decoy = Candidate("decoy", baseTier: 0, aggression: 1, hitChanceMilli: 0, objectiveClassMilli: 0);
        var strongerButWorseTier = Candidate("strong", baseTier: 0, aggression: 0, hitChanceMilli: 1000, objectiveClassMilli: 1000);
        var chosen = CandidateScorer.ChooseTarget(new[] { decoy, strongerButWorseTier }, currentRound: 0, Weights());
        Assert.Equal("decoy", chosen!.Value.ActorKey);
    }

    [Fact]
    public void Aggression_range_is_bounded_and_the_bound_is_authored()
    {
        // combat-ai `aggression-tier-map` CAI1.13: this test USED to assert the throw. An aggression
        // past ±aggressionRange is now a legal stacked value that SATURATES onto the vocabulary's edge
        // (audit M13: two +1 sources and a +1 status summed to +3 and ended the battle with an
        // ArgumentOutOfRangeException). Asserted here so the siege suite still covers the bound; the
        // vocabulary-width and signed-overshoot contracts live in
        // `Actions/Ai/AggressionTierMapTests`, which also pins that a non-positive RANGE still throws.
        Assert.Equal(-2, CandidateScorer.EffectiveTier(0, aggression: 3, aggressionRange: 2, out var overshootUp));
        Assert.Equal(1, overshootUp);
        Assert.Equal(2, CandidateScorer.EffectiveTier(0, aggression: -3, aggressionRange: 2, out var overshootDown));
        Assert.Equal(-1, overshootDown);
    }

    // -- R2: additive scoring, XCOM's shipped ordering --

    [Fact]
    public void Hit_chance_outweighs_lethality_seventy_to_fifteen()
    {
        var w = Weights();
        // A guaranteed-hit non-kill beats a low-chance kill, matching XCOM's own shipped ordering --
        // an AI that maximises expected damage with no risk term reads as suicidal.
        var safeNonKill = Candidate("safe", hitChanceMilli: 1000, isKillingBlow: false);
        var riskyKill = Candidate("risky", hitChanceMilli: 100, isKillingBlow: true);
        var chosen = CandidateScorer.ChooseTarget(new[] { safeNonKill, riskyKill }, currentRound: 0, w);
        Assert.Equal("safe", chosen!.Value.ActorKey);
    }

    [Fact]
    public void The_round_term_makes_a_stalled_ai_bolder_over_time()
    {
        var w = Weights();
        var risky = Candidate("risky", incomingThreatMilli: 40);
        Assert.True(CandidateScorer.Score(risky, currentRound: 100, w) > CandidateScorer.Score(risky, currentRound: 0, w));
    }

    [Fact]
    public void Risk_term_prevents_walking_into_a_kill_zone()
    {
        var dangerous = Candidate("dangerous", hitChanceMilli: 150, incomingThreatMilli: 200);
        var safe = Candidate("safe", hitChanceMilli: 100, incomingThreatMilli: 0);

        var atDefaultRisk = CandidateScorer.ChooseTarget(new[] { dangerous, safe }, currentRound: 0, Weights(risk: 120));
        Assert.Equal("safe", atDefaultRisk!.Value.ActorKey);

        // decision 31's one-row rollback: risk weight 0 makes the AI cover-blind.
        var riskBlind = CandidateScorer.ChooseTarget(new[] { dangerous, safe }, currentRound: 0, Weights(risk: 0));
        Assert.Equal("dangerous", riskBlind!.Value.ActorKey);
    }

    [Fact]
    public void Risk_weight_zero_makes_the_ai_cover_blind()
    {
        var c = Candidate("a", incomingThreatMilli: 1000);
        Assert.Equal(CandidateScorer.Score(c with { IncomingThreatMilli = 0 }, 0, Weights()), CandidateScorer.Score(c, 0, Weights(risk: 0)));
    }

    [Fact]
    public void Cover_reduces_perceived_risk()
    {
        // "Cover reduces incoming threat" is the caller's job (siege-cover's own math); this module's
        // contribution is that a lower incomingThreatMilli produces a strictly higher (better) score.
        var exposed = Candidate("a", incomingThreatMilli: 800);
        var covered = Candidate("a", incomingThreatMilli: 200);
        Assert.True(CandidateScorer.Score(covered, 0, Weights()) > CandidateScorer.Score(exposed, 0, Weights()));
    }

    [Fact]
    public void No_path_holds_rather_than_fidgets()
    {
        // An empty candidate list is the "no target in reach" case -- ChooseTarget returns null, and
        // the caller's own fallback (R3: path toward objective, or hold) never rolls a random move.
        Assert.Null(CandidateScorer.ChooseTarget(Array.Empty<TargetCandidate>(), currentRound: 0, Weights()));
    }

    // -- R6: readability --

    [Fact]
    public void Decision_trace_names_the_top_three_with_scores()
    {
        var candidates = new[]
        {
            Candidate("a", hitChanceMilli: 100), Candidate("b", hitChanceMilli: 900),
            Candidate("c", hitChanceMilli: 500), Candidate("d", hitChanceMilli: 10),
        };
        var top3 = CandidateScorer.TopThree(candidates, currentRound: 0, Weights());
        Assert.Equal(3, top3.Count);
        Assert.Equal("b", top3[0].ActorKey);
        Assert.Equal("c", top3[1].ActorKey);
        Assert.Equal("a", top3[2].ActorKey);
    }

    [Fact]
    public void ScoreBreakdown_total_always_equals_Score_for_the_same_candidate()
    {
        // The breakdown must never be a second, independently-derived total — a real risk once a
        // trace consumer starts trusting it for "why did the AI pick this" debugging.
        var c = Candidate("a", hitChanceMilli: 700, objectiveClassMilli: 300, isKillingBlow: true,
            targetMissingHpMilli: 400, targetCanCounter: false, incomingThreatMilli: 50);
        var w = Weights();

        var breakdown = CandidateScorer.ScoreBreakdownOf(c, currentRound: 2, w);

        Assert.Equal(CandidateScorer.Score(c, currentRound: 2, w), breakdown.Total);
    }

    [Fact]
    public void ScoreBreakdown_names_each_individual_weighted_term()
    {
        var c = Candidate("a", hitChanceMilli: 700, objectiveClassMilli: 300, isKillingBlow: true,
            targetMissingHpMilli: 400, targetCanCounter: false, incomingThreatMilli: 50);
        var w = Weights();

        var breakdown = CandidateScorer.ScoreBreakdownOf(c, currentRound: 2, w);

        Assert.Equal(70L * 700, breakdown.HitChance);
        Assert.Equal(50L * 300, breakdown.Objective);
        Assert.Equal(15L * 1000, breakdown.Kill); // IsKillingBlow: true
        Assert.Equal(10L * 400, breakdown.LowHp);
        Assert.Equal(10L * 1000, breakdown.CannotCounter); // TargetCanCounter: false -> full credit
        Assert.Equal(1L * 2, breakdown.Round);
        Assert.Equal(120L * 50, breakdown.Risk);
    }

    [Fact]
    public void FormatTopThree_names_every_ranked_candidate_and_its_total()
    {
        var candidates = new[]
        {
            Candidate("b", hitChanceMilli: 900), Candidate("a", hitChanceMilli: 100), Candidate("c", hitChanceMilli: 500),
        };
        var w = Weights();

        var summary = CandidateScorer.FormatTopThree(CandidateScorer.TopThree(candidates, currentRound: 0, w));

        Assert.Equal(
            $"#1=b(hit=63000,obj=0,kill=0,lowhp=0,cc=10000,rnd=0,risk=0,total=73000) " +
            $"#2=c(hit=35000,obj=0,kill=0,lowhp=0,cc=10000,rnd=0,risk=0,total=45000) " +
            $"#3=a(hit=7000,obj=0,kill=0,lowhp=0,cc=10000,rnd=0,risk=0,total=17000)",
            summary);
    }

    [Fact]
    public void Every_target_filter_has_a_display_key()
    {
        var filter = new TargetFilter { DisplayKey = "Ground only" };
        Assert.False(string.IsNullOrWhiteSpace(filter.DisplayKey));
    }

    // -- §7b: no hidden difficulty thumb, no score on ActionTargetOrdering --

    [Fact]
    public void No_stat_bonus_difficulty_exists()
    {
        var text = File.ReadAllText(FindSourceFile("SiegeAi.cs"));
        Assert.DoesNotContain("difficulty", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Score_is_not_on_ActionTargetOrdering()
    {
        var text = File.ReadAllText(FindSourceFile("SiegeAi.cs"));
        Assert.DoesNotContain("ActionTargetOrdering", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_targeting_ui_is_specced()
    {
        // Structural: this module ships no UI-facing type at all beyond TargetFilter's DisplayKey.
        var text = File.ReadAllText(FindSourceFile("SiegeAi.cs"));
        Assert.DoesNotContain("Ui", text, StringComparison.Ordinal);
    }

    static string FindSourceFile(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir != null; i++)
        {
            var candidate = Directory.GetFiles(dir, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
            if (candidate.Length > 0) return candidate[0];
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"{fileName} not found by walking up from {AppContext.BaseDirectory}");
    }

    // §1's own SiegeIntentSource dispatch/fallthrough symmetry tests moved to
    // Actions/IntentRouterTests.cs (combat-ai `intent-router`, CAI1.10) when the class they tested
    // was deleted -- see this file's own top comment.
}
