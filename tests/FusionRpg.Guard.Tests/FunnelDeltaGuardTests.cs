using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-fusion/scripts/guard-funnel-delta.py so CI/local test runs
/// keep Secondary plugins off TakeDamage / SetHp / Bag.Grant.
/// </summary>
public class FunnelDeltaGuardTests
{
    [Fact]
    public void funnel_delta_guard_is_gated_by_its_owning_phase()
    {
        var repoRoot = FindRepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(repoRoot, "funnel-delta");
    }

    [Fact]
    public void Guard_script_exits_zero()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-funnel-delta.py");
        Assert.True(File.Exists(script), "missing " + script);

        var (exit, stdout, stderr) = RunScript(script, repoRoot);
        Assert.True(exit == 0,
            $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("FUNNEL DELTA GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_script_exits_nonzero_when_plugin_calls_TakeDamage()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-funnel-delta.py");
        var fixture = CreateFixture("td-");
        try
        {
            var pluginDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Effects", "Plugins");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(
                Path.Combine(pluginDir, "BadDamagePlugin.cs"),
                "namespace X { class Bad { void Hit() { z.TakeDamage(10); } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("FUNNEL DELTA GUARD FAILED", stderr, StringComparison.Ordinal);
            Assert.Contains("TakeDamage", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Guard_script_exits_nonzero_when_plugin_calls_Bag_Grant()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-funnel-delta.py");
        var fixture = CreateFixture("grant-");
        try
        {
            var pluginDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Effects", "Plugins");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(
                Path.Combine(pluginDir, "BadGrantPlugin.cs"),
                "namespace X { class Bad { void G(EffectPluginContext ctx) { ctx.Bag.Grant(dto); } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("FUNNEL DELTA GUARD FAILED", stderr, StringComparison.Ordinal);
            Assert.Contains("Bag.Grant", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Guard_script_exits_zero_when_injector_TakeDamage_is_not_a_plugin()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-funnel-delta.py");
        var fixture = CreateFixture("hot-");
        try
        {
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Injector", "GameHooks.cs"),
                "namespace FusionRpg.Injector { class GameHooks { void Hit() { z.TakeDamage(1); } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit == 0,
                $"expected OK exit, got {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("FUNNEL DELTA GUARD OK", stdout, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Guard_script_exits_nonzero_when_core_calls_AddPlantHp()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-funnel-delta.py");
        var fixture = CreateFixture("core-hp-");
        try
        {
            var coreDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Combat");
            Directory.CreateDirectory(coreDir);
            File.WriteAllText(
                Path.Combine(coreDir, "BadCoreWriter.cs"),
                "namespace X { class Bad { void Hit() { EntityStatWriter.AddPlantHp(p, -10, \"x\"); } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("FUNNEL DELTA GUARD FAILED", stderr, StringComparison.Ordinal);
            Assert.Contains("AddPlantHp", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void EffectFunnel_fa10_params_have_no_absolute_hp_keys()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "FusionRpg.Core", "Effects", "EffectFunnel.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);
        Assert.Contains("EffectActions.ApplyResourceDelta", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"setHp\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"absoluteHp\"]", text, StringComparison.Ordinal);
    }

    /// <summary>Create the three scan scopes so a partial fixture is still a valid root.</summary>
    /// <remarks>
    /// The ported guard fails CLOSED on a missing scope: pointed at a tree without
    /// gk-fusion/src/FusionRpg.Injector it refuses (exit 64) rather than reporting a clean tree, because
    /// the .ps1 used to wrap every scan in if (Test-Path ...) and pass - the silent-green shape
    /// this migration exists to remove. So a fixture that exercises one scope must still create
    /// the other two. Weakening the guard to keep these fixtures small would restore the defect.
    /// </remarks>
    static string CreateFixture(string name)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-funnel-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Core", "Effects", "Plugins"));
        Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Injector"));
        return fixture;
    }

    static (int Exit, string Stdout, string Stderr) RunScript(string script, string root)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
