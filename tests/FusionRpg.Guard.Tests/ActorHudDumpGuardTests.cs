using Xunit;

namespace FusionRpg.Guard.Tests;

public sealed class ActorHudDumpGuardTests
{
    [Fact]
    public void GameDumps_includes_actorHud()
    {
        var text = ReadInjector("GameDumps.cs");
        Assert.Contains("ActorHudObserve.AttachRow", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudBuilder_read_surface_compliance()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudBuilder.cs"));
        Assert.DoesNotContain("FindObjectsOfType", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FusionRpg.Data", text, StringComparison.Ordinal);
        Assert.DoesNotContain("theLevel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("theShieldHealth", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/unique", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ActorHub.Resolve", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectorBootstrap_installs_actor_hud_invalidator()
    {
        var text = ReadInjector(Path.Combine("Host", "InjectorBootstrap.cs"));
        Assert.Contains("ActorHudInvalidator.Install", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityApply_marks_hud_dirty_after_pin()
    {
        var text = ReadInjector(Path.Combine("Stats", "EntityApply.cs"));
        Assert.Contains("InjectorDerivedOverride.Pin(key, resolved.Derived)", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudCache.MarkDirty(key)", text, StringComparison.Ordinal);
        Assert.Contains("InjectorDerivedOverride.Pin(key, resolvedZ.Derived)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugRuntime_board_stats_includes_actorHud()
    {
        var text = ReadInjector("DebugRuntime.cs");
        Assert.Contains("AddBoardStatsActorHud", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudObserve.AttachRow", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectorLoop_reconciles_dirty_hud_cache()
    {
        var text = ReadInjector(Path.Combine("Host", "InjectorLoop.cs"));
        Assert.Contains("ActorHudCache.ReconcileDirty", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GameCaptureHooks_marks_unique_plant_hud()
    {
        var text = ReadInjector("GameCaptureHooks.cs");
        Assert.Contains("ActorHudUniqueFlags.Mark", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudCache.MarkDirty", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectRuntime_shield_tick_marks_hud_dirty()
    {
        var text = ReadInjector(Path.Combine("Effects", "EffectRuntime.cs"));
        Assert.Contains("MarkDirtyFromOwnerKey", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudCache.Clear", text, StringComparison.Ordinal);
    }

    static string ReadInjector(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }
}
