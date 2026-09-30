using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// W0 (spec-core-split-wiring.md): the release gate masked its test failures — its four
/// <c>dotnet test</c> lines ran with no exit check between them, so only the last line decided the
/// step (the same defect a real CI run reproduced on 2026-08-24). Every test command a workflow runs
/// must be followed on the next line by an exit check, so a failure in any project fails its step.
/// One rule over every workflow file, no line or step counts (<c>validation-ssot.md</c>).
/// </summary>
[Trait("VerificationId", "guard.workflows")]
public class WorkflowExitCheckTests
{
    // A line whose trimmed text starts with one of these runs tests; its result must decide the step.
    static readonly string[] TestCommandPrefixes =
    {
        "dotnet test ",
        "python -m pytest ",
        // `python gk-core/scripts/test_sharded.py `, not `.\scripts\test-sharded.ps1 `: that file was ported
        // to `test_sharded.py` and no longer exists, so the prefix could never match a real workflow
        // line again and this guard had quietly stopped covering the one sharded step. A guard that
        // watches a string nothing emits is the SILENT GREEN this class of defect produces.
        @"python scripts/test_sharded.py ",
    };

    static readonly Regex ExitCheckLine = new(
        @"^\s*if \(\$LASTEXITCODE -ne 0\) \{ throw ",
        RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(WorkflowFiles))]
    public void Every_test_command_is_followed_by_its_exit_check(string relativePath)
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), relativePath));
        var violations = FindMissingExitChecks(lines);
        Assert.True(
            violations.Count == 0,
            $"{relativePath}: test command(s) whose failure the step would not see:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void A_planted_workflow_missing_an_exit_check_is_reported()
    {
        string[] planted =
        {
            "          dotnet test tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj -c Release",
            "          if ($LASTEXITCODE -ne 0) { throw \"FusionRpg.Core.Tests failed\" }",
            "          dotnet test tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj -c Release",
            "          Write-Host \"no exit check here\"",
            "          python -m pytest tests -q",
            "          if ($LASTEXITCODE -ne 0) { throw \"seedsmith's own test suite failed\" }",
        };

        var violations = FindMissingExitChecks(planted);

        Assert.Single(violations);
        Assert.Contains("line 3", violations[0], StringComparison.Ordinal);
    }

    /// <summary>Every workflow file, so a new one cannot ship a test command whose failure is silent.</summary>
    public static IEnumerable<object[]> WorkflowFiles()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, ".github", "workflows");
        foreach (var file in Directory.GetFiles(dir, "*.yml").OrderBy(p => p, StringComparer.Ordinal))
            yield return new object[] { Path.GetRelativePath(root, file).Replace('\\', '/') };
    }

    static IReadOnlyList<string> FindMissingExitChecks(IReadOnlyList<string> lines)
    {
        var violations = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!TestCommandPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal)))
                continue;
            var next = i + 1 < lines.Count ? lines[i + 1] : string.Empty;
            if (!ExitCheckLine.IsMatch(next))
                violations.Add($"  line {i + 1}: `{trimmed}` is not followed by an exit check");
        }
        return violations;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
