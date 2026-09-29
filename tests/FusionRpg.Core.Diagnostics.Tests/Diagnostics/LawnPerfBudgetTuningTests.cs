using FusionRpg.Core.Diagnostics;
using Xunit;

namespace FusionRpg.Core.Tests.Diagnostics;

/// <summary>
/// lawn LW1.1 (<c>spec-rider-default-on.md</c>) — the perf ceiling moved out of a plan document and a
/// commit message into a tunable, and this is the reader that proves the shipped file says what the
/// program measures against.
///
/// <para><b>The shipped file is the subject, not a fixture.</b> A synthetic JSON document would pass
/// while <c>gk-core/data/tuning/" + LawnPerfBudgetFiles.Current + "</c> was missing, empty or stale, which is the exact
/// class of defect this task exists to end. So the first test reads the real file through
/// <see cref="LawnPerfBudgetTuningLoader"/>; the rest pin the refusals.</para>
/// </summary>
[Trait("VerificationId", "core.lawn-perf-budget")]
public class LawnPerfBudgetTuningTests
{
    static LawnPerfBudgetTuning Shipped() =>
        LawnPerfBudgetTuningLoader.Parse(File.ReadAllText(FindBudget()));

    [Fact]
    public void The_shipped_budget_states_the_reference_ceiling()
    {
        var tuning = Shipped();

        Assert.Equal(1, tuning.SchemaVersion);   // the SHAPE did not change: sections is an added optional key
        // MOVED DELIBERATELY, exactly as this test comment predicted: CAI4.8 published v2 to add
        // ceiling.sections (this program own lawn.ai.decide share, declared unmeasured), so the revision
        // the shipped file declares is now 2.
        Assert.Equal(2, tuning.Version);

        // The ceiling the program has been measured against since LAWN-COMBAT-WIRE L-N1 (28.06% of wall
        // at 19.8 fps against a proposed <=6%). Pinned because it is the budget's whole point: a pass
        // that wants a different number publishes v2, which moves this assertion deliberately.
        Assert.Equal(6, tuning.Ceiling.PipelineSharePercent);
        Assert.Equal(300, tuning.Ceiling.ReferenceZombies);
    }

    [Fact]
    public void The_fps_half_is_declared_even_while_unmeasured()
    {
        // The two gates are both required (owner ruling 2026-09-16). "Unmeasured" and "no gate" must be
        // distinguishable, so the key is always present and `null` is what says unmeasured. This test
        // asserts the DECLARATION exists, never the current value — LW4.1 measures it, and that must not
        // turn this red.
        var text = File.ReadAllText(FindBudget());
        Assert.Contains("\"minFpsRatioOfOff\"", text, StringComparison.Ordinal);

        var tuning = Shipped();
        Assert.Equal(tuning.Ceiling.MinFpsRatioOfOff.HasValue, tuning.Ceiling.IsFpsGateMeasured);
    }

    [Fact]
    public void Omitting_the_fps_half_is_refused_rather_than_treated_as_no_gate()
    {
        var rejection = Assert.Throws<LawnPerfBudgetRejection>(
            () => LawnPerfBudgetTuningLoader.Parse(Document(minFps: null)));
        Assert.Contains("minFpsRatioOfOff", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_null_fps_half_parses_as_unmeasured()
    {
        var tuning = LawnPerfBudgetTuningLoader.Parse(Document(minFps: "null"));

        Assert.False(tuning.Ceiling.IsFpsGateMeasured);
        Assert.Null(tuning.Ceiling.MinFpsRatioOfOff);
    }

    [Fact]
    public void A_measured_fps_half_parses_as_a_ratio()
    {
        var tuning = LawnPerfBudgetTuningLoader.Parse(Document(minFps: "0.9"));

        Assert.True(tuning.Ceiling.IsFpsGateMeasured);
        Assert.Equal(0.9, tuning.Ceiling.MinFpsRatioOfOff);
    }

    [Theory]
    [InlineData("0", "pipelineSharePercent")]
    [InlineData("-3", "pipelineSharePercent")]
    [InlineData("101", "pipelineSharePercent")]
    public void A_share_that_cannot_describe_a_cost_is_refused(string share, string expectedFieldInMessage)
    {
        var rejection = Assert.Throws<LawnPerfBudgetRejection>(
            () => LawnPerfBudgetTuningLoader.Parse(Document(share: share)));
        Assert.Contains(expectedFieldInMessage, rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_zombie_reference_scenario_is_refused()
    {
        var rejection = Assert.Throws<LawnPerfBudgetRejection>(
            () => LawnPerfBudgetTuningLoader.Parse(Document(referenceZombies: "0")));
        Assert.Contains("referenceZombies", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_positive_fps_floor_is_refused_because_it_would_pass_anything()
    {
        var rejection = Assert.Throws<LawnPerfBudgetRejection>(
            () => LawnPerfBudgetTuningLoader.Parse(Document(minFps: "0")));
        Assert.Contains("minFpsRatioOfOff", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hub_refuses_to_answer_before_the_host_configured_it()
    {
        LawnPerfBudgetTuningHub.Reset();

        var error = Assert.Throws<InvalidOperationException>(() => LawnPerfBudgetTuningHub.Tuning);
        Assert.Contains(LawnPerfBudgetFiles.Current, error.Message, StringComparison.Ordinal);

        LawnPerfBudgetTuningHub.Configure(Shipped());
        try
        {
            Assert.True(LawnPerfBudgetTuningHub.IsConfigured);
            Assert.Equal(6, LawnPerfBudgetTuningHub.Tuning.Ceiling.PipelineSharePercent);
        }
        finally
        {
            LawnPerfBudgetTuningHub.Reset();
        }
    }

    /// <summary>A synthetic document. <paramref name="minFps"/> = <c>null</c> OMITS the key (the shape
    /// the loader must refuse); the string <c>"null"</c> is the legal unmeasured declaration.</summary>
    static string Document(string share = "6", string referenceZombies = "300", string? minFps = "null")
    {
        var fpsHalf = minFps is null ? string.Empty : ",\n        \"minFpsRatioOfOff\": " + minFps;

        return $$"""
        {
          "schemaVersion": 1,
          "version": 1,
          "ceiling": {
            "pipelineSharePercent": {{share}},
            "referenceZombies": {{referenceZombies}}{{fpsHalf}}
          }
        }
        """;
    }

    static string FindBudget()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", LawnPerfBudgetFiles.Current);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("no data/tuning/" + LawnPerfBudgetFiles.Current + " above the test output");
    }
}
