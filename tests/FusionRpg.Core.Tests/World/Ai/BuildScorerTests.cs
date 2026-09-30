using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Ai.Utility;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// `ai-build-scorer` EP5.1 (spec-ai-build-scorer.md) — the scorer over <see cref="Considerations.Score"/>.
/// Four properties, each one of the spec's own testing-strategy rows: it writes no arithmetic of its own
/// (1), a continuity zero vetoes a rung (2), neutral needs change nothing (3), and the same belief and
/// tuning give the same pick with an ordinal tie-break (4).
/// </summary>
public class BuildScorerTests
{
    /// <summary>The shipped ladder order, read from the real document — the neutral case has to agree
    /// with what the ladder itself would pick, so it must not hand-copy the order.</summary>
    static readonly AptitudePresetTuning Presets = AptitudePresetTuningLoader.Parse(
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "aptitude-presets.v2.json")));

    /// <summary>
    /// The AI's own ladder context — no active preset (an AI holds none), no species favour, no posture
    /// — the SAME context `RpgStore.CommanderPoolOfUnlocked` walks with, so "the ladder's rung" below is
    /// the rung a real AI empire's walk lands on, never a value this test picked.
    /// </summary>
    static readonly AssignContext AiContext = new(
        ActivePresetRows: null, SpeciesFavourPermille: null, SpeciesPosture: null, FavourAllowed: false);

    /// <summary>Explicit tunables: EP5.2 publishes these from `ai.v{n}.json`; nothing in the code
    /// supplies a default (tunables-ssot.md T5), so every test builds its own.</summary>
    static readonly BuildScorerTuning Tuning = new(
        NeedFitCurve: ResponseCurve.Linear, NeedFitThreshold: 500,
        CounterFitCurve: ResponseCurve.Smoothstep, CounterFitThreshold: 500,
        ContinuityCooldownTurns: 3);

    [Fact]
    public void The_scorer_computes_no_product_itself()
    {
        // Reflection cannot look inside a method body, so this is the repo's other established idiom:
        // a source scan. It asserts the SHAPE that makes the reuse true -- the one scoring call is
        // Considerations.Score, the tie-break is the one the spec pins, and there is no product or
        // compensation expression in this file at all (Consideration.cs owns both).
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Core", "World", "Ai", "BuildScorer.cs"));

        Assert.Contains("Considerations.Score(considerations)", source, StringComparison.Ordinal);
        Assert.Contains("ThenBy(c => c.RuleId, StringComparer.Ordinal)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Compensate(", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\*"), source);

        // ... and the scan is not vacuous: the file it read is the scorer.
        Assert.Contains("public static class BuildScorer", source, StringComparison.Ordinal);
        Assert.Contains("public static BuildChoice Choose(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_on_continuity_vetoes_the_rung_whatever_its_other_scores()
    {
        // The vetoed rung is the STRONGER one on the other two axes, so the only thing that can make it
        // lose is the continuity zero.
        var strongButTooSoon = new BuildRung("posture-force", NeedFit: 1000, CounterFit: 1000);
        var weaker = new BuildRung("even", NeedFit: 300, CounterFit: 300);
        var rungs = new[] { strongButTooSoon, weaker };

        // Inside the cooldown (0 turns elapsed, cooldown 3): continuity scores 0 and vetoes it.
        var inside = BuildScorer.Choose(rungs, turnsSinceRepattern: 0, Tuning);
        Assert.Equal("even", inside.RuleId);
        Assert.Equal(0, inside.Considerations.Single(c => c.Name == BuildScorer.ContinuityName).Score());

        // The same call once the cooldown has elapsed picks the strong rung, so the veto is what decided
        // it rather than the score ordering.
        var elapsed = BuildScorer.Choose(rungs, turnsSinceRepattern: Tuning.ContinuityCooldownTurns, Tuning);
        Assert.Equal("posture-force", elapsed.RuleId);
        Assert.Equal(ResponseCurves.Max,
            elapsed.Considerations.Single(c => c.Name == BuildScorer.ContinuityName).Score());
        Assert.True(elapsed.Score > inside.Score,
            "the vetoed pick must score lower than the allowed one, or the veto is not the cause");
    }

    [Fact]
    public void With_uniform_needs_it_picks_the_ladders_rung()
    {
        // The rung the ladder's own walk lands on for an AI's context, asked of the ladder rather than
        // assumed: active-preset has no rows, species-favour is refused (Mode C), the three postures
        // have no posture, so the walk reaches its structural terminal rung.
        var ladderRung = AssignLadder.Suggest(AiContext, Presets.AssignLadder).RuleId;
        Assert.Equal(AptitudeAutoAssignRules.Even, ladderRung);

        // Neutral needs: the only INeedVector that exists answers its own 1000 on every axis
        // (UniformNeeds), and no enemy posture is observed, so the counter fit is the same neutral.
        var neutral = UniformNeeds.Instance;
        var needFit = neutral.ForSlotKind(SlotKind.EssenceDeposit);
        Assert.Equal(UniformNeeds.Neutral, needFit);

        // Every distribution rung the ladder could produce for a species (an AI holds no preset, so
        // `active-preset` is not a candidate). All five score identically under neutral inputs, and the
        // ordinal tie-break lands on `even` — the ladder's own rung. This is the property that documents
        // why the module waited for a real economy: a scorer over constant content changes no answer.
        var rungs = new[]
        {
            AptitudeAutoAssignRules.Even,
            AptitudeAutoAssignRules.SpeciesFavour,
            AptitudeAutoAssignRules.PostureForce,
            AptitudeAutoAssignRules.PostureFinesse,
            AptitudeAutoAssignRules.PostureBastion,
        }.Select(rule => new BuildRung(rule, needFit, UniformNeeds.Neutral)).ToArray();

        var choice = BuildScorer.Choose(rungs, turnsSinceRepattern: Tuning.ContinuityCooldownTurns, Tuning);

        Assert.Equal(ladderRung, choice.RuleId);
        // A tie, decided by the ordinal rule id: stated, so a future non-neutral input does not silently
        // keep this passing for the wrong reason.
        Assert.Equal(
            rungs.Select(r => r.RuleId).OrderBy(r => r, StringComparer.Ordinal).First(),
            choice.RuleId);
    }

    [Fact]
    public void The_same_belief_and_tuning_give_the_same_pick_with_an_ordinal_tie_break()
    {
        var rungs = new[]
        {
            new BuildRung("species-favour", NeedFit: 800, CounterFit: 700),
            new BuildRung("posture-bastion", NeedFit: 500, CounterFit: 900),
            new BuildRung("even", NeedFit: 600, CounterFit: 600),
        };

        var first = BuildScorer.Choose(rungs, turnsSinceRepattern: 9, Tuning);
        var again = BuildScorer.Choose(rungs, turnsSinceRepattern: 9, Tuning);
        Assert.Equal(first.RuleId, again.RuleId);
        Assert.Equal(first.Score, again.Score);
        Assert.Equal(first.Weakest?.Name, again.Weakest?.Name);

        // Candidate ORDER must not matter either: the ordering is the score, then the rule id.
        var reversed = BuildScorer.Choose(rungs.Reverse().ToArray(), turnsSinceRepattern: 9, Tuning);
        Assert.Equal(first.RuleId, reversed.RuleId);

        // An exact tie breaks on the ordinal rule id: "aaa" before "zzz", whichever order they arrive in.
        var tie = new[]
        {
            new BuildRung("zzz", NeedFit: 400, CounterFit: 400),
            new BuildRung("aaa", NeedFit: 400, CounterFit: 400),
        };
        Assert.Equal("aaa", BuildScorer.Choose(tie, turnsSinceRepattern: 9, Tuning).RuleId);
        Assert.Equal("aaa", BuildScorer.Choose(tie.Reverse().ToArray(), turnsSinceRepattern: 9, Tuning).RuleId);

        // The turn report's own line: the weakest axis of the winning rung is named, and it is one of
        // the three considerations this scorer wrote.
        Assert.Contains(first.Weakest?.Name, new[]
        {
            BuildScorer.NeedFitName, BuildScorer.CounterFitName, BuildScorer.ContinuityName,
        });
    }

    [Fact]
    public void Wiring_the_scorer_into_the_ai_species_default_needs_real_belief_content()
    {
        // `ai-build-scorer` EP5.3's own clause is "the AI empire's species follow the scored rungs
        // through the same allocation path". This case is the proof that such a wiring is NOT a
        // behaviour-preserving no-op on today's beliefs, and therefore cannot land as written without a
        // ruling and the economy:
        //
        //  * the ladder's OWN rung for a species context (a plan row present, favour allowed) is
        //    `species-favour` -- what the AI species baseline materialises today;
        //  * the same candidate set scored with neutral inputs ties, and the committed ordinal
        //    tie-break returns `even` -- a DIFFERENT distribution, i.e. a real behaviour change for
        //    every AI species, with no balance justification behind it.
        var favour = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["Might"] = 500, ["Vigor"] = 300, ["Fortitude"] = 200,
        };
        var speciesContext = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: favour, SpeciesPosture: null, FavourAllowed: true);
        var ladderRung = AssignLadder.Suggest(speciesContext, Presets.AssignLadder).RuleId;
        Assert.Equal(AptitudeAutoAssignRules.SpeciesFavour, ladderRung);

        var needFit = UniformNeeds.Instance.ForSlotKind(SlotKind.EssenceDeposit);
        var candidates = new[]
        {
            AptitudeAutoAssignRules.Even,
            AptitudeAutoAssignRules.SpeciesFavour,
            AptitudeAutoAssignRules.PostureForce,
            AptitudeAutoAssignRules.PostureFinesse,
            AptitudeAutoAssignRules.PostureBastion,
        }.Select(rule => new BuildRung(rule, needFit, UniformNeeds.Neutral)).ToArray();

        var pickedUnderNeutralNeeds = BuildScorer.Choose(
            candidates, turnsSinceRepattern: Tuning.ContinuityCooldownTurns, Tuning).RuleId;
        Assert.NotEqual(ladderRung, pickedUnderNeutralNeeds);

        // ... and the cause is the belief being constant, not the scorer being wrong: give the ladder's
        // own rung a real need edge and it wins on SCORE, with no tie-break involved. That is the
        // content EP5.3 needs and `sector-development` owns.
        var withBelief = candidates
            .Select(r => r with
            {
                NeedFit = r.RuleId == AptitudeAutoAssignRules.SpeciesFavour ? ResponseCurves.Max : ResponseCurves.Max / 2,
            })
            .ToArray();
        Assert.Equal(ladderRung, BuildScorer.Choose(
            withBelief, turnsSinceRepattern: Tuning.ContinuityCooldownTurns, Tuning).RuleId);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
