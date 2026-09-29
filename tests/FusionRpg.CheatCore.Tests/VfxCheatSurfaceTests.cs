using FusionRpg.CheatCore;
using Xunit;

namespace FusionRpg.CheatCore.Tests;

/// <summary>VFX SSOT cheat surface: SYS-ELEMENT-FX toggle (§16.5) and fx.* scenario steps (§11).</summary>
public class VfxCheatSurfaceTests
{
    [Fact]
    public void Element_fx_toggle_defaults_on_in_schema_and_registry()
    {
        Assert.True(CheatSchema.EffectiveToggle("SYS-ELEMENT-FX", false, false));
        var r = new CheatRegistry();
        r.EnsureDefaults();
        Assert.True(r.On("SYS-ELEMENT-FX"));
        // master stays independent and on
        Assert.True(r.On("SYS-DAMAGE-FX"));
    }

    [Fact]
    public void Fx_debug_steps_are_allowed_and_world_flash_survives()
    {
        Assert.Contains("debug.fx.play", DebugScenarios.AllowedStepNames);
        Assert.Contains("debug.fx.list", DebugScenarios.AllowedStepNames);
        Assert.Contains("debug.fx.mute", DebugScenarios.AllowedStepNames);
        Assert.Contains("debug.fx.unmute", DebugScenarios.AllowedStepNames);
        // legacy step name is test-pinned and survives the event retirement (vfx-ssot.md §11)
        Assert.Contains("debug.fx.world-flash", DebugScenarios.AllowedStepNames);
    }
}
