using System.Diagnostics;
using System.Text;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The npc-story-events program's one catalogued guard, <c>narrative</c> (tasks/npc-story-events-plan.md
/// §4 D3). One guard covers every <c>narrative</c> invariant, so the failure mode it must refuse is a
/// VACUOUS pass — a filter that selects nothing, or a row whose test forgot its trait, enforcing
/// nothing while printing green.
///
/// <para>These tests run the real script, not a re-implementation of it: the control and the three
/// falsifiers plant a registry in a temp root and require the guard to name the violation. A guard
/// never seen failing is not known to work. The trait filter's runtime half
/// (<c>--run-trait-filter</c>) is CI-only — the registry's <c>args.ci</c> carries it — so this class never
/// spawns a second test host from inside one.</para>
/// </summary>
[Trait("VerificationId", "guard.narrative")]
[Trait("Guard", "narrative")]
public sealed class NarrativeGuardContractTests
{
    private static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    private static (int Exit, string Stdout, string Stderr) RunGuard(string root, string registryPath)
    {
        var arguments = new StringBuilder()
            .Append("\"")
            .Append(Path.Combine(RepoRoot(), "scripts", "guard-narrative.py"))
            .Append("\" --root \"").Append(root)
            .Append("\" --registry-path \"").Append(registryPath)
            .Append('"')
            .ToString();

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = arguments,
            WorkingDirectory = RepoRoot(),
            CreateNoWindow = true
        };

        return ExternalProcess.Run(psi, 120_000, "guard-narrative timed out");
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "narrative-guard-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The guard's own committed row -> test-class map, parsed from the script, so a planted root can
    /// be made consistent with it. Hardcoding one row here is what let a second mapped row turn the
    /// control root red — the map is the guard's, and a test that duplicates it drifts from it.
    /// </summary>
    private static IReadOnlyList<(string Row, string Csproj, string File)> CommittedMap()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "guard-narrative.py"));
        var rows = new List<(string, string, string)>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     script, @"(?m)^\s*(?<row>[a-z0-9][a-z0-9-]*)\s*\|\s*(?<csproj>tests/[^|]+?\.csproj)\s*\|\s*(?<file>tests/[^|]+?\.cs)\s*$"))
        {
            rows.Add((match.Groups["row"].Value, match.Groups["csproj"].Value.Trim(), match.Groups["file"].Value.Trim()));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static string WriteRegistry(string root, string invariantsJson)
    {
        var scriptsDir = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scriptsDir);
        var path = Path.Combine(scriptsDir, "enforcement-registry.v1.json");
        File.WriteAllText(
            path,
            "{\"schemaVersion\":1,\"guards\":{\"narrative\":{\"script\":\"scripts/guard-narrative.py\","
            + "\"tier\":\"ci\",\"status\":\"gating\"}},\"invariants\":[" + invariantsJson + "]}");
        return path;
    }

    /// <summary>The invariant rows the committed map expects, as registry JSON.</summary>
    private static string MappedRowsJson(params string[] extraRows)
    {
        var rows = CommittedMap()
            .Select(m => $"{{\"id\":\"{m.Row}\",\"source\":\"test\",\"guards\":[\"narrative\"],\"unguardableReason\":null}}")
            .Concat(extraRows);
        return string.Join(",", rows);
    }

    /// <summary>Every class the committed map names, written into the planted root.</summary>
    private static void WriteMappedClasses(string root, string? withoutTrait = null)
    {
        foreach (var (_, _, file) in CommittedMap())
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var withTrait = !string.Equals(file, withoutTrait, StringComparison.Ordinal);
            File.WriteAllText(
                path,
                withTrait
                    ? "[Trait(\"Guard\", \"narrative\")]\npublic class Planted { }\n"
                    : "public class Planted { }\n");
        }
    }

    // ---- the committed tree ------------------------------------------------------------------------

    [Fact]
    public void The_committed_map_covers_every_narrative_row_and_the_guard_is_green()
    {
        var root = RepoRoot();
        var (exit, stdout, stderr) = RunGuard(
            root, Path.Combine(root, "scripts", "enforcement-registry.v1.json"));

        Assert.True(exit == 0, $"guard-narrative failed (exit {exit})\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("NARRATIVE GUARD OK", stdout, StringComparison.Ordinal);
    }

    // ---- the falsifiers: a planted violation must be named ------------------------------------------

    [Fact]
    public void Falsifier_a_narrative_row_with_no_map_line_is_named()
    {
        var root = NewRoot();
        try
        {
            var registry = WriteRegistry(
                root,
                MappedRowsJson("""{"id":"planted-row","source":"test","guards":["narrative"],"unguardableReason":null}"""));
            WriteMappedClasses(root);

            // Findings are on STDERR. On a failing run stdout is EMPTY, so an assertion left reading
            // stdout passes without the finding ever having been produced - the silent-green shape,
            // inside the very test that exists to catch it.
            var (exit, _, stderr) = RunGuard(root, registry);

            Assert.NotEqual(0, exit);
            Assert.Contains("planted-row", stderr, StringComparison.Ordinal);
            Assert.Contains("committed row -> test-class map", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Falsifier_a_registry_with_no_narrative_row_is_refused()
    {
        var root = NewRoot();
        try
        {
            var registry = WriteRegistry(
                root,
                """{"id":"unguarded","source":"test","guards":[],"unguardableReason":"not scannable"}""");

            var (exit, _, stderr) = RunGuard(root, registry);

            Assert.NotEqual(0, exit);
            Assert.Contains("no invariant names the 'narrative' guard", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Falsifier_a_mapped_class_that_lacks_the_trait_is_refused()
    {
        var root = NewRoot();
        try
        {
            var stripped = CommittedMap()[0].File;
            var registry = WriteRegistry(root, MappedRowsJson());
            WriteMappedClasses(root, withoutTrait: stripped);

            var (exit, _, stderr) = RunGuard(root, registry);

            Assert.NotEqual(0, exit);
            Assert.Contains("does not carry", stderr, StringComparison.Ordinal);
            Assert.Contains(stripped, stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Control_the_same_planted_root_passes_once_every_mapped_row_and_trait_is_present()
    {
        var root = NewRoot();
        try
        {
            var registry = WriteRegistry(root, MappedRowsJson());
            WriteMappedClasses(root);

            var (exit, stdout, stderr) = RunGuard(root, registry);

            Assert.True(exit == 0, $"control root failed (exit {exit})\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("NARRATIVE GUARD OK", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
