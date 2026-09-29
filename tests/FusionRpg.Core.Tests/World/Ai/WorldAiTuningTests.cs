using System;
using System.IO;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Ai.Utility;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// `ai-build-scorer` EP5.2 — the published `buildScorer` block: global defaults with per-empire
/// overrides, a missing key is a load rejection, and the block is what EP5.1's scorer reads.
/// </summary>
public class WorldAiTuningTests
{
    static readonly WorldAiTuning Shipped = WorldAiTuningLoader.Parse(
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "ai.v3.json")));

    [Fact]
    public void The_shipped_document_parses_and_carries_the_build_scorer_block()
    {
        // The version the publish tool assigned, and the block it added.
        Assert.Equal(3, Shipped.Version);
        Assert.Equal(1, Shipped.SchemaVersion);

        // Curve ids are a closed vocabulary, so naming them here is contract, not a balance pin.
        Assert.Equal(ResponseCurve.Linear, Shipped.BuildScorer.Defaults.NeedFitCurve);
        Assert.Equal(ResponseCurve.Smoothstep, Shipped.BuildScorer.Defaults.CounterFitCurve);

        // The magnitudes are asserted as an ENVELOPE (the range the arithmetic accepts), never as the
        // shipped numbers: a balance pass must be able to move them without turning this red.
        Assert.InRange(Shipped.BuildScorer.Defaults.NeedFitThreshold, 0, ResponseCurves.Max);
        Assert.InRange(Shipped.BuildScorer.Defaults.CounterFitThreshold, 0, ResponseCurves.Max);
        Assert.True(Shipped.BuildScorer.Defaults.ContinuityCooldownTurns > 0,
            "a zero cooldown would make the continuity rung a constant, not a rate limit");

        // An empire with no override gets the global defaults — the shipped block ships none yet.
        Assert.Empty(Shipped.BuildScorer.ByEmpire);
        Assert.Same(Shipped.BuildScorer.Defaults, Shipped.BuildScorer.For(EmpireId.Zomboss));
        Assert.Same(Shipped.BuildScorer.Defaults, Shipped.BuildScorer.For(EmpireId.Dave));
    }

    [Fact]
    public void The_published_block_is_what_the_scorer_reads()
    {
        // The seam between the two rows: EP5.2's payload drives EP5.1's scorer with no adaptation.
        var tuning = Shipped.BuildScorer.Defaults;
        var rungs = new[]
        {
            new BuildRung("even", NeedFit: 500, CounterFit: 500),
            new BuildRung("posture-force", NeedFit: 900, CounterFit: 900),
        };

        var choice = BuildScorer.Choose(rungs, turnsSinceRepattern: tuning.ContinuityCooldownTurns, tuning);

        Assert.Contains(choice.RuleId, new[] { "even", "posture-force" });
        Assert.True(choice.Score > 0, "a published block must produce a real score, not a veto-everything 0");
    }

    [Fact]
    public void A_missing_build_scorer_block_is_a_load_rejection()
    {
        var rejection = Assert.Throws<WorldAiTuningRejection>(
            () => WorldAiTuningLoader.Parse(Doc(buildScorer: null)));
        Assert.Contains("buildScorer", rejection.Message, StringComparison.Ordinal);

        // ... and a key missing INSIDE the block's defaults is named too.
        var missingKey = Doc(", \"buildScorer\": { \"defaults\": { \"needFitCurve\": \"Linear\", "
            + "\"needFitThreshold\": 500, \"counterFitCurve\": \"Smoothstep\", \"counterFitThreshold\": 500 }, "
            + "\"byEmpire\": {} }");
        var inner = Assert.Throws<WorldAiTuningRejection>(() => WorldAiTuningLoader.Parse(missingKey));
        Assert.Contains("continuityCooldownTurns", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_per_empire_override_wins_and_inherits_the_keys_it_does_not_name()
    {
        var json = Doc(", \"buildScorer\": { "
            + "\"defaults\": { \"needFitCurve\": \"Linear\", \"needFitThreshold\": 500, "
            + "\"counterFitCurve\": \"Smoothstep\", \"counterFitThreshold\": 500, \"continuityCooldownTurns\": 4 }, "
            + "\"byEmpire\": { \"zomboss\": { \"counterFitCurve\": \"Threshold\", \"counterFitThreshold\": 750 } } }");
        var tuning = WorldAiTuningLoader.Parse(json);

        var zomboss = tuning.BuildScorer.For(EmpireId.Zomboss);
        Assert.Equal(ResponseCurve.Threshold, zomboss.CounterFitCurve);   // the override
        Assert.Equal(750, zomboss.CounterFitThreshold);                   // the override
        Assert.Equal(ResponseCurve.Linear, zomboss.NeedFitCurve);         // inherited
        Assert.Equal(500, zomboss.NeedFitThreshold);                      // inherited
        Assert.Equal(4, zomboss.ContinuityCooldownTurns);                 // inherited

        // An empire the block does not name is untouched by another empire's override.
        Assert.Equal(ResponseCurve.Smoothstep, tuning.BuildScorer.For(EmpireId.Dave).CounterFitCurve);
    }

    [Fact]
    public void An_unknown_curve_name_is_a_load_rejection()
    {
        var json = Doc(", \"buildScorer\": { "
            + "\"defaults\": { \"needFitCurve\": \"Exponential\", \"needFitThreshold\": 500, "
            + "\"counterFitCurve\": \"Smoothstep\", \"counterFitThreshold\": 500, \"continuityCooldownTurns\": 3 }, "
            + "\"byEmpire\": {} }");

        var rejection = Assert.Throws<WorldAiTuningRejection>(() => WorldAiTuningLoader.Parse(json));
        Assert.Contains("needFitCurve", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("Exponential", rejection.Message, StringComparison.Ordinal);
        // The vocabulary is named, so a misspelling is diagnosable without reading the enum.
        Assert.Contains("Smoothstep", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>The shipped document's own shape with the buildScorer block spliced in (or omitted) —
    /// every other block is a real working set so a rejection can only be the block under test.</summary>
    static string Doc(string? buildScorer) => $$"""
        {
          "schemaVersion": 1,
          "version": 3,
          "frontierRules": { "recoverAtMilli": 400, "exploreTurns": 3, "severanceThresholdCost": 10000, "momentumMarginMilli": 250 },
          "threatMap": { "staleDecayPerTurn": 150, "maxSpreadHops": 4, "proximityFalloffPerHop": 400 },
          "valueMap": {
            "optimismMilli": 700, "overextensionPenaltyMilli": 1400, "habitabilityPenaltyMilli": 1400,
            "defaultWeights": { "yield": 1000, "strategic": 800, "defensibility": 500, "cost": 700, "risk": 900, "curiosity": 600 }
          }{{buildScorer ?? ""}}
        }
        """;

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core", "FusionRpg.Core.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
