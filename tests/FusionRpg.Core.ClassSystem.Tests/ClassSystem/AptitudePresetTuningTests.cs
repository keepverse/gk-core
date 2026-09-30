using FusionRpg.Core.Stats.Aptitudes;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>EP1.3 (spec-assign-ladder.md "Tunables") — `AptitudePresetTuningLoader`'s load contract
/// for `assignLadder.order`: every id known, no duplicates, the last id is `even`.</summary>
public class AptitudePresetTuningTests
{
    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string ShippedJson() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "aptitude-presets.v2.json"));

    [Fact]
    public void The_shipped_v2_file_parses_and_carries_the_ladder_order()
    {
        var tuning = AptitudePresetTuningLoader.Parse(ShippedJson());
        Assert.Equal(
            new[] { "active-preset", "species-favour", "posture", "even" },
            tuning.AssignLadder.Order);
    }

    static string DocWith(string orderJson) => $$"""
        {
          "schemaVersion": 1,
          "version": 2,
          "softMaxPresets": 32,
          "defaultRowAbsMax": 1000,
          "assignLadder": { "order": {{orderJson}} }
        }
        """;

    [Fact]
    public void An_order_not_ending_on_even_is_a_load_rejection_naming_the_key()
    {
        var ex = Assert.Throws<AptitudePresetTuningRejection>(
            () => AptitudePresetTuningLoader.Parse(DocWith("""["active-preset","species-favour"]""")));
        Assert.Contains("assignLadder.order", ex.Message);
        Assert.Contains("even", ex.Message);
    }

    [Fact]
    public void A_duplicate_rung_is_a_load_rejection_naming_the_key()
    {
        var ex = Assert.Throws<AptitudePresetTuningRejection>(
            () => AptitudePresetTuningLoader.Parse(DocWith("""["even","even"]""")));
        Assert.Contains("assignLadder.order", ex.Message);
    }

    [Fact]
    public void An_unknown_rung_id_is_a_load_rejection_naming_the_key()
    {
        var ex = Assert.Throws<AptitudePresetTuningRejection>(
            () => AptitudePresetTuningLoader.Parse(DocWith("""["posture-force","even"]""")));
        Assert.Contains("assignLadder.order", ex.Message);
        Assert.Contains("posture-force", ex.Message);
    }

    [Fact]
    public void A_missing_assignLadder_block_is_a_load_rejection()
    {
        const string doc = """
            {
              "schemaVersion": 1,
              "version": 2,
              "softMaxPresets": 32,
              "defaultRowAbsMax": 1000
            }
            """;
        var ex = Assert.Throws<AptitudePresetTuningRejection>(() => AptitudePresetTuningLoader.Parse(doc));
        Assert.Contains("assignLadder", ex.Message);
    }

    [Fact]
    public void An_empty_order_is_a_load_rejection()
    {
        var ex = Assert.Throws<AptitudePresetTuningRejection>(
            () => AptitudePresetTuningLoader.Parse(DocWith("[]")));
        Assert.Contains("assignLadder.order", ex.Message);
    }

    [Fact]
    public void The_minimal_legal_order_of_just_even_loads()
    {
        var tuning = AptitudePresetTuningLoader.Parse(DocWith("""["even"]"""));
        Assert.Equal(new[] { "even" }, tuning.AssignLadder.Order);
    }
}
