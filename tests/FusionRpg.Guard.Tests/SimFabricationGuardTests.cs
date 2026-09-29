using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs <c>gk-core/scripts/guard-sim-fabrication.py</c> (row RS4, owner ruling D4 (b)) against the real tree
/// and against planted violations. A rule never seen to fail is not known to work, so every rule the
/// guard claims gets a deliberately broken input and the same message is expected back.
///
/// <para><b>FINDINGS ARE READ FROM <c>stdout + stderr</c> THROUGHOUT THIS FILE</b>, and that is
/// the point rather than a convenience. The guard reports its findings on STDERR - the port
/// standard, so a caller reading stdout alone gets the reading line and the verdict and nothing
/// that could be mistaken for a finding - while the PowerShell original printed everything
/// through <c>Write-Host</c>, which is stdout. The five planted-violation assertions moved from
/// <c>stdout</c> to <c>stdout + stderr</c> when the port landed, and went red saying so with an
/// empty string where the finding should have been. The OK line stays on <c>stdout</c> alone: it is
/// the report, and a report that had to be read from stderr would be a finding.</para>
/// <para><b>Half A</b> is the scenario corpus: a verdict must read back through the same query path
/// the web FE uses, so a read whose route is <c>/api/test/snapshot</c> — or any <c>/api/test/*</c>
/// or <c>/api/sim/*</c> route — is a fabricated read-back and must be refused by name.
/// <b>Half B</b> is the shim surface: no <c>/api/sim/*</c> handler may take <c>RpgStore</c>, and
/// every <c>/api/test/*</c> handler that does must be on the guard's allowlist with a written reason;
/// a stale entry is itself a violation.</para>
/// </summary>
[Trait("VerificationId", "guard.sim-fabrication")]
public sealed class SimFabricationGuardTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root with Directory.Build.props");
    }

    static (int Exit, string Stdout, string Stderr) RunGuard(
        string root, string? scenarioDir = null, string? serverDir = null)
    {
        // The tool always lives in the REAL repo; only the trees it scans are redirected, which is what

        // lets a planted violation stay out of the real corpus and the real shim surface. `root` is

        // passed explicitly because the guard builds each reported path relative to it.

        var script = Path.Combine(RepoRoot(), "scripts", "guard-sim-fabrication.py");
        var args = $"\"{script}\" --root \"{root}\"";
        if (scenarioDir is not null) args += $" --scenario-dir \"{scenarioDir}\"";
        if (serverDir is not null) args += $" --server-dir \"{serverDir}\"";
        var psi = new ProcessStartInfo { FileName = "python", Arguments = args, CreateNoWindow = true };
        return ExternalProcess.Run(psi, 120_000, "sim-fabrication guard timed out");
    }

    /// <summary>A throwaway directory that is always removed — a failed delete throws, never swallowed
    /// (docs/contributing/testing-standard.md).</summary>
    static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SimFabricationGuard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_real_corpus_and_the_real_server_are_green()
    {
        var (exit, stdout, stderr) = RunGuard(RepoRoot());
        Assert.True(exit == 0, $"guard is red on the real tree:\n{stdout}\n{stderr}");
        Assert.Contains("SIM FABRICATION GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fabricated_read_back_is_refused_by_name()
    {
        var root = RepoRoot();
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "planted-fabrication.json"), """
                {
                  "id": "planted-fabrication",
                  "title": "planted",
                  "description": "A scenario that asserts on state it created through a fixture read-back.",
                  "seed": 1,
                  "clock": { "mode": "ambient", "note": "planted" },
                  "steps": [
                    { "op": "test.expedition.due", "route": "POST /api/test/expedition-due", "why": "planted" },
                    { "op": "read.snapshot", "route": "GET /api/test/snapshot", "why": "the fabricated read-back" },
                    { "op": "expect.snapshot.held", "reading": "read.snapshot", "path": "$.runs",
                      "check": "notEmpty", "why": "planted" },
                    { "op": "digest", "include": ["read.never-ran#$.runs"], "why": "planted" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunGuard(root, scenarioDir: dir);
            Assert.True(exit != 0, "the guard passed a fabricated read-back");
            Assert.Contains("/api/test/snapshot", stdout + stderr, StringComparison.Ordinal);
            Assert.Contains("read.never-ran", stdout + stderr, StringComparison.Ordinal);
            Assert.Contains("not named in the scenario's own notes", stdout + stderr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_planted_sim_handler_that_takes_the_store_is_refused()
    {
        var root = RepoRoot();
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "PlantedSim.cs"), """
                public static class PlantedSim
                {
                    public static void Map(WebApplication app)
                    {
                        var g = app.MapGroup("/api/sim");
                        g.MapPost("/evil", (RpgStore store) => Results.Ok(store.GetStats()));
                    }
                }
                """);

            var (exit, stdout, stderr) = RunGuard(root, serverDir: dir);
            Assert.True(exit != 0, "the guard passed a /api/sim handler that takes RpgStore");
            Assert.Contains("/api/sim/evil", stdout + stderr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_stale_test_store_allowlist_entry_is_refused()
    {
        var root = RepoRoot();
        var dir = NewTempDir();
        try
        {
            // Only /api/test/reset exists here, so the other eleven allowlist entries are stale.
            File.WriteAllText(Path.Combine(dir, "PlantedTest.cs"), """
                public static class PlantedTest
                {
                    public static void Map(WebApplication app)
                    {
                        var test = app.MapGroup("/api/test");
                        test.MapPost("/reset", (RpgStore store) => Results.Ok());
                    }
                }
                """);

            var (exit, stdout, stderr) = RunGuard(root, serverDir: dir);
            Assert.True(exit != 0, "the guard passed a stale allowlist");
            Assert.Contains("stale allowlist entry '/api/test/snapshot'", stdout + stderr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
