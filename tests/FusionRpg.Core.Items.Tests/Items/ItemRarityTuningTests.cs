using System.Text.Json;
using FusionRpg.Core.Items;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `rarity-bands` (item module 7) against the REAL, shipped `gk-core/data/tuning/item-rarity.v1.json` — the
/// re-derived drop-weight and enhancement-cap tables (spec-rarity-bands.md, re-derivations 1 and 2).
/// </summary>
public class ItemRarityTuningTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string RawJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-rarity.v1.json"));

    static IReadOnlyDictionary<string, ItemRarityRungTuning> Load() => ItemRarityTuning.Parse(RawJson());

    [Fact]
    public void All_ten_rungs_are_present()
    {
        var tuning = Load();
        Assert.Equal(RarityLadder.RungIds.ToHashSet(), tuning.Keys.ToHashSet());
    }

    [Fact]
    public void Drop_weights_sum_to_100000_and_are_monotone_decreasing()
    {
        var tuning = Load();
        var ordered = RarityLadder.RungIds.Select(id => tuning[id].DropWeightPer100k).ToList();

        Assert.Equal(100_000, ordered.Sum());
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i] < ordered[i - 1], $"weight at index {i} ({ordered[i]}) is not strictly below index {i - 1} ({ordered[i - 1]})");
    }

    [Fact]
    public void Chaff_is_the_balancing_row_at_40700_and_almanac_is_pinned_at_700()
    {
        var tuning = Load();
        Assert.Equal(40_700, tuning["chaff"].DropWeightPer100k);
        Assert.Equal(700, tuning["almanac"].DropWeightPer100k);
    }

    [Fact]
    public void Enhance_cap_is_monotone_non_increasing_across_the_ten_rungs()
    {
        // Shrinking, not merely per-rung -- the two-rung smoothing over step(rung) is what buys this.
        var tuning = Load();
        var ordered = RarityLadder.RungIds.Select(id => tuning[id].EnhanceCapMilli).ToList();

        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i] <= ordered[i - 1], $"enhance_cap at index {i} ({ordered[i]}) exceeds index {i - 1} ({ordered[i - 1]})");
    }

    [Fact]
    public void Enhance_cap_asymptotes_below_one_rung_step_at_every_rung()
    {
        // gain(n) = enhance_cap(rung) x n/(n+K) never reaches enhance_cap, and enhance_cap itself is
        // StepMarginAlphaMilli x (step-1); StepMarginAlphaMilli < 1000 (one full multiplicative step
        // in per-mille terms) is what makes the asymptote strictly below one rung step at every rung.
        using var doc = JsonDocument.Parse(RawJson());
        var alpha = doc.RootElement.GetProperty("enhanceCapStepMarginAlphaMilli").GetInt32();
        Assert.True(alpha < 1000, $"StepMarginAlphaMilli={alpha} must be below 1000 (one full step) or the asymptote can reach a full step");
    }

    [Fact]
    public void Chaff_and_almanac_carry_the_edge_rungs_borrowed_value()
    {
        // chaff has no measured magnitude of its own (rolls no affixes) and takes sprout's;
        // almanac has no rung above it and takes sunwoven's -- the conservative, monotone-preserving reading.
        var tuning = Load();
        Assert.Equal(tuning["sprout"].EnhanceCapMilli, tuning["chaff"].EnhanceCapMilli);
        Assert.Equal(tuning["sunwoven"].EnhanceCapMilli, tuning["almanac"].EnhanceCapMilli);
    }

    [Fact]
    public void Power_ceiling_shares_are_zero_at_chaff_and_1000_at_almanac_and_monotone_non_decreasing()
    {
        var tuning = Load();
        var ordered = RarityLadder.RungIds.Select(id => tuning[id].PowerCeilingShareMilli).ToList();

        Assert.Equal(0, ordered[0]);
        Assert.Equal(1000, ordered[^1]);
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i] >= ordered[i - 1], $"power_ceiling share at index {i} ({ordered[i]}) is below index {i - 1} ({ordered[i - 1]})");
    }

    [Fact]
    public void Parse_rejects_a_document_whose_drop_weights_do_not_sum_to_100000()
    {
        var bad = RawJson().Replace("\"almanac\":    { \"dropWeightPer100k\": 700,", "\"almanac\":    { \"dropWeightPer100k\": 701,");
        Assert.NotEqual(RawJson(), bad); // guards the fixture itself against a silent no-op replace
        Assert.Throws<ItemRarityTuningRejection>(() => ItemRarityTuning.Parse(bad));
    }

    [Fact]
    public void Parse_rejects_a_document_missing_a_rung() =>
        Assert.Throws<ItemRarityTuningRejection>(() => ItemRarityTuning.Parse("""{"rungs":{}}"""));

    [Fact]
    public void Parse_rejects_empty_input() =>
        Assert.Throws<ItemRarityTuningRejection>(() => ItemRarityTuning.Parse(""));
}
