using System.IO;
using System.Text.Json;
using FusionRpg.Core.PassiveTree.State;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.PassiveTree;

/// <summary>
/// Task A1 — `gk-core/data/tuning/passive-tree.v1.json` and its loader (spec-tree-plan.md §Tunables,
/// spec-tree-binder.md §3.6, spec-tree-state.md §8, spec-tree-resolve.md §8,
/// spec-gate-counters.md §7 P3/§13).
/// </summary>
public class PassiveTreeTuningTests
{
    static readonly string ValidJson = File.ReadAllText(LiveTuningPath());

    static string LiveTuningPath() =>
        Path.Combine(RepoRoot(), "data", "tuning", "passive-tree.v1.json");

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void The_live_tuning_file_loads_every_key()
    {
        var tuning = PassiveTreeTuningLoader.Parse(ValidJson);

        Assert.Equal(1, tuning.SchemaVersion);
        Assert.Equal(5, tuning.TierLadder.ReqScalePoints);
        Assert.Equal(500, tuning.Budget.BranchSplitMilli);
        Assert.Equal(182, tuning.Potency.MaxNodeShareMilli);
        Assert.Equal(1, tuning.Potency.MinTerminalWidth);
        Assert.Equal(new long[] { 46, 91, 137, 182 }, tuning.Potency.BandEdgesMilli);
        Assert.Equal(0, tuning.Mechanism.RampStartMilli);
        Assert.Equal(1000, tuning.Mechanism.RampEndMilli);
        Assert.Equal(6000, tuning.Archetype.RewardSpreadMaxRatioMilli);
        Assert.Equal(20, tuning.Exclusion.TargetShareMilli);
        Assert.Equal("ordinal-round-robin", tuning.ArchetypeAssignment);
        Assert.Equal(92, tuning.DesignTarget.ThetaAllIn);
        Assert.Equal(1200, tuning.Concentration.FmaxMilli);
        Assert.Equal(500, tuning.Concentration.WMilli);
        Assert.Equal(5, tuning.UnlockCost.FirstPoints);
        Assert.Equal(2, tuning.UnlockCost.StepPoints);
        Assert.Equal(50, tuning.Respec.BasePrice);
        Assert.Equal(500, tuning.Respec.EscalationPermille);
        Assert.Equal(23, tuning.GateCounters.MasteryCurveFirstCount);
        Assert.Equal(23, tuning.GateCounters.MasteryCurveStepCount);
        Assert.Equal(4, tuning.GateCounters.ElementMasteryRatePoints);
        Assert.Equal(4, tuning.GateCounters.StatusMasteryRatePoints);
        Assert.Equal(5000, tuning.GateCounters.FlushIntervalMs);
        Assert.Null(tuning.GateCounters.RateDivergenceWhy);
    }

    [Theory]
    [InlineData("tierLadder")]
    [InlineData("budget")]
    [InlineData("treeShareMilli")]
    [InlineData("treeBudgetMilli")]
    [InlineData("potency")]
    [InlineData("mechanism")]
    [InlineData("archetype")]
    [InlineData("exclusion")]
    [InlineData("archetypeAssignment")]
    [InlineData("designTarget")]
    [InlineData("concentration")]
    [InlineData("soulTrack")]
    [InlineData("unlockCost")]
    [InlineData("respec")]
    [InlineData("gateCounters")]
    public void A_missing_top_level_key_is_a_load_rejection_naming_it(string missingKey)
    {
        using var doc = JsonDocument.Parse(ValidJson);
        var mutated = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Name != missingKey)
                mutated[prop.Name] = prop.Value.Clone();

        var stripped = JsonSerializer.Serialize(mutated);
        var ex = Assert.Throws<PassiveTreeTuningRejection>(() => PassiveTreeTuningLoader.Parse(stripped));
        Assert.Contains(missingKey, ex.Message);
    }

    [Fact]
    public void A_missing_nested_key_is_a_load_rejection_naming_it()
    {
        using var doc = JsonDocument.Parse(ValidJson);
        var root = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            root[prop.Name] = prop.Value.Clone();

        var tierLadder = new Dictionary<string, JsonElement>(); // reqScalePoints stripped
        root["tierLadder"] = JsonSerializer.SerializeToElement(tierLadder);

        var stripped = JsonSerializer.Serialize(root);
        var ex = Assert.Throws<PassiveTreeTuningRejection>(() => PassiveTreeTuningLoader.Parse(stripped));
        Assert.Contains("reqScalePoints", ex.Message);
    }

    [Fact]
    public void SoulTrack_thetaPerSoulLevelMilli_1000_gives_one_theta_per_soul_level()
    {
        var tuning = PassiveTreeTuningLoader.Parse(ValidJson);
        // Ws = thetaPerSoulLevelMilli / 1000 — the semantic reading the resolver applies (spec-tree-
        // resolve.md §6.2). This pins the arithmetic at the tuning layer, ahead of D3 building the
        // actual resolve site.
        var ws = tuning.SoulTrack.ThetaPerSoulLevelMilli / 1000.0;
        Assert.Equal(1.0, ws);
    }

    [Fact]
    public void SoulTrack_thetaPerSoulLevelMilli_1_gives_a_thousandth()
    {
        var mutated = WithSoulTrackMilli(1);
        var tuning = PassiveTreeTuningLoader.Parse(mutated);
        var ws = tuning.SoulTrack.ThetaPerSoulLevelMilli / 1000.0;
        Assert.Equal(0.001, ws);
    }

    static string WithSoulTrackMilli(long milli)
    {
        using var doc = JsonDocument.Parse(ValidJson);
        var root = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            root[prop.Name] = prop.Value.Clone();
        root["soulTrack"] = JsonSerializer.SerializeToElement(new { thetaPerSoulLevelMilli = milli });
        return JsonSerializer.Serialize(root);
    }

    [Fact]
    public void Gate_counter_rates_default_equal_and_load_cleanly()
    {
        var tuning = PassiveTreeTuningLoader.Parse(ValidJson);
        Assert.Equal(tuning.GateCounters.StatusMasteryRatePoints, tuning.GateCounters.ElementMasteryRatePoints);
    }

    [Fact]
    public void Diverging_gate_counter_rates_without_a_why_is_refused()
    {
        using var doc = JsonDocument.Parse(ValidJson);
        var root = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            root[prop.Name] = prop.Value.Clone();
        root["gateCounters"] = JsonSerializer.SerializeToElement(new
        {
            masteryCurveFirstCount = 23, masteryCurveStepCount = 23,
            elementMasteryRatePoints = 4, statusMasteryRatePoints = 7,
            flushIntervalMs = 5000
        });
        var mutated = JsonSerializer.Serialize(root);

        var ex = Assert.Throws<PassiveTreeTuningRejection>(() => PassiveTreeTuningLoader.Parse(mutated));
        Assert.Contains("elementMasteryRatePoints", ex.Message);
        Assert.Contains("rateDivergenceWhy", ex.Message);
    }

    [Fact]
    public void Diverging_gate_counter_rates_WITH_a_stated_why_loads_cleanly()
    {
        using var doc = JsonDocument.Parse(ValidJson);
        var root = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            root[prop.Name] = prop.Value.Clone();
        root["gateCounters"] = JsonSerializer.SerializeToElement(new
        {
            masteryCurveFirstCount = 23, masteryCurveStepCount = 23,
            elementMasteryRatePoints = 4, statusMasteryRatePoints = 7,
            flushIntervalMs = 5000, rateDivergenceWhy = "deliberate test divergence"
        });
        var mutated = JsonSerializer.Serialize(root);

        var tuning = PassiveTreeTuningLoader.Parse(mutated);
        Assert.Equal("deliberate test divergence", tuning.GateCounters.RateDivergenceWhy);
    }

    [Fact]
    public void A_non_ascending_bandEdges_array_is_refused()
    {
        using var doc = JsonDocument.Parse(ValidJson);
        var root = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            root[prop.Name] = prop.Value.Clone();
        root["potency"] = JsonSerializer.SerializeToElement(new
        {
            maxNodeShareMilli = 182, minTerminalWidth = 1, bandEdgesMilli = new long[] { 46, 46, 182 }
        });
        var mutated = JsonSerializer.Serialize(root);

        Assert.Throws<PassiveTreeTuningRejection>(() => PassiveTreeTuningLoader.Parse(mutated));
    }

    [Fact]
    public void A_fractional_value_where_a_whole_number_is_expected_is_refused()
    {
        using var doc = JsonDocument.Parse(ValidJson);
        var root = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            root[prop.Name] = prop.Value.Clone();
        root["treeShareMilli"] = JsonSerializer.SerializeToElement(1000.5);
        var mutated = JsonSerializer.Serialize(root);

        var ex = Assert.Throws<PassiveTreeTuningRejection>(() => PassiveTreeTuningLoader.Parse(mutated));
        Assert.Contains("treeShareMilli", ex.Message);
    }

    [Fact]
    public void An_empty_document_is_refused()
    {
        Assert.Throws<PassiveTreeTuningRejection>(() => PassiveTreeTuningLoader.Parse(""));
    }

    [Fact]
    public void Invalid_json_is_refused_not_thrown_as_a_raw_JsonException()
    {
        Assert.Throws<PassiveTreeTuningRejection>(() => PassiveTreeTuningLoader.Parse("{ not json"));
    }

    /// <summary>D42: the dials still awaiting a real measurement carry an UNMEASURED marker in the
    /// shipped file's own `_meta`, so a reader of the raw JSON — not only of this test — sees the
    /// caveat.</summary>
    [Fact]
    public void TreeTotalPoints_and_soulTrack_are_marked_UNMEASURED_in_the_shipped_file()
    {
        Assert.Contains("UNMEASURED", ValidJson);
        using var doc = JsonDocument.Parse(ValidJson);
        var meta = doc.RootElement.GetProperty("_meta");
        var noteUnmeasured = meta.GetProperty("noteUnmeasured").GetString()!;
        Assert.Contains("treeTotalPoints", noteUnmeasured);
        Assert.Contains("soulTrack.thetaPerSoulLevelMilli", noteUnmeasured);
    }

    /// <summary>D53 (2026-09-06): `treeShareMilli` is no longer a placeholder — 1000 (100%) is the
    /// real, decided value, and the shipped note must not call it UNMEASURED any more (a coverage
    /// audit found this exact "decided but never propagated" gap 2026-09-07).</summary>
    [Fact]
    public void TreeShareMilli_is_1000_and_no_longer_listed_as_unmeasured()
    {
        using var doc = JsonDocument.Parse(ValidJson);
        Assert.Equal(1000, doc.RootElement.GetProperty("treeShareMilli").GetInt64());
        var noteUnmeasured = doc.RootElement.GetProperty("_meta").GetProperty("noteUnmeasured").GetString()!;
        Assert.DoesNotContain("treeShareMilli is UNMEASURED", noteUnmeasured);
        Assert.DoesNotContain("treeTotalPoints, treeShareMilli", noteUnmeasured);
    }

    /// <summary>
    /// Ruling R2's banned-spelling list — checked against actual JSON PROPERTY NAMES, not raw file
    /// text. The file's own `_meta.note` legitimately NAMES every banned spelling as a warning
    /// ("must never reappear: Fmax, w, Ws, …") — a naive whole-text substring search flags that
    /// warning sentence as the violation it is warning against, which is not the real check. The
    /// real check is: does any KEY in the document use a superseded spelling. `w`/`k`/`first`/`step`
    /// are legal as leaf names UNDER their own object (`unlockCost.firstPoints` still contains no
    /// bare `"first"` key — it is one property named `firstPoints`), so this walks every property
    /// path and compares the leaf name only against the fully-qualified banned form.
    /// </summary>
    [Fact]
    public void No_superseded_spelling_is_used_as_an_actual_JSON_key()
    {
        var bannedPaths = new[]
        {
            "concentration.fmax", "concentration.w", "ladder.kPoints", "tierLadder.k",
            "soulThetaWeight", "mechanism.floorMilli", "mechanism.capMilli", "nodePotencyCeiling",
            "unlockCost.first", "unlockCost.step",
        };
        using var doc = JsonDocument.Parse(ValidJson);
        var actualPaths = new List<string>();
        CollectPaths(doc.RootElement, "", actualPaths);

        foreach (var banned in bannedPaths)
            Assert.DoesNotContain(banned, actualPaths);
    }

    static void CollectPaths(JsonElement el, string prefix, List<string> outPaths)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in el.EnumerateObject())
        {
            var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            outPaths.Add(path);
            CollectPaths(prop.Value, path, outPaths);
        }
    }

    [Fact]
    public void No_superseded_gen_file_ships()
    {
        var supersededPath = Path.Combine(RepoRoot(), "data", "tuning", "passive-tree-gen.v1.json");
        Assert.False(File.Exists(supersededPath),
            "passive-tree-gen.v1.json is superseded (ruling R2) and must not exist alongside passive-tree.v1.json");
    }
}
