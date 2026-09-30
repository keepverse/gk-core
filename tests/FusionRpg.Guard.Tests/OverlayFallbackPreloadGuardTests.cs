using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Direct game launches select the Launcher by default, but have no Launcher pipe. Keep the first
/// failed probe wired to a hidden in-process preload so F10 never depends on clicking Rift first.
/// </summary>
public class OverlayFallbackPreloadGuardTests
{
    [Fact]
    public void A_missing_launcher_probe_preloads_but_does_not_toggle_the_in_process_view()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Injector", "Hud", "OverlaySwitch.cs"));

        Assert.Contains("OverlayFallbackPolicy.ShouldPreloadInProcessFallback(RpgHost.OverlayHost, _probeOk)", source, StringComparison.Ordinal);

        var ensureStart = source.IndexOf("static void EnsureOwnView()", StringComparison.Ordinal);
        var toggleStart = source.IndexOf("static void ToggleOwnView()", StringComparison.Ordinal);
        Assert.True(ensureStart >= 0 && toggleStart > ensureStart, "expected separate hidden preload and toggle paths");

        var ensureBody = source[ensureStart..toggleStart];
        Assert.Contains("OverlayViewHost.Start(RpgHost.ServerUrl)", ensureBody, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayViewHost.Toggle()", ensureBody, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
