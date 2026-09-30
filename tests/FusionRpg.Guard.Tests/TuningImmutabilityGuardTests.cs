using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/guard-tuning-immutability.py (solid-enforcement `tuning-immutability`,
/// spec-tuning-immutability.md) against a TEMPORARY git repository (checked cleanup) — never the
/// real repo's own gk-core/data/tuning/, which the guard would otherwise diff for real. T1: a modified
/// published version may change only `_meta`. T2: an added `<domain>.v&lt;n&gt;.json` (n &gt; 1)
/// needs its predecessor to exist. T3: a deleted published tuning file fails, unless a commit in the
/// checked range carries `tuning-immutability: correction &lt;reason&gt;` and names that exact path.
/// T4: an added file's domain must not match `gk-core/scripts/tuning-domain-denylist.v1.json`.
/// </summary>
public sealed class TuningImmutabilityGuardTests
{
    [Fact]
    public void No_tuning_changes_passes_clean()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":10}""");
            Commit(fixture, "seed");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected clean, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T1_a_meta_only_edit_passes()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{"note":"x"},"value":10}""");
            Commit(fixture, "seed");
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{"note":"y"},"value":10}""");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T1_a_value_edit_fails()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":10}""");
            Commit(fixture, "seed");
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":20}""");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected T1 to fail on a value edit");
            Assert.Contains("T1", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T2_adding_v3_without_v2_fails()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":1}""");
            Commit(fixture, "seed");
            WriteTuningFile(fixture, "d.v3.json", """{"schemaVersion":1,"version":3,"_meta":{},"value":3}""");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected T2 to fail on a non-contiguous version");
            Assert.Contains("T2", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T2_adding_v2_passes()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":1}""");
            Commit(fixture, "seed");
            WriteTuningFile(fixture, "d.v2.json", """{"schemaVersion":1,"version":2,"_meta":{},"value":2}""");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T3_deleting_a_published_version_fails()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":1}""");
            Commit(fixture, "seed");
            File.Delete(Path.Combine(fixture, "data", "tuning", "d.v1.json"));

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected T3 to fail on a deleted published version");
            Assert.Contains("T3", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T3_the_correction_marker_scoped_to_the_named_file_passes()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":1}""");
            Commit(fixture, "seed");
            File.Delete(Path.Combine(fixture, "data", "tuning", "d.v1.json"));
            Commit(fixture, "tuning-immutability: correction data/tuning/d.v1.json restored to its shipped values");

            // The deletion is now committed, so working-tree-vs-HEAD shows no diff at all — check the
            // actual correction commit against its own parent.
            var (exit, stdout, stderr) = RunGuard(fixture, baseRef: "HEAD~1");
            Assert.True(exit == 0, $"expected the marker to exempt this path, got exit={exit}\n{stdout}\n{stderr}");
            // The notice is on STDERR: it is a diagnostic about a PASS, and the verdict is on stdout.
            // An escape hatch that passes quietly is an escape hatch nobody reviews.
            Assert.Contains("correction marker used", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T3_a_correction_marker_naming_a_different_file_does_not_exempt_this_one()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "d.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":1}""");
            Commit(fixture, "seed");
            File.Delete(Path.Combine(fixture, "data", "tuning", "d.v1.json"));
            Commit(fixture, "tuning-immutability: correction data/tuning/other.v1.json restored");

            var (exit, _, stderr) = RunGuard(fixture, baseRef: "HEAD~1");
            Assert.True(exit != 0, "a marker naming a different file must not exempt this deletion");
            Assert.Contains("T3", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T4_adding_a_denylisted_domain_fails()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "loopfootest.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":1}""");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected T4 to fail on a denylisted domain shape");
            Assert.Contains("T4", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void T4_a_real_looking_domain_passes()
    {
        var fixture = NewGitFixture();
        try
        {
            WriteTuningFile(fixture, "aptitudes.v1.json", """{"schemaVersion":1,"version":1,"_meta":{},"value":1}""");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void registry_names_the_guard_and_an_owning_phase_reaches_it()
    {
        var registry = EnforcementRegistry.Load(FindRepoRoot());
        Assert.True(registry.Guards.ContainsKey("tuning-immutability"),
            "guard 'tuning-immutability' is not in scripts/enforcement-registry.v1.json");
        Assert.Equal("scripts/guard-tuning-immutability.py", registry.Guards["tuning-immutability"].Script);
    }

    // ---- fixture plumbing --------------------------------------------------------------------------

    static string NewGitFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-tuningimmutability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "tuning"));
        RunGit(dir, "init", "-q");
        RunGit(dir, "config", "user.email", "guard-test@example.invalid");
        RunGit(dir, "config", "user.name", "guard-test");
        return dir;
    }

    static void WriteTuningFile(string fixtureRoot, string fileName, string json) =>
        File.WriteAllText(Path.Combine(fixtureRoot, "data", "tuning", fileName), json);

    static void Commit(string fixtureRoot, string message)
    {
        RunGit(fixtureRoot, "add", "-A");
        RunGit(fixtureRoot, "commit", "-q", "-m", message);
    }

    static void RunGit(string fixtureRoot, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = fixtureRoot,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var (exit, stdout, stderr) = ExternalProcess.Run(psi, 30_000, "git command timed out");
        Assert.True(exit == 0, $"git {string.Join(' ', args)} failed: {stdout}\n{stderr}");
    }

    /// <summary>Runs guard-tuning-immutability.py FROM the real repo (so the denylist file is found)
    /// but pointed AT the fixture directory via --root, exactly like ClassSystemGuardTests' own
    /// pattern. The denylist default is deliberately relative to the SCRIPT and not to --root, so a
    /// fixture repository still finds the real one; a port that resolved it against --root would find
    /// nothing in every fixture and pass every T4 case vacuously.</summary>
    static (int Exit, string Stdout, string Stderr) RunGuard(string fixtureRoot, string? baseRef = null)
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-tuning-immutability.py");
        var args = $"\"{script}\" --root \"{fixtureRoot}\"";
        if (baseRef is not null) args += $" --base-ref \"{baseRef}\"";
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    // Unguarded, deliberately: docs/contributing/testing-standard.md's own rule ("a failed
    // temp-delete is a FAILURE, never catch { }") applies here even though every other Guard.Tests
    // fixture in this project uses the swallowed form (ClassSystemGuardTests.cs and siblings, all
    // pre-existing debt the test-substrate ratchet already grandfathers) -- a NEW file copying that
    // shape would be NEW debt the ratchet correctly refuses (guard-test-substrate.ps1: "new -- not
    // in baseline"), reproduced live while writing this test.
    //
    // The read-only-attribute clear below is NOT a swallowed failure -- it is real, deterministic
    // handling of a real Windows/git interaction, reproduced live: `git commit` on Windows leaves
    // some `.git/objects/**` blobs marked read-only, and a plain recursive delete throws
    // UnauthorizedAccessException on the first one. Clearing the attribute is the correct fix (the
    // same class .NET's own `Directory.Delete` docs name); the delete call itself still has no
    // try/catch, so any OTHER, genuine failure still fails the test.
    static void Cleanup(string fixture)
    {
        foreach (var f in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(f);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(fixture, recursive: true);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
