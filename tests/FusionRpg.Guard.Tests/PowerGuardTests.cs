using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/guard-power.py (spec-power-guard.md, T4.1; ported from guard-power.ps1) — G1 no literal curve, G2 no private
/// f(level), G3 no new curve vs inventory.json, G4 pin holds — and that its owning phase gates it.
///
/// <para><b>Where the output goes.</b> Findings and a FAILED verdict are on <b>stderr</b>; only the
/// OK verdict reaches stdout. Every finding assertion below therefore reads stderr, because on a
/// failing run stdout is empty and an assertion left reading it passes without the finding ever
/// having been produced.</para>
/// </summary>
[Trait("VerificationId", "guard.power")]
public class PowerGuardTests
{
    [Fact]
    public void power_guard_is_gated_by_its_owning_phase()
    {
        var repoRoot = FindRepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(repoRoot, "power");
    }

    [Fact]
    public void PowerGuard_script_exits_zero_on_the_real_tree()
    {
        var (exit, stdout, stderr) = Run(FindRepoRoot(), FindRepoRoot());
        Assert.True(exit == 0, $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("POWER GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryJson_exists_and_parses()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "docs", "architecture", "power", "inventory.json");
        Assert.True(File.Exists(path), "missing " + path);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(doc.RootElement.GetProperty("scales").GetArrayLength() > 0);
    }

    [Fact]
    public void G1_fails_on_a_literal_curve_field_outside_the_loader()
    {
        var fixture = NewFixture();
        try
        {
            var powerDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Power");
            Directory.CreateDirectory(powerDir);
            File.WriteAllText(Path.Combine(powerDir, "Sneaky.cs"),
                "namespace X { class Sneaky { long PinValue = 680; } }\n");
            SeedInventory(fixture);

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G1 to fail");
            Assert.Contains("G1 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G2_fails_on_a_private_f_level_method_outside_Core_Power()
    {
        var fixture = NewFixture();
        try
        {
            var battleDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Battle");
            Directory.CreateDirectory(battleDir);
            File.WriteAllText(Path.Combine(battleDir, "Sneaky.cs"), SneakyLevelMethodCs);
            SeedInventory(fixture);

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G2 to fail");
            Assert.Contains("G2 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G2_allowlisted_file_passes()
    {
        var fixture = NewFixture();
        try
        {
            var battleDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Battle");
            Directory.CreateDirectory(battleDir);
            File.WriteAllText(Path.Combine(battleDir, "Sneaky.cs"), SneakyLevelMethodCs);
            SeedInventory(fixture, extraLocation: "src/FusionRpg.Core/Battle/Sneaky.cs");

            // -File mode passes script args as PLAIN STRINGS, not re-parsed PowerShell syntax -- a
            // literal "@('x')" array-literal token is never evaluated as an array here, only bound as
            // one opaque string. A bare scalar coerces into a [string[]] parameter's one-element array
            // via PowerShell's own normal binding, which is what actually works through -File.
            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot(), "--g2-allowlist-file Sneaky.cs");
            Assert.True(exit == 0, $"expected pass with allowlist, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G3_fails_when_a_power_shaped_method_is_not_in_inventory_json()
    {
        var fixture = NewFixture();
        try
        {
            var battleDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Battle");
            Directory.CreateDirectory(battleDir);
            File.WriteAllText(Path.Combine(battleDir, "Sneaky.cs"), SneakyLevelMethodCs);
            SeedInventory(fixture); // deliberately does NOT list Sneaky.cs

            var (exit, _, stderr) = Run(fixture, FindRepoRoot(), "--g2-allowlist-file Sneaky.cs");
            Assert.True(exit != 0, "expected G3 to fail even with the G2 allowlist covering it");
            Assert.Contains("G3 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G4_fails_when_a_tuning_versions_pin_is_broken()
    {
        var fixture = NewFixture();
        try
        {
            SeedInventory(fixture);
            var tuningDir = Path.Combine(fixture, "data", "tuning");
            Directory.CreateDirectory(tuningDir);
            // bMilli=1 does not divide this pin exactly at pinIndex=20 (599,810 / 20 has a remainder)
            // -- the guard's own belt-and-braces re-derivation must catch this, not just echo whatever
            // pinValue the file claims (an EARLIER draft of this fixture picked self-consistent
            // numbers by accident, which is always internally "correct" no matter what pinValue says
            // -- this one is deliberately NOT self-consistent).
            File.WriteAllText(Path.Combine(tuningDir, "power-scale.v9.json"),
                """{"curve":{"cMilli":80000,"bMilli":1,"pinIndex":20,"pinValue":680}}""");

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G4 to fail on a broken pin");
            Assert.Contains("G4 ", stderr, StringComparison.Ordinal);
            Assert.Contains("power-scale.v9.json", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G4_passes_on_the_real_shipped_pin()
    {
        // The real gk-core/data/tuning/power-scale.v1.json, checked via the fixture harness rather than only
        // implicitly through the whole-tree pass above -- this isolates G4 specifically.
        var fixture = NewFixture();
        try
        {
            SeedInventory(fixture);
            var tuningDir = Path.Combine(fixture, "data", "tuning");
            Directory.CreateDirectory(tuningDir);
            var real = Path.Combine(FindRepoRoot(), "data", "tuning", "power-scale.v1.json");
            File.Copy(real, Path.Combine(tuningDir, "power-scale.v1.json"));

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"G4 should pass on the real pin, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    // ---- fixture plumbing --------------------------------------------------------------------------

    /// <summary>The method signature must start its own line — the guard's regex anchors on
    /// line-start (multiline `^`), matching real C# formatting conventions, so a fixture packed onto
    /// one line (an earlier draft of this file did exactly that) never reaches the check at all.</summary>
    const string SneakyLevelMethodCs = """
        namespace X
        {
            class Sneaky
            {
                public static int Foo(int level) => 5 + 3 * level;
            }
        }

        """;

    static string NewFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-powerguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "FusionRpg.Core", "Power"));
        return dir;
    }

    /// <summary>A minimal inventory.json so the fixture doesn't fail on "missing inventory.json"
    /// before it even reaches the check under test. `extraLocation`, when given, is folded in so a
    /// test can prove G3 passes for a specific planted file without needing the real 20-row file.</summary>
    static void SeedInventory(string fixtureRoot, string? extraLocation = null)
    {
        var dir = Path.Combine(fixtureRoot, "docs", "architecture", "power");
        Directory.CreateDirectory(dir);
        var extra = extraLocation is null ? "" : $$"""
            ,{"id":99,"scale":"test","shape":"test","location":"{{extraLocation}}","verdict":"test"}
            """;
        File.WriteAllText(Path.Combine(dir, "inventory.json"),
            $$"""{"schemaVersion":1,"scales":[{"id":1,"scale":"placeholder","shape":"none","location":"none","verdict":"none"}{{extra}}]}""");
    }

    /// <summary>Runs the guard FROM the real repo (so the interpreter is found) but pointed AT the
    /// fixture directory via --root, exactly like DalGuardTests' own pattern.</summary>
    static (int Exit, string Stdout, string Stderr) Run(string fixtureRoot, string repoRoot, string extraArgs = "")
    {
        var script = Path.Combine(repoRoot, "scripts", "guard-power.py");
        var psi = new ProcessStartInfo
        {
            // 60s, not 1800s: the port is not slower than the original, and the timeout exists to
            // catch a wedged child rather than to accommodate a slow one.
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{fixtureRoot}\" {extraArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    static void Cleanup(string fixture)
    {
        try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var script = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(script)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root with Directory.Build.props");
    }
}
