namespace FusionRpg.Core.Overlay;

/// <summary>
/// Decides when the launcher-selected host must be backed by the injector-owned view.
/// A direct game launch has no Launcher pipe, but must still make the F10 overlay available.
/// </summary>
public static class OverlayFallbackPolicy
{
    /// <summary>
    /// The configured Launcher remains authoritative when it answers the probe. Only a confirmed
    /// absence permits the in-process fallback; this avoids two WebView2 windows for normal launcher
    /// sessions while keeping direct game launches discoverable through F10.
    /// </summary>
    public static bool ShouldPreloadInProcessFallback(OverlayHostMode selectedHost, bool launcherReachable) =>
        selectedHost == OverlayHostMode.Launcher && !launcherReachable;
}
