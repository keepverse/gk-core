using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs scripts/guard-secondary-no-unity.ps1 so CI/local test runs
/// keep Secondary plugins off Unity / StatusExecutor / EntityStatWriter.
/// </summary>
public class SecondaryNoUnityGuardTests
{
    [Fact]
    public void secondary_no_unity_guard_is_gated_by_its_owning_phase()
    {
        var repoRoot = FindRepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(repoRoot, "secondary-no-unity");
    }

    [Fact]
    public void Guard_script_exits_zero()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-secondary-no-unity.py");
        Assert.True(File.Exists(script), "missing " + script);

        var (exit, stdout, stderr) = RunScript(script, repoRoot);
        Assert.True(exit == 0,
            $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("SECONDARY NO-UNITY GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_script_exits_nonzero_when_plugin_references_UnityEngine()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-secondary-no-unity.py");
        Assert.True(File.Exists(script), "missing " + script);

        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-sec-unity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pluginDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Effects", "Plugins");
            Directory.CreateDirectory(pluginDir);
            Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Core"));
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Core", "FusionRpg.Core.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
            File.WriteAllText(
                Path.Combine(pluginDir, "BadUnityPlugin.cs"),
                "using UnityEngine;\nnamespace X { class Bad { } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
                            // STDERR, not stdout: the port routes a failure verdict to stderr and an OK
                // verdict to stdout, so a caller can branch on the stream instead of scraping
                // text. The PowerShell original emitted this banner with `Write-Host`, which
                // writes the INFORMATION stream and is invisible to a `2>&1` capture - the trap
                // this migration exists to remove.
Assert.Contains("SECONDARY NO-UNITY GUARD FAILED", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Guard_script_exits_nonzero_when_implementer_outside_Plugins_references_UnityEngine()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-secondary-no-unity.py");
        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-sec-impl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Core", "Effects", "Plugins"));
            Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Server"));
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Core", "FusionRpg.Core.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Server", "BadGrantPlugin.cs"),
                "using UnityEngine;\nnamespace X { class BadGrantPlugin : IEffectGrantPlugin { } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("SECONDARY NO-UNITY GUARD FAILED", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Guard_script_exits_nonzero_when_Core_csproj_references_UnityEngine()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-secondary-no-unity.py");
        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-sec-csproj-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Core", "Effects", "Plugins"));
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Core", "FusionRpg.Core.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup><PackageReference Include=\"UnityEngine\" /></ItemGroup>\n</Project>\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("SECONDARY NO-UNITY GUARD FAILED", stderr, StringComparison.Ordinal);
            Assert.Contains("UnityEngine", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Guard_script_exits_zero_when_Injector_has_UnityEngine_without_plugin()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-secondary-no-unity.py");
        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-sec-hot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Core", "Effects", "Plugins"));
            Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Injector"));
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Core", "FusionRpg.Core.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Injector", "GameHooks.cs"),
                "using UnityEngine;\nnamespace FusionRpg.Injector { class GameHooks { } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit == 0,
                $"expected OK exit, got {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("SECONDARY NO-UNITY GUARD OK", stdout, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Core_csproj_has_no_UnityEngine()
    {
        var repoRoot = FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "FusionRpg.Core", "FusionRpg.Core.csproj");
        Assert.True(File.Exists(csproj), "missing " + csproj);
        var text = File.ReadAllText(csproj);
        Assert.DoesNotContain("UnityEngine", text, StringComparison.OrdinalIgnoreCase);
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
