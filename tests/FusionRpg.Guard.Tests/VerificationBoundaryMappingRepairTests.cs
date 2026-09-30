using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Structural regression coverage for the BCU2.12 verification-boundary repair.
///
/// The launcher/report tests are owned by the separate Seedsmith acceptance lane.  The planner
/// proof therefore uses a tiny planted registry with the same boundary shape instead of making
/// this structural test depend on that lane's dirty files; the first two tests still read and
/// assert the real registry itself.
/// </summary>
[Trait("VerificationId", "guard.verification-boundaries")]
public sealed class VerificationBoundaryMappingRepairTests
{
    private const string RegistryPath = "scripts/verification-boundaries.v1.json";
    private const string BoundaryId = "seedsmith-bcu212";
    private const string FallbackId = "seedsmith-fallback";
    private const string TreeOwnerId = "seedsmith-trees";
    private const string NeighborPath = "tools/seedsmith/seedsmith/adapters/trees/species/generate_tree.py";
    private const string NeighborTestPath = "tools/seedsmith/tests/test_tree_species_fixture.py";

    private static readonly string[] ConcretePaths =
    {
        ".claude/cmdc-agents/scripts/bcu212-full-run.ps1",
        ".claude/cmdc-agents/scripts/bcu212-report.py",
        "tools/seedsmith/_j9_batch_run.py",
    };

    private static readonly string[] FocusedTestFiles =
    {
        "tools/seedsmith/tests/test_bcu212_launcher.py",
        "tools/seedsmith/tests/test_bcu212_report.py",
        "tools/seedsmith/tests/test_j9_batch_run.py",
    };

    private static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void Bcu212_boundary_maps_exactly_the_three_paths_to_the_three_real_tests()
    {
        using var document = ReadRegistry();
        var boundary = FindBoundary(document.RootElement, BoundaryId);

        Assert.Equal("owner", boundary.GetProperty("kind").GetString());
        Assert.Equal("seedsmith", boundary.GetProperty("project").GetString());
        Assert.Equal("focused", boundary.GetProperty("level").GetString());
        Assert.Equal(ConcretePaths, StringArray(boundary, "paths"));
        Assert.Equal(FocusedTestFiles, StringArray(boundary, "testFiles"));
        Assert.Empty(StringArray(boundary, "guards"));

        var boundaries = document.RootElement.GetProperty("boundaries").EnumerateArray().ToArray();
        Assert.All(ConcretePaths, path => Assert.Equal(BoundaryId, ResolveOwnerId(path, boundaries)));
    }

    [Fact]
    public void Bcu212_boundary_precedes_the_fallback_and_does_not_steal_the_tree_owner()
    {
        using var document = ReadRegistry();
        var boundaries = document.RootElement.GetProperty("boundaries").EnumerateArray().ToArray();
        var bcuIndex = Array.FindIndex(boundaries, b => b.GetProperty("id").GetString() == BoundaryId);
        var fallbackIndex = Array.FindIndex(boundaries, b => b.GetProperty("id").GetString() == FallbackId);

        Assert.True(bcuIndex >= 0, $"missing {BoundaryId}");
        Assert.True(fallbackIndex >= 0, $"missing {FallbackId}");
        Assert.True(bcuIndex < fallbackIndex,
            $"{BoundaryId} must precede {FallbackId}: {bcuIndex} is not before {fallbackIndex}");

        var treeOwner = FindBoundary(document.RootElement, TreeOwnerId);
        Assert.Contains("tools/seedsmith/seedsmith/adapters/trees/**", StringArray(treeOwner, "paths"));
        Assert.Equal(TreeOwnerId, ResolveOwnerId(NeighborPath, boundaries));
    }

    [Fact]
    public void Phase0A_fail_closed_scripts_map_to_the_manager_boundary()
    {
        using var document = ReadRegistry();
        var boundaries = document.RootElement.GetProperty("boundaries").EnumerateArray().ToArray();
        var boundary = FindBoundary(document.RootElement, "manager-fail-closed");
        var paths = StringArray(boundary, "paths");

        Assert.Equal("manager-fail-closed", boundary.GetProperty("project").GetString());
        Assert.Equal("focused", boundary.GetProperty("level").GetString());
        Assert.Equal(".claude/cmdc-agents/scripts/test_fail_closed_pipeline.py",
            Assert.Single(StringArray(boundary, "testFiles")));
        Assert.All(paths, path => Assert.Equal("manager-fail-closed", ResolveOwnerId(path, boundaries)));
    }

    [Fact]
    public void PlanOnly_selects_only_the_three_bcu212_tests_and_keeps_the_neighbor_on_seedsmith_trees()
    {
        var root = PlantPlannerFixture();
        try
        {
            var bcuPaths = PathArguments(ConcretePaths);
            var (bcuExit, bcuStdout, bcuStderr) = RunPlanner(
                root,
                // The root is DOUBLE-quoted, like every other value here. It was single-quoted for
                // PowerShell, and `ProcessStartInfo.Arguments` does NOT strip single quotes -- so the
                // planner received a path with a literal apostrophe on each end and answered
                // ROOT-UNRESOLVABLE, which names a MISSING DIRECTORY rather than a quoting mistake.
                $"--paths {bcuPaths} --root \"{root}\" " +
                "--allow-unscoped --plan-only --format json\"");

            Assert.True(bcuExit == 0,
                $"BCU2.12 plan failed exit={bcuExit}\nstdout:\n{bcuStdout}\nstderr:\n{bcuStderr}");
            Assert.DoesNotContain(FallbackId, bcuStdout, StringComparison.Ordinal);

            using var bcuPlan = JsonDocument.Parse(bcuStdout);
            var bcuSelections = bcuPlan.RootElement.GetProperty("selections").EnumerateArray()
                .Where(s => ConcretePaths.Contains(s.GetProperty("path").GetString(), StringComparer.Ordinal))
                .ToArray();
            Assert.Equal(ConcretePaths.Length, bcuSelections.Length);
            Assert.All(bcuSelections, selection =>
                Assert.Equal(BoundaryId, selection.GetProperty("boundary").GetString()));

            var pytest = bcuPlan.RootElement.GetProperty("checks").EnumerateArray()
                .Single(check => check.GetProperty("kind").GetString() == "pytest");
            Assert.Equal(
                FocusedTestFiles.OrderBy(path => path, StringComparer.Ordinal),
                pytest.GetProperty("targets").EnumerateArray().Select(e => e.GetString() ?? ""));

            var (neighborExit, neighborStdout, neighborStderr) = RunPlanner(
                root,
                $"--paths \"{NeighborPath}\" --root \"{root}\" " +
                "--allow-unscoped --plan-only --format json\"");
            Assert.True(neighborExit == 0,
                $"neighbor plan failed exit={neighborExit}\nstdout:\n{neighborStdout}\nstderr:\n{neighborStderr}");

            using var neighborPlan = JsonDocument.Parse(neighborStdout);
            var neighborSelection = neighborPlan.RootElement.GetProperty("selections").EnumerateArray()
                .Single(selection => selection.GetProperty("path").GetString() == NeighborPath);
            Assert.Equal(TreeOwnerId, neighborSelection.GetProperty("boundary").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonDocument ReadRegistry()
    {
        var path = Path.Combine(RepoRoot(), RegistryPath.Replace('/', Path.DirectorySeparatorChar));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement FindBoundary(JsonElement root, string id) =>
        root.GetProperty("boundaries").EnumerateArray()
            .Single(boundary => boundary.GetProperty("id").GetString() == id);

    private static string[] StringArray(JsonElement value, string property) =>
        value.GetProperty(property).EnumerateArray().Select(item => item.GetString() ?? "").ToArray();

    private static string ResolveOwnerId(string path, JsonElement[] boundaries)
    {
        var hits = boundaries
            .Where(boundary => boundary.GetProperty("kind").GetString() == "owner")
            .SelectMany(boundary => StringArray(boundary, "paths").Select(pattern => new
            {
                Id = boundary.GetProperty("id").GetString()!,
                Pattern = pattern,
                Specificity = PatternSpecificity(pattern),
            }))
            .Where(hit => Matches(path, hit.Pattern))
            .ToArray();

        Assert.NotEmpty(hits);
        var best = hits.Max(hit => hit.Specificity);
        var owners = hits.Where(hit => hit.Specificity == best)
            .Select(hit => hit.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(owners);
        return owners[0];
    }

    private static bool Matches(string path, string pattern)
    {
        if (pattern.EndsWith("/**", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith(pattern[..^2], StringComparison.OrdinalIgnoreCase);
        if (!pattern.Contains('*', StringComparison.Ordinal))
            return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        var lastSlash = pattern.LastIndexOf('/');
        var directory = lastSlash < 0 ? "" : pattern[..(lastSlash + 1)];
        var tail = pattern[(lastSlash + 1)..];
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) return false;
        var pathTail = path[directory.Length..];
        if (pathTail.Contains('/', StringComparison.Ordinal)) return false;
        return Regex.IsMatch(
            pathTail,
            "^" + Regex.Escape(tail).Replace("\\*", "[^/]*") + "$",
            RegexOptions.IgnoreCase);
    }

    private static int PatternSpecificity(string pattern)
    {
        var rank = pattern.EndsWith("/**", StringComparison.OrdinalIgnoreCase) ? 0
            : pattern.Contains('*', StringComparison.Ordinal) ? 2
            : 3;
        return rank * 100_000 + pattern.Length;
    }

    /// <summary>
    /// Space-separated, double-quoted paths for argparse's <c>--paths</c>.
    /// <para>
    /// This used to build a PowerShell array, <c>@('a','b')</c>, comma-joined. argparse takes
    /// space-separated values, so passing the old spelling hands it ONE path literally named
    /// <c>@('a','b')</c> -- which resolves to nothing, and an empty plan reads as a scope refusal rather
    /// than as a bug. The single-quote doubling is gone with the array syntax: it existed to escape
    /// inside PowerShell, and the quoting here is the one <c>ProcessStartInfo.Arguments</c> parses.
    /// </para>
    /// </summary>
    private static string PathArguments(IEnumerable<string> paths) =>
        string.Join(" ", paths.Select(path => $"\"{path}\""));

    /// <summary>
    /// Runs the Python planner against a PLANTED root. Both of this file's former PowerShell callers
    /// were planner sites, so the helper is converted in place rather than duplicated.
    /// </summary>
    private static (int Exit, string Stdout, string Stderr) RunPlanner(string root, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"scripts/verify-change.py {arguments}",
            WorkingDirectory = root,
            CreateNoWindow = true,
        };
        return ExternalProcess.Run(psi, 300_000, "verify-change.py fixture timed out");
    }

    private static string PlantPlannerFixture()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-bcu212-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
        Copy(repo, "scripts/verify-change.py", root);
        // The guard and BOTH libs: the Python guard imports `verification_boundaries.py`, and
        // `verify-change.py` - which this fixture also copies - IMPORTS `verification_boundaries.py`
        // and is still live. Copying one for the other leaves a fixture with a planner that cannot start
        // or a guard that cannot import.
        Copy(repo, "scripts/guard-verification-boundaries.py", root);
        Copy(repo, "scripts/lib/verification_boundaries.py", root);
        File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"),
            "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n\n");
        File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"),
            "{\"schemaVersion\":1,\"guards\":{},\"invariants\":[]}\n");

        foreach (var path in ConcretePaths)
            WriteFixtureFile(root, path, "fixture\n");
        foreach (var path in FocusedTestFiles)
            WriteFixtureFile(root, path, "def test_fixture(): pass\n");
        WriteFixtureFile(root, NeighborPath, "fixture\n");
        WriteFixtureFile(root, NeighborTestPath, "def test_fixture(): pass\n");

        var registry = new
        {
            schemaVersion = 5,
            projects = new
            {
                seedsmith = new { runner = "pytest", root = "tools/seedsmith", tests = "tests" },
            },
            boundaries = new object[]
            {
                new
                {
                    id = BoundaryId,
                    kind = "owner",
                    paths = ConcretePaths,
                    project = "seedsmith",
                    testFiles = FocusedTestFiles,
                    guards = Array.Empty<string>(),
                    level = "focused",
                },
                new
                {
                    id = FallbackId,
                    kind = "owner",
                    paths = new[] { "tools/seedsmith/**" },
                    project = "seedsmith",
                    guards = Array.Empty<string>(),
                    level = "module",
                },
                new
                {
                    id = TreeOwnerId,
                    kind = "owner",
                    paths = new[] { "tools/seedsmith/seedsmith/adapters/trees/**" },
                    project = "seedsmith",
                    testFiles = new[] { NeighborTestPath },
                    guards = Array.Empty<string>(),
                    level = "focused",
                },
            },
        };
        File.WriteAllText(
            Path.Combine(root, RegistryPath.Replace('/', Path.DirectorySeparatorChar)),
            JsonSerializer.Serialize(registry));
        return root;
    }

    private static void Copy(string repo, string relativePath, string root)
    {
        var destination = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar)), destination);
    }

    private static void WriteFixtureFile(string root, string relativePath, string content)
    {
        var destination = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, content);
    }
}
