using FusionRpg.Core.Items.Materials;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T10 (spec-craft-risk-ladder.md Testing strategy): the potential derivation,
/// the override rule, the tuning loader, and the Stage-1 policy. Closed vocabularies (class ids,
/// rung ids, verb ids) are pinned with their reason; no population count anywhere.
/// </summary>
public class PotentialTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static DeploymentHierarchyTuning Shipped() => DeploymentHierarchyTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "deployment-hierarchy.v5.json")));

    static HeadDerivationEntry Entry(string id = "item.test", string cls = "plate") =>
        new(id, cls, Array.Empty<string>());

    // ── derivation ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Derive_is_base_times_multiplier_over_1000()
    {
        var tuning = Shipped();
        // plate 100 × chaff 1000 / 1000; almanac 1900 → 190. Recomputed from the shipped file,
        // never a literal expectation of the product.
        Assert.Equal(
            100L * tuning.PotentialRarityMultiplierMilli["chaff"] / 1000,
            PotentialTable.DeriveMax(Entry(), "chaff", tuning));
        Assert.Equal(
            100L * tuning.PotentialRarityMultiplierMilli["almanac"] / 1000,
            PotentialTable.DeriveMax(Entry(), "almanac", tuning));
    }

    [Fact]
    public void Unknown_class_or_rung_refuses_naming_the_base_type()
    {
        var tuning = Shipped();
        var exClass = Assert.Throws<HeadDerivationRejection>(
            () => PotentialTable.DeriveMax(Entry("item.x", "not-a-class"), "chaff", tuning));
        Assert.Contains("item.x", exClass.Message);
        Assert.Contains("not-a-class", exClass.Message);
        var exRung = Assert.Throws<HeadDerivationRejection>(
            () => PotentialTable.DeriveMax(Entry(), "mythic", tuning));
        Assert.Contains("mythic", exRung.Message);
    }

    // ── the override rule ────────────────────────────────────────────────────────────

    [Fact]
    public void A_present_override_wins_and_an_absent_one_derives()
    {
        var tuning = Shipped() with
        {
            PotentialOverrides = new Dictionary<string, long>(StringComparer.Ordinal)
                { ["item.pinned"] = 7 },
        };
        Assert.Equal(7, PotentialTable.DeriveMax(Entry("item.pinned"), "chaff", tuning));
        Assert.NotEqual(7, PotentialTable.DeriveMax(Entry("item.other"), "chaff", tuning));
    }

    [Fact]
    public void An_expected_but_missing_override_throws_naming_the_base_type()
    {
        var tuning = Shipped() with
        {
            PotentialOverrideExpected = new[] { "item.must-pin" },
        };
        var ex = Assert.Throws<HeadDerivationRejection>(
            () => PotentialTable.DeriveMax(Entry("item.must-pin"), "chaff", tuning));
        Assert.Contains("item.must-pin", ex.Message);
    }

    // ── tuning loader ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shipped_file_parses_with_all_eleven_verbs_at_equal_cost()
    {
        var tuning = Shipped();
        // species-gear-chain T23: `repair` joins the per-verb table as the eleventh verb. Reads
        // CraftOperations.All.Count live rather than a second literal (population-pin SE3.3,
        // 2026-09-19); pinned once, MaterialVocabularyTests.
        Assert.Equal(CraftOperations.All.Count, tuning.PotentialCostPerVerb.Count);
        Assert.All(tuning.PotentialCostPerVerb.Values, v => Assert.Equal(1, v));
        Assert.Equal(50, tuning.CraftWearPerAttemptMilli);
    }

    [Fact]
    public void A_wear_ratio_outside_0_to_1000_is_a_load_rejection()
    {
        var bad = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "deployment-hierarchy.v5.json"))
            .Replace("\"craftWearPerAttemptMilli\": 50", "\"craftWearPerAttemptMilli\": 1001");
        Assert.Throws<DeploymentHierarchyTuningRejection>(() => DeploymentHierarchyTuningLoader.Parse(bad));
    }

    [Fact]
    public void A_missing_verb_cost_is_a_load_rejection_not_a_free_verb()
    {
        var bad = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "deployment-hierarchy.v5.json"))
            .Replace("\"forge-gem\": 1,", "");
        Assert.Throws<DeploymentHierarchyTuningRejection>(() => DeploymentHierarchyTuningLoader.Parse(bad));
    }

    // ── numeric discipline ───────────────────────────────────────────────────────────

    [Fact]
    public void The_derivation_throws_rather_than_wrapping_at_the_boundary()
    {
        var tuning = Shipped() with
        {
            PotentialBaseByClass = new Dictionary<string, long>(StringComparer.Ordinal)
                { ["plate"] = long.MaxValue },
        };
        // MaxValue × 1000 overflows a long: checked must throw, never wrap.
        Assert.Throws<OverflowException>(() => PotentialTable.DeriveMax(Entry(), "chaff", tuning));
    }

    // ── the Stage-1 policy ───────────────────────────────────────────────────────────

    [Fact]
    public void Stage_is_assured_while_potential_remains_and_exhaustion_decays()
    {
        Assert.Equal(CraftRiskStage.Assured, CraftRiskPolicy.StageFor(1));
        Assert.Equal(CraftRiskStage.Assured, CraftRiskPolicy.StageFor(long.MaxValue));
        Assert.Equal(CraftRiskStage.Exhausted, CraftRiskPolicy.StageFor(0));
        // T24: exhaustion now carries Stage 2's consequence — the attempt decays durability. The T10
        // stub this line used to check (`Assert.False(CraftRiskPolicy.CanDecay)`) is the thing T24
        // replaced, so it moves to the live contract rather than being deleted.
        Assert.False(CraftRiskPolicy.CanDecay(1));
        Assert.True(CraftRiskPolicy.CanDecay(0));
        Assert.False(CraftRiskPolicy.CanDecay(null));   // never derived ⇒ never exhausted
    }
}
