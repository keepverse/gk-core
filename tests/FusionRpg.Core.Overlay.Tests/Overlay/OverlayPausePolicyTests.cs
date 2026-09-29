using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

/// <summary>
/// When the lawn should hold still because the player is looking at the web UI instead.
/// Pure: the point is that a player never loses a run to a menu they opened deliberately.
/// </summary>
public class OverlayPausePolicyTests
{
    [Fact]
    public void Looking_away_mid_run_pauses()
    {
        Assert.True(OverlayPausePolicy.ShouldPause(enabled: true, matchActive: true, playerAway: true));
    }

    [Fact]
    public void Playing_the_lawn_never_pauses()
    {
        Assert.False(OverlayPausePolicy.ShouldPause(enabled: true, matchActive: true, playerAway: false));
    }

    [Fact]
    public void Outside_a_run_there_is_nothing_to_protect()
    {
        // Menus and the almanac are not a run; freezing time there would just look broken.
        Assert.False(OverlayPausePolicy.ShouldPause(enabled: true, matchActive: false, playerAway: true));
    }

    [Fact]
    public void The_player_can_switch_it_off()
    {
        Assert.False(OverlayPausePolicy.ShouldPause(enabled: false, matchActive: true, playerAway: true));
    }

    // ---- handing time back ----

    [Fact]
    public void Pausing_reports_the_frozen_scale()
    {
        Assert.Equal(0f, OverlayPausePolicy.PausedTimeScale);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2.5f)]   // a speed cheat was running
    [InlineData(0.5f)]
    public void Resuming_restores_exactly_what_was_running_before(float before)
    {
        // Restoring a hardcoded 1.0 would silently cancel the player's own timescale setting.
        Assert.Equal(before, OverlayPausePolicy.ResumeScale(capturedScale: before));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void A_nonsense_captured_scale_resumes_at_normal_speed(float bad)
    {
        // Capturing while something else already froze time must not leave the game stuck at 0.
        Assert.Equal(1f, OverlayPausePolicy.ResumeScale(bad));
    }

    [Fact]
    public void An_absurd_captured_scale_is_clamped()
    {
        Assert.Equal(OverlayPausePolicy.MaxResumeScale, OverlayPausePolicy.ResumeScale(1000f));
    }
}
