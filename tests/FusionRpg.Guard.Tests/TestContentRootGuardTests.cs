using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs <c>gk-core/scripts/guard-test-content-root.py</c> and proves it both passes on the current tree and
/// FAILS on a planted violation. Row: <c>tasks/keepverse-split-todo.md</c> KS-F2 (<c>= CS-F1-b</c>),
/// the guard half of the <c>CS-F1</c> fixture defect — a private <c>..\..\..</c> walk from a test's
/// own source directory that resolved above the repo root, passed in a lane worktree and failed in
/// the main checkout. Contract: <c>tasks/keepverse-split-plan.md</c> "Resolver contract";
/// <c>gk-core/tests/Shared/KeepverseRoots.cs</c>.
///
/// <para><b>The planted probe is assembled at run time, on purpose.</b> This file lives under
/// <c>tests/**</c>, so the guard scans it too — a literal <c>"..", "..", ".."</c> written into the
/// fixture below would be this guard's own planted violation on the real tree, and the
/// "passes on the current tree" case would fail for the wrong reason. <see cref="Walk"/> therefore
/// builds the walk literal from a quote and the two dots, and the planted text is written to a
/// throwaway tree via <c>--root</c> — the same discipline <c>TestSubstrateGuardTests</c> records for
/// its own probe (planting in the real tree reddens every parallel run and leaves the probe behind).</para>
///
/// <para><b>FINDINGS ARE READ FROM <c>stdout + stderr</c> THROUGHOUT THIS FILE</b>, and that is the
/// point rather than a convenience. The guard reports its findings on STDERR — the port standard, so a
/// caller reading stdout alone gets the verdict and the readings and nothing that could be mistaken for
/// them — while the PowerShell original printed everything through <c>Write-Host</c>, which is
/// stdout. Every assertion below moved from <c>stdout</c> to <c>stdout + stderr</c> the moment the
/// port landed, and went red saying so with an empty string where the finding should have been.</para>
///
/// <para>The interpreter is selected the same way <c>TestSubstrateGuardTests</c> does it: <c>python</c>
/// by name, with no PowerShell host probing left. The host-selection helper this file used to carry
/// existed only to choose between <c>pwsh</c> and <c>powershell</c> and is gone with the shell it
/// chose.</para>
/// </summary>
[Trait("VerificationId", "guard.test-content-root")]
public class TestContentRootGuardTests
{
    const string Quote = "\"";

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root with Directory.Build.props");
    }

    static (int exit, string stdout, string stderr) RunGuard(string root)
    {
        // The tool always lives in the REAL repo; only the tree it scans is redirected, which is what
        // lets a planted violation stay out of the real tests/ tree.
        var script = Path.Combine(RepoRoot(), "scripts", "guard-test-content-root.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 120_000, "test-content-root guard timed out");
    }

    /// <summary>
    /// A throwaway tree shaped like the real one: <c>gk-core/tests/FusionRpg.Data.Tests/</c> sits two
    /// segments below the root and <c>gk-core/tests/FusionRpg.Core.Tests/Actions/</c> three — the two depths
    /// that decide whether a walk escapes or misses.
    /// </summary>
    sealed class ProbeTree : IDisposable
    {
        public string Root { get; }

        public ProbeTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "content-root-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "tests", "FusionRpg.Data.Tests"));
            Directory.CreateDirectory(Path.Combine(Root, "tests", "FusionRpg.Core.Tests", "Actions"));
        }

        public void Plant(string relativePath, string content) =>
            File.WriteAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);

        public void Dispose()
        {
            // A failed temp-delete is a failure, never a swallowed catch — the standard
            // guard-test-substrate.py enforces, applied to that guard's sibling.
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    static string Walk(int levels) =>
        string.Join(", ", Enumerable.Repeat(Quote + ".." + Quote, levels));

    /// <summary>One `Path.GetFullPath(Path.Combine(&lt;dir&gt;, &lt;walk&gt;))`, anchored at `[CallerFilePath]`.</summary>
    static string AnchoredWalkSource(string localName, int levels, string? keepverseRoot)
    {
        var tail = keepverseRoot is null ? "" : ", " + Quote + keepverseRoot + Quote;
        return $$"""
            using System.IO;
            using System.Runtime.CompilerServices;

            namespace FusionRpg.Probe;

            public static class Planted
            {
                public static string Resolve([CallerFilePath] string here = "")
                {
                    var {{localName}} = System.IO.Path.GetDirectoryName(here)!;
                    return System.IO.Path.GetFullPath(System.IO.Path.Combine({{localName}}, {{Walk(levels)}}{{tail}}));
                }
            }
            """;
    }

    /// <summary>The chained shape: a walk local, then the Keepverse root named off it.</summary>
    static string ChainedWalkSource(string localName, int levels, string keepverseRoot)
    {
        return $$"""
            using System.IO;
            using System.Runtime.CompilerServices;

            namespace FusionRpg.Probe;

            public static class Planted
            {
                public static string Resolve([CallerFilePath] string here = "")
                {
                    var {{localName}} = System.IO.Path.GetDirectoryName(here)!;
                    var repo = System.IO.Path.GetFullPath(System.IO.Path.Combine({{localName}}, {{Walk(levels)}}));
                    return System.IO.Path.Combine(repo, {{Quote}}{{keepverseRoot}}{{Quote}}, "x.json");
                }
            }
            """;
    }

    [Fact]
    public void Guard_exits_zero_on_the_current_tree()
    {
        var (exit, stdout, stderr) = RunGuard(RepoRoot());
        Assert.True(exit == 0, $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("TEST CONTENT-ROOT GUARD OK", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_fails_on_a_planted_walk_that_escapes_the_repo_root()
    {
        // The CS-F1 shape exactly: a file directly in gk-core/tests/FusionRpg.Data.Tests (two segments below
        // the root) walking three levels up, which lands one level ABOVE the repository root.
        using var tree = new ProbeTree();
        tree.Plant("tests/FusionRpg.Data.Tests/SpeciesModLedgerTests.cs", AnchoredWalkSource("testsDir", 3, null));

        var (exit, stdout, stderr) = RunGuard(tree.Root);

        Assert.True(exit != 0, $"expected a failing exit, got 0\nstdout:\n{stdout}");
        Assert.Contains("TEST CONTENT-ROOT GUARD FAILED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("walk-escapes-root", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_fails_on_a_planted_walk_that_misses_the_repo_root()
    {
        // The quieter half of the same defect: a depth-3 file walking two levels up lands on
        // `tests/`, so `tests/data` is named instead of `data`. The path is wrong by construction
        // even though it never leaves the repository.
        using var tree = new ProbeTree();
        tree.Plant("tests/FusionRpg.Core.Tests/Actions/ShallowWalkTests.cs", ChainedWalkSource("testsDir", 2, "data"));

        var (exit, stdout, stderr) = RunGuard(tree.Root);

        Assert.True(exit != 0, $"expected a failing exit, got 0\nstdout:\n{stdout}");
        Assert.Contains("TEST CONTENT-ROOT GUARD FAILED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("walk-misses-root", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_passes_a_walk_that_lands_on_the_repo_root()
    {
        // The rule is stated over the RESOLUTION, not over the presence of `..`: the same depth-3
        // directory with the correct number of levels passes untouched. Without this case the rule
        // above would be indistinguishable from a blanket ban on `..` in tests/.
        using var tree = new ProbeTree();
        tree.Plant("tests/FusionRpg.Core.Tests/Actions/RungTableTests.cs", ChainedWalkSource("testsDir", 3, "data"));
        tree.Plant("tests/FusionRpg.Core.Tests/Actions/FixtureWalkTests.cs", AnchoredWalkSource("testsDir", 3, "fixtures"));

        var (exit, stdout, stderr) = RunGuard(tree.Root);

        Assert.True(exit == 0, $"a walk that lands on the repo root must not be flagged\nstdout:\n{stdout}");
        Assert.Contains("TEST CONTENT-ROOT GUARD OK", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_does_not_flag_a_runtime_walk_up_loop()
    {
        // ResolvableHereTests.FindSourceFile walks UP at run time (`Directory.GetParent`) from the
        // source directory until it finds the file. Its cursor is reassigned, so its depth is not a
        // static fact and the guard must stay silent rather than guess — a false positive here would
        // be the rule banning a legitimate, depth-independent search.
        using var tree = new ProbeTree();
        tree.Plant("tests/FusionRpg.Core.Tests/Actions/RuntimeWalkTests.cs", $$"""
            using System.IO;
            using System.Runtime.CompilerServices;

            namespace FusionRpg.Probe;

            public static class Planted
            {
                public static string Find(string fileName, [CallerFilePath] string here = "")
                {
                    var dir = System.IO.Path.GetDirectoryName(here);
                    for (var i = 0; i < 10 && dir != null; i++)
                    {
                        var found = System.IO.Directory.GetFiles(System.IO.Path.Combine(dir, {{Quote}}data{{Quote}}, {{Quote}}tuning{{Quote}}), fileName);
                        if (found.Length > 0) return found[0];
                        dir = System.IO.Directory.GetParent(dir)?.FullName;
                    }
                    return "";
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root);

        Assert.True(exit == 0, $"a runtime walk-up loop must not be flagged\nstdout:\n{stdout}");
    }

    [Fact]
    public void test_content_root_guard_is_gated_by_its_owning_phase()
    {
        GuardWiring.AssertGuardReachableInItsOwningPhase(RepoRoot(), "test-content-root");
    }
}
