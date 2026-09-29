using FusionRpg.Core.BuildPresets;
using FusionRpg.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.BuildPresets;

/// <summary>
/// build-preset BP1.10 (spec-preset-store.md "Tunables") — the first version of a new tuning domain:
/// the build-preset library's soft max, its loader's rejections, and the hub.
/// </summary>
[Trait("VerificationId", "core.build-preset")]
public class BuildPresetTuningTests
{
    static string ShippedPath() =>
        Path.Combine(CoreRoot.Path, "data", "tuning", "build-preset.v1.json");

    [Fact]
    public void The_shipped_v1_file_loads_and_its_soft_max_is_a_positive_long()
    {
        var tuning = BuildPresetTuningLoader.Parse(File.ReadAllText(ShippedPath()));
        Assert.Equal(1, tuning.SchemaVersion);
        Assert.Equal(1, tuning.Version);
        // The acceptance's own floor, never the shipped literal: softMaxBuildPresets is a balance value
        // (a list-length soft refusal on create) that a later gk-core/tools/tuning/publish.py revision may raise.
        Assert.True(tuning.SoftMaxBuildPresets >= 1, $"softMaxBuildPresets was {tuning.SoftMaxBuildPresets}");
    }

    [Fact]
    public void A_missing_key_is_a_load_rejection_naming_it()
    {
        var ex = Assert.Throws<BuildPresetTuningRejection>(() =>
            BuildPresetTuningLoader.Parse("""{"schemaVersion":1,"version":1}"""));
        Assert.Contains("softMaxBuildPresets", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-9000000000L)]
    public void A_non_positive_soft_max_is_rejected(long value)
    {
        var ex = Assert.Throws<BuildPresetTuningRejection>(() => BuildPresetTuningLoader.Parse(
            $$"""{"schemaVersion":1,"version":1,"softMaxBuildPresets":{{value}}}"""));
        Assert.Contains("softMaxBuildPresets", ex.Message);
    }

    [Fact]
    public void A_non_long_soft_max_is_rejected_by_the_same_key_name()
    {
        var ex = Assert.Throws<BuildPresetTuningRejection>(() =>
            BuildPresetTuningLoader.Parse("""{"schemaVersion":1,"version":1,"softMaxBuildPresets":"32"}"""));
        Assert.Contains("softMaxBuildPresets", ex.Message);
    }

    [Fact]
    public void An_empty_or_malformed_document_is_rejected()
    {
        Assert.Throws<BuildPresetTuningRejection>(() => BuildPresetTuningLoader.Parse(""));
        Assert.Throws<BuildPresetTuningRejection>(() => BuildPresetTuningLoader.Parse("   "));
        Assert.Throws<BuildPresetTuningRejection>(() => BuildPresetTuningLoader.Parse("{not json"));
    }

    [Fact]
    public void The_hub_serves_the_configured_tuning_and_refuses_a_null()
    {
        Assert.Throws<ArgumentNullException>(() => BuildPresetTuningHub.Configure(null!));

        var tuning = BuildPresetTuningLoader.Parse(File.ReadAllText(ShippedPath()));
        BuildPresetTuningHub.Configure(tuning);
        Assert.True(BuildPresetTuningHub.IsConfigured);
        Assert.Same(tuning, BuildPresetTuningHub.Tuning);
    }
}
