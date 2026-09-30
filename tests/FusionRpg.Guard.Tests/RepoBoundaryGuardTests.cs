using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/guard-repo-boundary.py (solid-enforcement `repo-boundary`, ported from the .ps1,
/// spec-repo-boundary.md) against a TEMPORARY fixture tree (checked cleanup) — never the real
/// src/, so a planted violation stays out of the repo it is proving something about.
///
/// B1: the pinned ProjectReference graph across the five standalone assemblies. B2: none of the
/// five may reference a host assembly (UnityEngine/Il2Cpp/MelonLoader/BepInEx/HarmonyLib/0Harmony)
/// or `using` one. B3 (diff-based, a temporary git repo): tasks/plan.md and tasks/todo.md are frozen,
/// and SPEC.md may never be added at the repository root.
/// </summary>
public sealed class RepoBoundaryGuardTests
{
    [Fact]
    public void The_real_tree_passes()
    {
        var (exit, stdout, stderr) = RunGuard(FindRepoRoot());
        Assert.True(exit == 0, $"exit={exit}\n{stdout}\n{stderr}");
        Assert.Contains("REPO BOUNDARY GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void B1_fails_when_Core_gains_a_reference_to_Data()
    {
        var fixture = NewCleanFixture();
        try
        {
            AppendToItemGroup(fixture, "FusionRpg.Core",
                """<ProjectReference Include="..\FusionRpg.Data\FusionRpg.Data.csproj" />""");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected B1 to fail on an out-of-graph edge");
            Assert.Contains("B1 FusionRpg.Core", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void B1_an_InternalsVisibleTo_item_is_never_mistaken_for_a_ProjectReference()
    {
        // The regression memory `core-data-guard-substring-scan` records: a naive substring guard
        // once flagged Core.csproj because it names FusionRpg.Data.Tests in an InternalsVisibleTo
        // item. This guard parses ONLY <ProjectReference> elements, so the same item must pass.
        var fixture = NewCleanFixture();
        try
        {
            AppendToItemGroup(fixture, "FusionRpg.Core",
                """<InternalsVisibleTo Include="FusionRpg.Data.Tests" />""");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void B2_fails_on_a_using_UnityEngine_in_a_standalone_project()
    {
        var fixture = NewCleanFixture();
        try
        {
            WriteSourceFile(fixture, "FusionRpg.Server", "Rogue.cs", "using UnityEngine;\nclass Rogue {}");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected B2 to fail on a host-namespace using");
            Assert.Contains("B2", stderr, StringComparison.Ordinal);
            Assert.Contains("UnityEngine", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void B2_fails_on_a_HarmonyLib_PackageReference()
    {
        var fixture = NewCleanFixture();
        try
        {
            AppendToItemGroup(fixture, "FusionRpg.Data",
                """<PackageReference Include="HarmonyLib" Version="2.2.2" />""");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected B2 to fail on a host PackageReference");
            Assert.Contains("B2", stderr, StringComparison.Ordinal);
            Assert.Contains("HarmonyLib", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void The_clean_fixture_alone_passes()
    {
        var fixture = NewCleanFixture();
        try
        {
            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void B3_fails_when_tasks_plan_md_is_modified()
    {
        var fixture = NewCleanGitFixture();
        try
        {
            File.AppendAllText(Path.Combine(fixture, "tasks", "plan.md"), "\nnew line\n");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected B3 to fail on a modified tasks/plan.md");
            Assert.Contains("B3 tasks/plan.md", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void B3_a_prefixed_plan_pair_passes()
    {
        var fixture = NewCleanGitFixture();
        try
        {
            File.WriteAllText(Path.Combine(fixture, "tasks", "widget-plan.md"), "# widget plan\n");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void B3_fails_when_SPEC_md_is_added_at_the_root()
    {
        var fixture = NewCleanGitFixture();
        try
        {
            File.WriteAllText(Path.Combine(fixture, "SPEC.md"), "# spec\n");

            var (exit, _, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, "expected B3 to fail on a root SPEC.md");
            Assert.Contains("B3 SPEC.md", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void registry_names_the_guard_and_an_owning_phase_reaches_it()
    {
        var registry = EnforcementRegistry.Load(FindRepoRoot());
        Assert.True(registry.Guards.ContainsKey("repo-boundary"),
            "guard 'repo-boundary' is not in scripts/enforcement-registry.v1.json");
        Assert.Equal("gating", registry.Guards["repo-boundary"].Status);
    }

    // ---- fixture plumbing --------------------------------------------------------------------------

    static readonly string[] AllProjects =
    {
        "FusionRpg.Contracts", "FusionRpg.Core", "FusionRpg.CheatCore", "FusionRpg.Data", "FusionRpg.Server",
    };

    static readonly Dictionary<string, string[]> AllowedGraph = new()
    {
        ["FusionRpg.Contracts"] = Array.Empty<string>(),
        ["FusionRpg.Core"] = new[] { "FusionRpg.Contracts" },
        ["FusionRpg.CheatCore"] = new[] { "FusionRpg.Contracts" },
        ["FusionRpg.Data"] = new[] { "FusionRpg.Contracts", "FusionRpg.Core", "FusionRpg.CheatCore" },
        ["FusionRpg.Server"] = new[] { "FusionRpg.Contracts", "FusionRpg.Core", "FusionRpg.CheatCore", "FusionRpg.Data" },
    };

    /// <summary>A clean tree: the five projects, the frozen task files, and a git repository with one
    /// seed commit.
    ///
    /// <para><b>Why this is a git repository.</b> It used not to be, and two of these tests passed
    /// <i>because of that</i>: on a directory git cannot answer for, the retired PowerShell guard ran
    /// its diff with <c>2&gt;&amp;1</c> suppressed, read the empty output as "nothing changed", and
    /// reported a clean run. The port reports an unanswerable B3 as a named finding instead, so those
    /// two tests went red - correctly. Their INTENT is unaffected (B1 parses only
    /// &lt;ProjectReference&gt; elements; a clean graph is clean), so the fixture is made answerable
    /// rather than the expectation weakened. <see cref="An_unanswerable_B3_is_reported_rather_than_ignored"/>
    /// is the test that stops the hole coming back.</para></summary>
    static string NewCleanFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-repoboundary-" + Guid.NewGuid().ToString("N"));
        foreach (var project in AllProjects)
        {
            var projDir = Path.Combine(dir, "src", project);
            Directory.CreateDirectory(projDir);
            var refs = string.Join("\n    ", AllowedGraph[project]
                .Select(r => $"""<ProjectReference Include="..\{r}\{r}.csproj" />"""));
            File.WriteAllText(Path.Combine(projDir, project + ".csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    {refs}
                  </ItemGroup>
                </Project>
                """);
        }
        Directory.CreateDirectory(Path.Combine(dir, "tasks"));
        File.WriteAllText(Path.Combine(dir, "tasks", "plan.md"), "# perf plan (frozen history)\n");
        File.WriteAllText(Path.Combine(dir, "tasks", "todo.md"), "# perf todo (frozen history)\n");
        RunGit(dir, "init", "-q");
        RunGit(dir, "config", "user.email", "guard-test@example.invalid");
        RunGit(dir, "config", "user.name", "guard-test");
        RunGit(dir, "add", "-A");
        RunGit(dir, "commit", "-q", "-m", "seed");
        return dir;
    }

    /// <summary>The historical name for the same fixture, kept so the B3 call sites read unchanged.</summary>
    static string NewCleanGitFixture() => NewCleanFixture();

    /// <summary>The hole, pinned. A root git cannot answer for must be REPORTED, because a silent
    /// clean run is how a typo in CI's base reference disabled the whole of B3 while reporting
    /// green.</summary>
    [Fact]
    public void An_unanswerable_B3_is_reported_rather_than_ignored()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-repoboundary-nogit-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var project in AllProjects)
            {
                var projDir = Path.Combine(fixture, "src", project);
                Directory.CreateDirectory(projDir);
                File.WriteAllText(Path.Combine(projDir, project + ".csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            }

            var (exit, _, stderr) = RunGuard(fixture);

            Assert.NotEqual(0, exit);
            Assert.Contains("B3", stderr, StringComparison.Ordinal);
            Assert.Contains("GIT-FAILED", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    static void AppendToItemGroup(string fixtureRoot, string project, string xmlFragment)
    {
        var path = Path.Combine(fixtureRoot, "src", project, project + ".csproj");
        var text = File.ReadAllText(path);
        text = text.Replace("</ItemGroup>", $"    {xmlFragment}\n  </ItemGroup>");
        File.WriteAllText(path, text);
    }

    static void WriteSourceFile(string fixtureRoot, string project, string fileName, string csharp) =>
        File.WriteAllText(Path.Combine(fixtureRoot, "src", project, fileName), csharp);

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

    /// <summary>Runs the guard FROM the real repo (so the interpreter is found) but pointed AT the
    /// fixture directory via --root, matching every sibling Guard.Tests fixture in this project.
    /// <para><b>Where the output goes:</b> findings and a FAILED verdict are on <b>stderr</b>; only
    /// the OK verdict reaches stdout, and that is the one assertion below that still reads
    /// stdout.</para></summary>
    static (int Exit, string Stdout, string Stderr) RunGuard(string fixtureRoot)
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-repo-boundary.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{fixtureRoot}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

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
