using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/guard-stat-pairs.py (spec-stat-taxonomy.md §6.2, T0.3) — P1 every Contest paired,
/// P2 every pair symmetric, P3 no Race paired, P4 no capped Contest magnitude — and that its owning phase
/// gates it. Mirrors PowerGuardTests' fixture shape.
/// </summary>
public class StatTaxonomyGuardTests
{
    [Fact]
    public void stat_pairs_guard_is_gated_by_its_owning_phase()
    {
        var repoRoot = FindRepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(repoRoot, "stat-pairs");
    }

    [Fact]
    public void StatPairsGuard_script_exits_zero_on_the_real_tree()
    {
        var (exit, stdout, stderr) = Run(FindRepoRoot(), FindRepoRoot());
        Assert.True(exit == 0, $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("STAT-PAIRS GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void P1_fails_on_a_Contest_family_with_no_counterpart()
    {
        var fixture = NewFixture();
        try
        {
            WriteCatalog(fixture, """
                {"entries":[
                  {"family":"combat.lonely","statClass":"Contest","cap":null,"unitClass":"GameUnits"}
                ]}
                """);

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected P1 to fail");
                            // STDERR, not stdout: the port routes findings to stderr and a clean verdict to
                // stdout. The PowerShell original emitted both with `Write-Host`, which writes
                // the INFORMATION stream and is invisible to a `2>&1` capture - the trap this
                // migration exists to remove.
Assert.Contains("P1 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void P2_fails_on_an_asymmetric_pair()
    {
        var fixture = NewFixture();
        try
        {
            WriteCatalog(fixture, """
                {"entries":[
                  {"family":"combat.a","statClass":"Contest","counterpart":"combat.b","unitClass":"GameUnits"},
                  {"family":"combat.b","statClass":"Contest","counterpart":"combat.c","unitClass":"GameUnits"},
                  {"family":"combat.c","statClass":"Contest","counterpart":"combat.b","unitClass":"GameUnits"}
                ]}
                """);

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected P2 to fail");
            Assert.Contains("P2 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void P3_fails_on_a_Race_family_with_a_counterpart()
    {
        var fixture = NewFixture();
        try
        {
            WriteCatalog(fixture, """
                {"entries":[
                  {"family":"turn.speed","statClass":"Race","counterpart":"turn.slowness","unitClass":"GameUnits"}
                ]}
                """);

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected P3 to fail");
            Assert.Contains("P3 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void P4_fails_on_a_capped_Contest_magnitude()
    {
        var fixture = NewFixture();
        try
        {
            WriteCatalog(fixture, """
                {"entries":[
                  {"family":"combat.foo","statClass":"Contest","counterpart":"combat.bar","cap":0.5,"unitClass":"GameUnits"},
                  {"family":"combat.bar","statClass":"Contest","counterpart":"combat.foo","cap":null,"unitClass":"GameUnits"}
                ]}
                """);

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected P4 to fail");
            Assert.Contains("P4 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void P4_does_not_fire_on_a_capped_StatusPotencyPoints_Contest_half()
    {
        // status.resist's shipped 0.95 cap is StatusPotencyPoints-shaped, not GameUnits — P4 must stay
        // scoped to the two true magnitude unit classes or this shipped, deliberate cap would trip it.
        var fixture = NewFixture();
        try
        {
            WriteCatalog(fixture, """
                {"entries":[
                  {"family":"status.power","statClass":"Contest","counterpart":"status.resist","cap":null,"unitClass":"StatusPotencyPoints"},
                  {"family":"status.resist","statClass":"Contest","counterpart":"status.power","cap":0.95,"unitClass":"StatusPotencyPoints"}
                ]}
                """);

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void HealPowerReclassifiedAsContestFailsTheGuard()
    {
        // spec-healing-pair.md §6 (HealIsPoolNotContest, T4.5): the classification is load-bearing,
        // not incidental. Mutates the REAL shipped catalog's resource.restore entry to Contest (no
        // counterpart) and proves the guard actually catches it -- not just that the generic P1
        // mechanism works on an arbitrary synthetic family name, which the tests above already show.
        var fixture = NewFixture();
        try
        {
            var realCatalogPath = Path.Combine(FindRepoRoot(), "data", "seed", "derived-stats", "catalog.json");
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(realCatalogPath))!.AsObject();
            var entries = root["entries"]!.AsArray();

            System.Text.Json.Nodes.JsonNode? healPower = null;
            foreach (var e in entries)
            {
                if ((string?)e!["family"] == "resource.restore") { healPower = e; break; }
            }
            Assert.True(healPower is not null, "resource.restore entry not found in the real catalog");
            healPower!["statClass"] = "Contest";

            WriteCatalog(fixture, root.ToJsonString());

            var (exit, _, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected the guard to fail on a Contest-reclassified heal.power");
            Assert.Contains("P1 resource.restore", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    // ---- fixture plumbing --------------------------------------------------------------------------

    static string NewFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-statpairsguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "seed", "derived-stats"));
        return dir;
    }

    static void WriteCatalog(string fixtureRoot, string json)
    {
        var path = Path.Combine(fixtureRoot, "data", "seed", "derived-stats", "catalog.json");
        File.WriteAllText(path, json);
    }

    /// <summary>Runs guard-stat-pairs.py FROM the real repo (so the interpreter is found) but
    /// pointed AT the fixture directory via -Root, exactly like PowerGuardTests' own pattern.</summary>
    static (int Exit, string Stdout, string Stderr) Run(string fixtureRoot, string repoRoot)
    {
        var script = Path.Combine(repoRoot, "scripts", "guard-stat-pairs.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{fixtureRoot}\"",
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
