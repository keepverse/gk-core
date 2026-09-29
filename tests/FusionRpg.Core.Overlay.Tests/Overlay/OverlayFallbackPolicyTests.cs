using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

public class OverlayFallbackPolicyTests
{
    [Fact]
    public void A_missing_launcher_preloads_the_in_process_fallback()
    {
        Assert.True(OverlayFallbackPolicy.ShouldPreloadInProcessFallback(
            OverlayHostMode.Launcher, launcherReachable: false));
    }

    [Fact]
    public void A_reachable_launcher_remains_the_only_host()
    {
        Assert.False(OverlayFallbackPolicy.ShouldPreloadInProcessFallback(
            OverlayHostMode.Launcher, launcherReachable: true));
    }

    [Fact]
    public void The_injector_selection_never_uses_the_launcher_fallback_path()
    {
        Assert.False(OverlayFallbackPolicy.ShouldPreloadInProcessFallback(
            OverlayHostMode.Injector, launcherReachable: false));
    }
}
