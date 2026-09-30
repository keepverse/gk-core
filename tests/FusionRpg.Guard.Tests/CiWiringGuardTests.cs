using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// E24 (completeness-audit.md finding B5): <c>FusionRpg.Server.Tests</c> and <c>FusionRpg.E2E.Tests</c>
/// existed, passed locally, and ran nowhere else — "this suite exists, surely it runs" is the exact
/// mistake <c>gk-forge/tests/FusionRpg.AtomImporter.Tests</c>'s own CI wiring made once already (E14a's todo
/// entry records it). This guard is the standing version of that lesson: every test project under
/// <c>tests/</c> that has its own <c>.csproj</c> must appear in <c>ci.yml</c>, or a new suite can ship
/// silently unwired the same way twice.
/// </summary>
[Trait("VerificationId", "guard.workflows")]
public class CiWiringGuardTests
{
    [Fact]
    public void Server_and_E2E_tests_are_wired_into_ci()
    {
        var ci = ReadCi();

        Assert.Contains("tests/FusionRpg.Server.Tests/FusionRpg.Server.Tests.csproj", ci, StringComparison.Ordinal);
        Assert.Contains("tests/FusionRpg.E2E.Tests/FusionRpg.E2E.Tests.csproj", ci, StringComparison.Ordinal);
    }

    /// <summary>
    /// E35 (spec-match-modify.md §4): the one deliberate exemption to this guard. This project's whole
    /// reason to exist is testing <c>CheatState.SetLong</c>/<c>LVal</c> and the scoped match-end
    /// restore, both of which live in <c>FusionRpg.Injector</c> — a project ci.yml has never compiled
    /// (AGENTS.md, "Injector not built by CI": no game directory on the runner, so the BepInEx interop
    /// DLLs its build needs do not exist there). Adding this csproj's path string to ci.yml without
    /// actually wiring a working step would be exactly the lie this guard exists to catch — a suite
    /// that "appears wired" but never truly runs. The honest fix is this named exemption, not a fake
    /// wire-up; if `FusionRpg.Injector` itself is ever made CI-buildable, this exemption is the first
    /// place to remove.
    /// </summary>
    static readonly string[] ExemptFromCiWiring =
    {
        "tests/FusionRpg.Injector.Tests/FusionRpg.Injector.Tests.csproj",
        // PassiveTreeRosterGen is intentionally held back while its pre-existing status-roster
        // drift is resolved; listing the real project here prevents the completeness guard from
        // turning that unrelated content failure into a false CI-wiring defect.
        "tests/FusionRpg.PassiveTreeRosterGen.Tests/FusionRpg.PassiveTreeRosterGen.Tests.csproj",
    };

    [Fact]
    public void Every_test_project_under_tests_appears_somewhere_in_ci_yml()
    {
        // The general form of the guard above: walk every *.Tests.csproj actually in the tree and
        // assert its project path string appears in the workflow file. Fails loudly the day a new
        // test project is added and nobody adds the matching CI line — which is precisely how
        // Server.Tests and E2E.Tests went unrun for as long as they did.
        var repoRoot = FindRepoRoot();
        var testsDir = Path.Combine(repoRoot, "tests");
        var ci = ReadCi();

        var missing = new List<string>();
        foreach (var csproj in GuardWiring.SafeFiles(testsDir, "*.Tests.csproj"))
        {
            // bin/obj copies of a csproj are not the project — only the one directly under tests/<Name>.
            if (csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(repoRoot, csproj).Replace('\\', '/');
            if (ExemptFromCiWiring.Contains(relative, StringComparer.Ordinal)) continue;
            if (!ci.Contains(relative, StringComparison.Ordinal))
                missing.Add(relative);
        }

        Assert.True(missing.Count == 0,
            "test project(s) not referenced anywhere in .github/workflows/ci.yml: " + string.Join(", ", missing));
    }

    /// <summary>
    /// E47 (spec-validate-gate-ci.md, test 6): "the point" of the whole module — E24 built this very
    /// guard for "the next unwired suite" and was itself the next unwired suite (its own `--validate`
    /// flag shipped with no CI caller). This is that guard pointed at the fix: if the step naming
    /// `gk-forge/tools/AtomImporter` and `--validate` together ever disappears from <c>ci.yml</c>, the content
    /// validation gate goes silent again exactly the way it did the first time — an unrelated ci.yml
    /// edit that deletes or rewords the step is now caught here rather than surfacing months later as
    /// "why did nobody notice this content was broken."
    /// </summary>
    [Fact]
    public void AtomImporter_validate_gate_is_wired_into_ci()
    {
        var ci = ReadCi();

        Assert.Contains("tools/AtomImporter", ci, StringComparison.Ordinal);
        Assert.Contains("--validate", ci, StringComparison.Ordinal);
        Assert.Contains("--check", ci, StringComparison.Ordinal);
        Assert.Contains("--db", ci, StringComparison.Ordinal);
    }

    /// <summary>
    /// W7 (spec-core-split-wiring.md, ruled R24 2026-09-18: *"Yes, both, with exit checks; the CI
    /// wiring guard then covers `tools/`"*). Every <c>*.Tests.csproj</c> under <c>tools/</c> must be
    /// wired like a Core test project — a `dotnet test &lt;path&gt;` line immediately followed by its
    /// exit check — or sit in the exemption table below with a reason. The table lands <b>empty</b>:
    /// R24 answered the one open question by wiring both projects rather than excusing them. The old
    /// `ci.Contains(path)` form accepted a path inside a YAML comment; this one does not.
    /// </summary>
    static readonly string[] ExemptFromToolsCiWiring = Array.Empty<string>();

    [Fact]
    public void Every_tools_test_project_is_wired_and_exit_checked()
    {
        var repoRoot = FindRepoRoot();
        var projects = GuardWiring.SafeFiles(Path.Combine(repoRoot, "tools"), "*.Tests.csproj")
            .Where(csproj => !csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                          && !csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(csproj => Path.GetRelativePath(repoRoot, csproj).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var missing = FindUnwiredToolTestProjects(ReadCi(), projects,
            new HashSet<string>(ExemptFromToolsCiWiring, StringComparer.Ordinal));

        Assert.True(missing.Count == 0,
            "tools test project(s) not wired, or wired without an exit check: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_tools_test_project_named_only_in_a_comment_is_reported()
    {
        const string path = "tools/LawnCombatObserver.Tests/LawnCombatObserver.Tests.csproj";
        const string planted =
            "        run: |\n" +
            "          # dotnet test tools/LawnCombatObserver.Tests/LawnCombatObserver.Tests.csproj -c Release\n" +
            "          if ($LASTEXITCODE -ne 0) { throw \"x\" }\n";

        var missing = FindUnwiredToolTestProjects(planted, new[] { path }, new HashSet<string>());

        Assert.Single(missing);
        Assert.Equal(path, missing[0]);
    }

    /// <summary>
    /// A path is wired only by a line whose TRIMMED text starts with `dotnet test &lt;path&gt;` — so a
    /// YAML comment naming the path does not count — immediately followed by the workflow exit check.
    /// </summary>
    static IReadOnlyList<string> FindUnwiredToolTestProjects(string ciText, IEnumerable<string> relativePaths, ISet<string> exempt)
    {
        var lines = ciText.Replace("\r\n", "\n").Split('\n');
        var missing = new List<string>();
        foreach (var path in relativePaths)
        {
            if (exempt.Contains(path)) continue;
            var wired = false;
            for (var i = 0; i < lines.Length && !wired; i++)
            {
                if (!lines[i].TrimStart().StartsWith("dotnet test " + path, StringComparison.Ordinal)) continue;
                wired = i + 1 < lines.Length
                    && Regex.IsMatch(lines[i + 1], @"^\s*if \(\$LASTEXITCODE -ne 0\) \{ throw ");
            }
            if (!wired) missing.Add(path);
        }
        return missing;
    }

    static string ReadCi()
    {
        var path = Path.Combine(FindRepoRoot(), ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
