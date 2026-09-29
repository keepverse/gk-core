using System.Text.Json;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// python-test-lane §CI: the Python counterpart of <see cref="CiWiringGuardTests"/>. Every `pytest`
/// runner project in <c>verification-boundaries.v1.json</c> must have a real CI step: a
/// <c>python -m pytest</c> line under a step whose <c>working-directory</c> equals that project's own
/// <c>root</c>. This is what keeps a re-pointed or newly added pytest project (like `tuning-py`, whose
/// tests ran nowhere in CI before the E2 step landed) from going unwired the same way
/// <c>gk-forge/tests/FusionRpg.AtomImporter.Tests</c> once did.
/// </summary>
[Trait("VerificationId", "guard.workflows")]
public class CiPytestWiringTests
{
    [Fact]
    public void Every_pytest_project_has_a_ci_step_running_pytest_in_its_own_root()
    {
        var repoRoot = FindRepoRoot();
        var roots = PytestProjectRoots(repoRoot);
        var wiredRoots = WiredPytestRoots(ReadCi(repoRoot));

        var missing = roots.Where(r => !wiredRoots.Contains(r)).ToArray();
        Assert.True(missing.Length == 0,
            "pytest project root(s) with no 'python -m pytest' CI step at that working-directory: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_pytest_line_under_a_different_working_directory_does_not_count()
    {
        const string planted =
            "      - name: fake\n" +
            "        working-directory: tools/other\n" +
            "        run: |\n" +
            "          python -m pytest . -q\n";

        var wired = WiredPytestRoots(planted);

        Assert.DoesNotContain("tools/seedsmith", wired);
    }

    /// <summary>Every project id whose registry value is an object with <c>runner: "pytest"</c>, as
    /// its <c>root</c> field.</summary>
    static IReadOnlyList<string> PytestProjectRoots(string repoRoot)
    {
        var registryPath = Path.Combine(repoRoot, "scripts", "verification-boundaries.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(registryPath));
        var roots = new List<string>();
        foreach (var property in doc.RootElement.GetProperty("projects").EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (property.Value.TryGetProperty("runner", out var runner) && runner.GetString() == "pytest"
                && property.Value.TryGetProperty("root", out var root))
            {
                roots.Add(root.GetString()!);
            }
        }
        return roots;
    }

    /// <summary>Every <c>working-directory</c> value of a CI step whose own <c>run:</c> block contains
    /// a <c>python -m pytest</c> line — a step-scoped scan (not "does the whole file contain both
    /// strings somewhere"), so a pytest line in one step and an unrelated working-directory in another
    /// can never be mistaken for a real wire-up.</summary>
    static HashSet<string> WiredPytestRoots(string ciText)
    {
        var lines = ciText.Replace("\r\n", "\n").Split('\n');
        var wired = new HashSet<string>(StringComparer.Ordinal);
        string? currentWorkingDirectory = null;
        var sawPytestLine = false;
        void Flush()
        {
            if (currentWorkingDirectory is not null && sawPytestLine) wired.Add(currentWorkingDirectory);
        }
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- name:", StringComparison.Ordinal))
            {
                Flush();
                currentWorkingDirectory = null;
                sawPytestLine = false;
                continue;
            }
            if (trimmed.StartsWith("working-directory:", StringComparison.Ordinal))
            {
                currentWorkingDirectory = trimmed["working-directory:".Length..].Trim();
                continue;
            }
            if (trimmed.StartsWith("python -m pytest", StringComparison.Ordinal)) sawPytestLine = true;
        }
        Flush();
        return wired;
    }

    static string ReadCi(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
