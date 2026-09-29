namespace FusionRpg.Core.Overlay;

/// <summary>
/// Whether the lawn should hold still because the player is looking at the web UI instead of
/// the game. Opening the control room mid-wave should not cost a run.
/// </summary>
public static class OverlayPausePolicy
{
    public static float PausedTimeScale => OverlayTuningHub.Tuning.Pause.PausedTimeScale;

    /// <summary>Ceiling on a restored scale, matching the existing G-TIMESCALE clamp.</summary>
    public static float MaxResumeScale => OverlayTuningHub.Tuning.Pause.MaxResumeScale;

    /// <summary>
    /// Pause only inside a live run: freezing time in menus or the almanac would just look broken,
    /// and there is nothing to lose there.
    /// </summary>
    public static bool ShouldPause(bool enabled, bool matchActive, bool playerAway) =>
        enabled && matchActive && playerAway;

    /// <summary>
    /// The scale to hand back on resume. Restoring a hardcoded 1.0 would silently cancel the
    /// player's own timescale setting, and a captured 0 (something else had already frozen time)
    /// would leave the game stuck.
    /// </summary>
    public static float ResumeScale(float capturedScale)
    {
        if (float.IsNaN(capturedScale) || capturedScale <= 0f) return 1f;
        return capturedScale > MaxResumeScale ? MaxResumeScale : capturedScale;
    }
}
