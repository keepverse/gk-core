using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `deployment-hierarchy` module 7 §2, pulled forward as species-gear-chain T11: durability's
/// derivation is PURELY derived (no override — `:408`'s Never stands), same checked long shape as
/// potential. Closed vocabularies pinned with reason; no population count anywhere.
/// </summary>
public class DurabilityTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static DeploymentHierarchyTuning Shipped() => DeploymentHierarchyTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "deployment-hierarchy.v5.json")));

    static HeadDerivationEntry Entry(string id = "item.test", string cls = "plate") =>
        new(id, cls, Array.Empty<string>());

    [Fact]
    public void Derive_is_base_times_multiplier_over_1000()
    {
        var tuning = Shipped();
        Assert.Equal(
            100L * tuning.DurabilityRarityMultiplierMilli["chaff"] / 1000,
            DurabilityTable.DeriveMax(Entry(), "chaff", tuning));
        Assert.Equal(
            100L * tuning.DurabilityRarityMultiplierMilli["almanac"] / 1000,
            DurabilityTable.DeriveMax(Entry(), "almanac", tuning));
    }

    [Fact]
    public void Unknown_class_or_rung_refuses_naming_the_base_type()
    {
        var tuning = Shipped();
        var exClass = Assert.Throws<HeadDerivationRejection>(
            () => DurabilityTable.DeriveMax(Entry("item.x", "not-a-class"), "chaff", tuning));
        Assert.Contains("item.x", exClass.Message);
        var exRung = Assert.Throws<HeadDerivationRejection>(
            () => DurabilityTable.DeriveMax(Entry(), "mythic", tuning));
        Assert.Contains("mythic", exRung.Message);
    }

    [Fact]
    public void The_derivation_throws_rather_than_wrapping_at_the_boundary()
    {
        var tuning = Shipped() with
        {
            DurabilityBaseByClass = new Dictionary<string, long>(StringComparer.Ordinal)
                { ["plate"] = long.MaxValue },
        };
        Assert.Throws<OverflowException>(() => DurabilityTable.DeriveMax(Entry(), "chaff", tuning));
    }

    [Fact]
    public void Potential_and_durability_share_the_derivation_shape_but_not_the_override()
    {
        // OQ1's recommendation, pinned: same fields, same formula, same refusals — and durability
        // takes no override while potential may. The asymmetry is the filed ask-#3 reconciliation,
        // not an oversight.
        var tuning = Shipped() with
        {
            PotentialOverrides = new Dictionary<string, long>(StringComparer.Ordinal)
                { ["item.pinned"] = 7 },
        };
        Assert.Equal(7, PotentialTable.DeriveMax(Entry("item.pinned"), "chaff", tuning));
        Assert.NotEqual(
            7, DurabilityTable.DeriveMax(Entry("item.pinned"), "chaff", tuning));
    }
}
