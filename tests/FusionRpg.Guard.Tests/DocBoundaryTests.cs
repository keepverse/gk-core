using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The docs and assistant-config boundary (verification-boundaries.v1.json
/// <c>docs-and-assistant-config</c>). Before it existed every docs/** and .claude/** path was
/// refused with VERIFICATION BOUNDARY MISSING, so a change to a standard or a skill had no
/// verification path at all (creative-mode.md §8.5). These assert the mapping contract: the path
/// resolves to its owner and every Markdown path gets the file-scoped doc-citation audit. They never
/// assert how many checks are selected.
/// </summary>
[Trait("VerificationId", "guard.doc-boundary")]
public sealed class DocBoundaryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "verify-change.py")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root with scripts/verify-change.py");
    }

    /// <summary>
    /// Runs the Python planner. The PowerShell form took `-Paths a,b` as a PowerShell ARRAY, whereas
    /// argparse takes space-separated values; the interpreter and the script are named here rather than
    /// passed in, so a caller supplies only flags.
    /// </summary>
    private static (int Exit, string Stdout, string Stderr) Plan(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"scripts/verify-change.py --paths {path} --allow-unscoped --plan-only",
            WorkingDirectory = RepoRoot(),
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 300_000, "verify-change.py plan timed out");
    }

    [Theory]
    [InlineData("docs/PRINCIPLES.md")]
    [InlineData(".claude/skills/creative-mode/SKILL.md")]
    [InlineData("AGENTS.md")]
    public void A_doc_or_assistant_config_path_resolves_to_its_boundary_and_its_citation_audit(string path)
    {
        var (exit, stdout, stderr) = Plan(path);

        Assert.True(exit == 0, $"plan failed for '{path}' exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("docs-and-assistant-config", stdout, StringComparison.Ordinal);
        Assert.Contains($"doc-citations: {path}", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_more_specific_doc_owner_still_wins_over_the_broad_docs_boundary()
    {
        // docs/architecture/power/** has its own owner; the broad docs/** row must not capture it.
        var (exit, stdout, stderr) = Plan("docs/architecture/power/ssot-power-scale.md");

        Assert.True(exit == 0, $"plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("power-ssot-doc", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("-> docs-and-assistant-config", stdout, StringComparison.Ordinal);
    }
}
