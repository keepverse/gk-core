using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// battle-timeline T13 / injector write-path honesty (backlog-clean-up BCU8.3). B27's live run
/// measured the kernel safe on 2026-09-20, so the legacy `FUSIONRPG_KERNEL_GRIDS=0` kill switch and
/// the two accumulator grids it fell back to are deleted: one scheduler in the injector, which is
/// `spec-injector-kernel-drive.md` §10 success criterion 1.
///
/// <para>Text-based, like the other injector guards — the injector assembly needs a real PVZ Fusion
/// install and never builds under CI, so a source scan is the only regression coverage that runs.</para>
///
/// <para>The third test is the one worth keeping. T4.14 (D15) moved status timing onto
/// `AdvancedEffectClock`, but the injection point it chose was the legacy DoT accumulator — which
/// B26 had already gated behind the kernel, so the fix was <b>inert on every live board</b> from the
/// day it landed. A guard that only checks the dead symbols are gone would happily pass on a build
/// where the effect clock never advances.</para>
/// </summary>
public class InjectorKernelGridsGuardTests
{
    [Fact]
    public void The_accumulator_grids_are_gone_from_the_effect_runtime()
    {
        var text = CodeOf(File.ReadAllText(Injector("Effects", "EffectRuntime.cs")));

        Assert.DoesNotContain("_dotAccum", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_shieldAccum", text, StringComparison.Ordinal);
        Assert.DoesNotContain("public static void TickDots(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("public static void TickShields(", text, StringComparison.Ordinal);

        // The survivors, pinned so this test cannot pass by the file being emptied or renamed: the
        // two scheduled handlers the kernel dispatches, and the clock advance the kernel calls.
        Assert.Contains("public static void PulseDotsNow()", text, StringComparison.Ordinal);
        Assert.Contains("public static void PulseShieldsNow()", text, StringComparison.Ordinal);
        Assert.Contains("public static void AdvanceEffectClock(float scaledDeltaSeconds)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_kernel_kill_switch_and_its_call_site_are_gone()
    {
        var kernel = CodeOf(File.ReadAllText(Injector("Effects", "KernelDriveHost.cs")));
        Assert.DoesNotContain("FUSIONRPG_KERNEL_GRIDS", kernel, StringComparison.Ordinal);
        Assert.DoesNotContain("GridsOnKernel", kernel, StringComparison.Ordinal);
        Assert.DoesNotContain("DrivingGrids", kernel, StringComparison.Ordinal);

        var loop = CodeOf(File.ReadAllText(Injector("Host", "InjectorLoop.cs")));
        Assert.DoesNotContain("DrivingGrids", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectRuntime.TickDots(", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectRuntime.TickShields(", loop, StringComparison.Ordinal);
    }

    /// <summary>
    /// D15's contract, pinned at the one place that can break it silently: the effect clock must be
    /// advanced from the kernel tick, off the same scaled delta the kernel itself advances by. If a
    /// later edit moves the advance somewhere a live board never reaches, this fails instead of
    /// shipping another inert clock.
    /// </summary>
    [Fact]
    public void The_effect_clock_advances_from_the_kernel_tick()
    {
        var text = CodeOf(File.ReadAllText(Injector("Effects", "KernelDriveHost.cs")));

        Assert.Contains("EffectRuntime.AdvanceEffectClock(scaledDeltaTime);", text, StringComparison.Ordinal);

        // And only after the pause guard, so a paused game freezes the pulse schedule and the status
        // clock together rather than advancing one of them.
        var guard = text.IndexOf("if (!(scaledDeltaTime > 0f)) return;", StringComparison.Ordinal);
        var advance = text.IndexOf("EffectRuntime.AdvanceEffectClock(scaledDeltaTime);", StringComparison.Ordinal);
        Assert.True(guard >= 0, "pause guard missing from KernelDriveHost.Tick");
        Assert.True(advance > guard, "the effect clock advances before the pause guard");
    }

    /// <summary>
    /// Reads CODE ONLY — comment lines are stripped first, the same lesson
    /// <see cref="EntityFields12PlusGuardTests"/> records: a comment explaining why a symbol was
    /// deleted contains that symbol, so a raw scan reports the deletion as still present and the
    /// guard cries wolf until someone weakens it.
    /// </summary>
    static string CodeOf(string text) =>
        string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    static string Injector(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Injector" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return path;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
