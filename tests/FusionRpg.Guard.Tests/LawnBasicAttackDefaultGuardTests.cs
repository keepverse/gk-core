using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The basic-attack feature's default is a recorded owner decision, not a reading — so it is pinned,
/// and flipping it is a new decision that needs its own perf evidence.
///
/// <para><b>It was default OFF</b> from 2026-09-15 (lawn-combat-wire L-N1) after a measured 300-zombie
/// frame-budget breach: the RPG capture pipeline took 28.06% of wall against a proposed ≤6% ceiling,
/// at 19.8 fps. <b>It is default ON</b> from 2026-09-16, because that ruling named its own release
/// condition ("until a measured perf pass clears it") and the pass ran: after the derived-fold, HUD
/// cache and HUD rescan fixes, <c>effect.onCapture</c> is 3.46% of wall at 59.7 fps with the feature
/// on, against 0.00% at 59.4 fps with it off. Files:
/// <c>_baseline-lcw-300z-{env-on-a,hud-resync-b60,featoff-b60}.json</c>.</para>
///
/// <para>The env var is read at process start and is the only sanctioned way a live proof pins the
/// feature either way — a mid-match toggle leaves bound grants live (L-N8). Source scan, because
/// <c>LawnBasicAttackFeatureFlagTests</c> lives in Injector.Tests, which CI does not build.</para>
/// </summary>
public class LawnBasicAttackDefaultGuardTests
{
    static string Feature() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "Effects", "LawnBasicAttackFeature.cs"));

    [Fact]
    public void The_feature_default_is_on()
    {
        var text = Feature();
        Assert.Contains("public const bool DefaultEnabled = true;", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default is only half the contract. The other half is that it is this module's OWN default:
    /// the 2026-09-14 investigation found the flag borrowing <c>CheatSchema.EffectiveToggle</c>'s
    /// default-true fallback as its only source of "on by default", which is a boundary defect — a
    /// production switch cannot take its default from a debug/QA toggle store whose contract never
    /// promised one. Now that the default is true again, that is the exact defect this could quietly
    /// regress into, so pin that <c>CheatState</c> is consulted only when a user actually set it.
    /// </summary>
    public class DefaultComesFromThisModuleNotCheatState
    {
        [Fact]
        public void DebugOverride_is_null_unless_the_toggle_was_explicitly_set()
        {
            var text = Feature();
            Assert.Contains(
                "static bool? DebugOverride => CheatState.IsUserSet(CheatToggleId) ? CheatState.On(CheatToggleId) : null;",
                text,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Env_zero_forces_off_env_one_forces_on_and_only_then_a_debug_override_applies()
    {
        var text = Feature();
        Assert.Contains("static readonly bool EnvForcedOff = string.Equals(EnvValue, \"0\", StringComparison.Ordinal);", text, StringComparison.Ordinal);
        Assert.Contains("static readonly bool EnvForcedOn = string.Equals(EnvValue, \"1\", StringComparison.Ordinal);", text, StringComparison.Ordinal);
        Assert.Contains("public static bool Enabled => !EnvForcedOff && (EnvForcedOn || (DebugOverride ?? DefaultEnabled));", text, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }
}
