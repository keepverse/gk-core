using System;
using System.IO;
using FusionRpg.Core.Diagnostics;
using Xunit;

namespace FusionRpg.Core.Tests.Diagnostics;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.8): the per-section share this program owes — the
/// `ceiling.sections.lawn.ai.decide` key the budget file's own `_meta` anticipates (*"a later lawn perf
/// section (for example `lawn.ai.decide`, whose share the combat-ai program owes) is added beside these
/// three keys, never as a second file"*).
///
/// <para><b>Declared but UNMEASURED is the honest seed, and the tests say so.</b> The file's contract is
/// that <c>null</c> means "cannot pass yet", never "no gate" — a clean 300-zombie A/B has not been taken on
/// the current build (that is `CAI5.1`'s live probe), so the key exists and its value is null. A gate that
/// read null as a pass would ship a feature nobody measured.</para>
/// </summary>
[Trait("VerificationId", "core.lawn-perf-budget")]
public class LawnPerfBudgetSectionShareTests
{
    static string ShippedText() => File.ReadAllText(
        Path.Combine(FindRepoRoot(), "data", "tuning", LawnPerfBudgetFiles.Current));

    [Fact]
    public void The_shipped_revision_declares_this_programs_section_and_it_is_unmeasured()
    {
        var tuning = LawnPerfBudgetTuningLoader.Parse(ShippedText());

        Assert.True(tuning.Ceiling.TryGetSectionShare("lawn.ai.decide", out var share));
        Assert.Null(share);                                       // declared, not measured
        Assert.False(tuning.Ceiling.IsSectionMeasured("lawn.ai.decide"));   // so no gate may pass on it
    }

    [Fact]
    public void A_section_the_document_does_not_name_has_no_share_at_all()
    {
        var tuning = LawnPerfBudgetTuningLoader.Parse(ShippedText());

        Assert.False(tuning.Ceiling.TryGetSectionShare("lawn.not-a-section", out var share));
        Assert.Null(share);
    }

    [Fact]
    public void A_measured_section_share_is_read_by_name()
    {
        var tuning = LawnPerfBudgetTuningLoader.Parse(
            """{"schemaVersion":1,"version":2,"ceiling":{"pipelineSharePercent":6,"referenceZombies":300,"minFpsRatioOfOff":null,"sections":{"lawn.ai.decide":2.5}}}""");

        Assert.True(tuning.Ceiling.TryGetSectionShare("lawn.ai.decide", out var share));
        Assert.Equal(2.5, share);
        Assert.True(tuning.Ceiling.IsSectionMeasured("lawn.ai.decide"));
    }

    [Theory]
    [InlineData("\"oops\"")]   // a string is a typo, not a share
    [InlineData("0")]          // a share of wall is (0, 100]
    [InlineData("101")]
    public void A_malformed_section_share_is_refused_rather_than_defaulted(string value)
    {
        var json = "{\"schemaVersion\":1,\"version\":2,\"ceiling\":{\"pipelineSharePercent\":6,"
                 + "\"referenceZombies\":300,\"minFpsRatioOfOff\":null,\"sections\":{\"lawn.ai.decide\":" + value + "}}}";

        Assert.Throws<LawnPerfBudgetRejection>(() => LawnPerfBudgetTuningLoader.Parse(json));
    }

    /// <summary>Nothing about the three original keys moved: the publish was an ADDITION.</summary>
    [Fact]
    public void The_three_original_keys_are_unchanged_by_the_addition()
    {
        var tuning = LawnPerfBudgetTuningLoader.Parse(ShippedText());

        Assert.Equal(6, tuning.Ceiling.PipelineSharePercent);
        Assert.Equal(300, tuning.Ceiling.ReferenceZombies);
        Assert.Null(tuning.Ceiling.MinFpsRatioOfOff);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "collect-class-system-realrun.ps1")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
