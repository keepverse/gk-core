using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/gate_class_system_phase9.py (class-system-todo.md P9.0) — the mechanical readiness
/// gate for Phase 9 ("tune on real data"). Nobody decides "we are ready" by eye: the gate wraps
/// gk-core/scripts/audit-reader-census.py (P8.4) and reports READY only when the census itself says every
/// aptitude-fed family has a reader and _meta.measurable's own prose still agrees with a fresh run.
///
/// <para>audit-reader-census.py has no -Root override (its paths are hardcoded to the real repo tree,
/// by design — see its own module boundary, spec-residual-fit.md §5 "ships no src/ code"). Rather than
/// extending that already-shipped, already-tested script's scope to support a synthetic fixture tree,
/// the gate itself exposes a narrower test seam (--census-json) that swaps in a canned census report
/// shaped exactly like audit-reader-census.py's own --json output. This proves the gate's own readiness
/// arithmetic in both directions without touching the census script or needing a fully-built game.</para>
/// </summary>
public class ClassSystemPhase9ReadinessGateTests
{
    [Fact]
    public void Gate_exitsOneOnTheRealTree_todayNotReady()
    {
        // class-system-plan.md §0.1 / Phase 9 header: real data cannot exist until every mechanism
        // (actions, passives, skills, items) does, so this MUST report NOT READY today — that is this
        // task's own explicit acceptance line, not a defect this test should ever expect to go green
        // before the rest of the game is built.
        var (exit, stdout, stderr) = Run(FindRepoRoot(), censusJsonPath: null);
        Assert.True(exit == 1, $"expected exit=1 (not ready today), got exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("PHASE 9 READINESS GATE: NOT READY", stdout, StringComparison.Ordinal);
        Assert.Contains("aptitude-fed families still have no reader", stdout, StringComparison.Ordinal);
        // Matches the live _meta.measurable roster (P8.4) — if this list ever shrinks to empty, the
        // gate is supposed to flip to READY, not silently keep failing.
        Assert.Contains("resource.efficiency", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_exitsZero_whenCensusReportsZeroReaderLessFamilies()
    {
        var fixtureJson = WriteCensusFixture(new
        {
            families_total = 48,
            families_with_reader = 48,
            families_without_reader = 0,
            edges_total = 486,
            edges_unmapped = Array.Empty<string>(),
            edges_reserved = 0,
            edges_reserved_pct = 0.0,
            reader_less_families = Array.Empty<string>()
        });
        try
        {
            var (exit, stdout, stderr) = Run(FindRepoRoot(), fixtureJson);
            Assert.True(exit == 0, $"expected exit=0 (fully ready), got exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("PHASE 9 READINESS GATE: READY", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(fixtureJson);
        }
    }

    [Fact]
    public void Gate_exitsOne_whenCensusReportsReaderLessFamilies_andNamesThemInTheReport()
    {
        var fixtureJson = WriteCensusFixture(new
        {
            families_total = 10,
            families_with_reader = 8,
            families_without_reader = 2,
            edges_total = 100,
            edges_unmapped = Array.Empty<string>(),
            edges_reserved = 7,
            edges_reserved_pct = 7.0,
            reader_less_families = new[] { "planted.familyOne", "planted.familyTwo" }
        });
        try
        {
            var (exit, stdout, stderr) = Run(FindRepoRoot(), fixtureJson);
            Assert.True(exit == 1, $"expected exit=1 (planted gap), got exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("PHASE 9 READINESS GATE: NOT READY", stdout, StringComparison.Ordinal);
            Assert.Contains("planted.familyOne", stdout, StringComparison.Ordinal);
            Assert.Contains("planted.familyTwo", stdout, StringComparison.Ordinal);
            Assert.Contains("2 of 10", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(fixtureJson);
        }
    }

    [Fact]
    public void Gate_notWiredIntoTheDeploy_becauseNotReadyIsExpectedForALongTime()
    {
        // Deliberate: unlike guard-class-system.ps1 (a code-invariant guard, gated by CI and the
        // implement phase), this gate is EXPECTED to report NOT READY until the whole game is built
        // (class-system-plan.md §0.1). Wiring it into any "throw on failure" pipeline would break the
        // run today for an honestly-expected state, not a regression — so it must not be wired there.
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "scripts", "deploy-play.py");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);
        // Both spellings, because the retired form was hyphenated and the port is underscored. A
        // check for one leaves the other free to be wired in.
        Assert.DoesNotContain("gate-class-system-phase9", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gate_class_system_phase9", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Owner ruling 2026-09-26: a deploy runs no guard SUITE. The 28-guard `-Tier local
    /// -IncludeBacklog` sweep was 203s of verification paid before every deploy (28.3s of measured
    /// stages, 2026-09-26), and it was green every time it ran — so it gated nothing a deploy could
    /// break. Verification lives in
    /// the implement phase (`verify-change.py`) and CI/nightly (`run_guards.py --tier ci`).
    /// </summary>
    [Fact]
    public void Deploy_runs_the_positioned_precondition_and_no_guard_suite()
    {
        GuardWiring.AssertDeployRunsNoGuardSuite(FindRepoRoot());
    }

    static string WriteCensusFixture(object payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phase9-census-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload));
        return path;
    }

    /// <summary>Runs gate_class_system_phase9.py FROM the real repo.
    /// When censusJsonPath is null, exercises the real python-backed path against the live tree;
    /// otherwise swaps in the fixture via --census-json. Mirrors ClassSystemGuardTests' own Run().</summary>
    static (int Exit, string Stdout, string Stderr) Run(string repoRoot, string? censusJsonPath)
    {
        var script = Path.Combine(repoRoot, "scripts", "gate_class_system_phase9.py");
        Assert.True(File.Exists(script), "missing " + script);
        var arguments = $"\"{script}\"";
        if (censusJsonPath is not null) arguments += $" --census-json \"{censusJsonPath}\"";
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "gate script timed out");
    }

    static void Cleanup(string fixturePath)
    {
        // solid-enforcement tuning-immutability SE2.3: a failed temp-delete is a failure, never a
        // swallowed catch -- fixturePath is this test's own Path.GetTempPath() scratch file.
        File.Delete(fixturePath);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
