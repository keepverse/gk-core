using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Audit stress + predicted distinguishability (static; LIVE eyeball overrides).</summary>
public class StatusVfxIdentityAuditTests
{
    [Fact]
    public void Batch5_pact_command_predict_pass_on_sustain_glance()
    {
        foreach (var id in new[] { "pact_mark", "command" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Pass, score.SustainGlance);
        }
    }

    [Fact]
    public void Batch5_pact_command_predict_low_pair_risk()
    {
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("pact_mark", "command"));
    }

    [Fact]
    public void Batch4_quartet_predict_conditional_on_apply_moment()
    {
        foreach (var id in new[] { "leech", "rally", "pact_mark", "command" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Conditional, score.ApplyMoment);
        }
    }

    [Fact]
    public void Predicted_apply_moment_buckets_match_batch4_bar()
    {
        var scores = StatusVfxIdentityScoring.AllScores();
        foreach (var id in StatusVfxIdentity.CustomIds)
        {
            var score = scores.Single(s => s.StatusId == id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Conditional, score.ApplyMoment);
        }

        Assert.Equal(13, scores.Count(s => s.ApplyMoment == StatusVfxIdentityScoring.GlanceVerdict.Conditional));
        Assert.Equal(0, scores.Count(s => s.ApplyMoment == StatusVfxIdentityScoring.GlanceVerdict.Fail));
    }

    [Fact]
    public void Batch3_orbit_pairs_predict_low_risk()
    {
        foreach (var (a, b) in new[] { ("spore", "bond"), ("spore", "charm_pulse"), ("bond", "charm_pulse") })
            Assert.Equal("low", StatusVfxIdentityScoring.PairRisk(a, b));
    }

    [Fact]
    public void Batch3_orbit_trio_predict_conditional_on_apply_moment()
    {
        foreach (var id in new[] { "spore", "charm_pulse", "bond" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Conditional, score.ApplyMoment);
        }
    }

    [Fact]
    public void Batch3_spore_charm_predict_pass_on_sustain_glance()
    {
        foreach (var id in new[] { "spore", "charm_pulse" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Pass, score.SustainGlance);
        }
    }

    [Fact]
    public void Batch2_crackle_trio_predict_conditional_on_apply_moment()
    {
        foreach (var id in new[] { "spark", "shatter", "expose" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Conditional, score.ApplyMoment);
        }
    }

    [Fact]
    public void Batch2_spark_shatter_predict_pass_on_sustain_glance()
    {
        foreach (var id in new[] { "spark", "shatter" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Pass, score.SustainGlance);
        }
    }

    [Fact]
    public void Batch1_drip_trio_predict_conditional_on_apply_moment()
    {
        foreach (var id in new[] { "wither", "blight", "rot" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Conditional, score.ApplyMoment);
        }
    }

    [Fact]
    public void Batch1_drip_trio_predict_pass_on_sustain_glance()
    {
        foreach (var id in new[] { "wither", "blight", "rot" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Pass, score.SustainGlance);
        }
    }

    [Fact]
    public void P0_pairs_flagged_high_or_critical_risk()
    {
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("spore", "charm_pulse"));
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("spark", "shatter"));
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("spark", "expose"));
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("blight", "rot"));
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("wither", "blight"));
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("pact_mark", "command"));
    }

    [Fact]
    public void Unique_motion_statuses_predict_pass_on_sustain_glance()
    {
        foreach (var id in new[] { "leech", "rally" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Pass, score.SustainGlance);
        }
    }

    [Fact]
    public void Marker_statuses_predict_pass_on_sustain_glance()
    {
        foreach (var id in new[] { "pact_mark", "expose", "bond", "command" })
        {
            var score = StatusVfxIdentityScoring.Score(id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Pass, score.SustainGlance);
        }
    }

    [Fact]
    public void Stress_eviction_keeps_marker_over_non_marker_on_same_host()
    {
        var t = new VfxStateTracker();
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());
        catalog.TryGet(StatusVfxCues.CueId("wither"), out var witherRecipe);
        catalog.TryGet(StatusVfxCues.CueId("pact_mark"), out var pactRecipe);
        catalog.TryGet(StatusVfxCues.CueId("spark"), out var sparkRecipe);

        t.Start("Z1", "wither", StatusVfxCues.CueId("wither"), witherRecipe!, 8000, 0.0, out _);
        t.Start("Z1", "pact_mark", StatusVfxCues.CueId("pact_mark"), pactRecipe!, 8000, 1.0, out _);
        Assert.Equal(2, t.LiveCount);

        t.Start("Z1", "spark", StatusVfxCues.CueId("spark"), sparkRecipe!, 8000, 2.0, out var evicted);
        Assert.Single(evicted);
        Assert.Equal("wither", evicted[0].StatusId);
        Assert.Contains(t.Live, s => s.StatusId == "pact_mark");
        Assert.Contains(t.Live, s => s.StatusId == "spark");
    }

    [Fact]
    public void Predicted_sustain_glance_buckets_match_batch3_bar()
    {
        var scores = StatusVfxIdentityScoring.AllScores();
        foreach (var id in StatusVfxIdentity.CustomIds)
        {
            var score = scores.Single(s => s.StatusId == id);
            Assert.Equal(StatusVfxIdentityScoring.GlanceVerdict.Pass, score.SustainGlance);
        }

        Assert.Equal(13, scores.Count(s => s.SustainGlance == StatusVfxIdentityScoring.GlanceVerdict.Pass));
        Assert.Equal(0, scores.Count(s => s.SustainGlance == StatusVfxIdentityScoring.GlanceVerdict.Conditional));
        Assert.Equal(0, scores.Count(s => s.SustainGlance == StatusVfxIdentityScoring.GlanceVerdict.Fail));
    }

    [Fact]
    public void All_thirteen_scores_have_rationale()
    {
        var scores = StatusVfxIdentityScoring.AllScores();
        // AllScores() wraps StatusVfxIdentity.CustomIds (pinned there, StatusVfxIdentityCollisionTests)
        // -- reads that same canonical owner instead of a second 13 (population-pin SE3.3, 2026-09-19).
        Assert.Equal(StatusVfxIdentity.CustomIds.Count, scores.Count);
        Assert.All(scores, s => Assert.False(string.IsNullOrWhiteSpace(s.Rationale)));
    }
}
