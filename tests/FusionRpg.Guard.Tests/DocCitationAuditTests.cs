using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// scripts/audit-doc-citations.py EXEMPT 1: a closed list of files that are real but outside git on
/// purpose (gitignored, or the user-level cmdc-subagent skill). verify-change.py runs the audit
/// with --strict on every changed Markdown file, so a missing name here fails every edit to
/// AGENTS.md (found 2026-09-19). The contract: every listed name is exempt, and a name NOT on the
/// list still reports HIGH, so the exemption cannot hide an invented filename.
///
/// Runs in memory: a Python harness loads the audit module, replaces its git listing with one
/// virtual document and serves that document's text without touching the disk.
/// </summary>
[Trait("VerificationId", "guard.doc-citations")]
public sealed class DocCitationAuditTests
{
    /// <summary>The harness is handed the audit script's ABSOLUTE path.
    ///
    /// It carried the relative `scripts/audit-doc-citations.py`, which resolved against the process working
    /// directory - gk-core - while the script is the WORKSPACE ROOT's, because development documentation and
    /// its tooling are gk-workflow's. Measured: `scripts/audit-doc-citations.py` is present at the workspace
    /// root and absent from gk-core. All thirteen of these tests failed with a FileNotFoundError from inside
    /// the harness, which is why the message read "audit harness failed exit=1" and a traceback rather than
    /// anything about a citation.
    ///
    /// The path is interpolated rather than appended, so the working directory can be anything: a relative
    /// path in a harness is a claim about the CWD that the CWD never promised to honour.
    /// </summary>
    const string HarnessTemplate = """
import importlib.util, io, json, sys
spec = importlib.util.spec_from_file_location("audit", sys.argv[1])
audit = importlib.util.module_from_spec(spec); spec.loader.exec_module(audit)
doc = "virtual/NOTES.md"
text = sys.stdin.read()
audit.tracked_files = lambda: [doc]
audit.deleted_paths = lambda: set()
real_open = io.open
audit.io.open = lambda p, *a, **k: io.StringIO(text) if p == doc else real_open(p, *a, **k)
findings, _, _ = audit.audit("virtual/")
print(json.dumps([[f["ref"], f["sev"]] for f in findings]))
""";

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string Audit(string markdown)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = RepoRoot(),
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(HarnessTemplate);
        // `python -c CODE arg` puts `arg` at `sys.argv[1]`, so the script path travels with the harness
        // instead of being a claim about the working directory.
        psi.ArgumentList.Add(KeepverseRoots.Workspace() + "/scripts/audit-doc-citations.py");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(markdown);
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(60_000), "audit harness timed out");
        var stdout = stdoutTask.Result;
        Assert.True(p.ExitCode == 0, $"audit harness failed exit={p.ExitCode}\n{stdout}\n{stderrTask.Result}");
        return stdout;
    }

    [Theory]
    [InlineData("skills-lock.json")]
    [InlineData("settings.local.json")]
    [InlineData("cmdc_agent.py")]
    [InlineData("allowed-models.json")]
    [InlineData("status.json")]
    public void A_deliberate_out_of_repo_file_is_not_reported(string name)
    {
        var findings = Audit($"The runner reads `{name}` before every segment.\n");

        Assert.DoesNotContain(name, findings, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invented_filename_still_reports_high()
    {
        var findings = Audit("The runner reads `cmdc_agent_v2_invented.py` before every segment.\n");

        Assert.Contains("cmdc_agent_v2_invented.py", findings, StringComparison.Ordinal);
        Assert.Contains("HIGH", findings, StringComparison.Ordinal);
    }

    /// <summary>
    /// A citation into a dot-directory used to be structurally invisible: CITATION required the
    /// backticked token to start with [A-Za-z0-9_], so every `.claude/…`, `.kilo/…` and
    /// `.commandcode/…` reference in the repo was skipped (measured 2026-09-26: 95 of them, 74
    /// distinct tokens, across 1693 documents). That is how a dead dot-path can sit in a document
    /// while the audit reports that document clean.
    ///
    /// The path is INVENTED on purpose: the real machine-local dot-files (`.kilo/setup-script.ps1`,
    /// `.kilo/agent-manager.json`) are on EXEMPT 1 because they exist on the owner's machine and are
    /// excluded through `.git/info/exclude`, so a test using one would be asserting the exemption
    /// list, not the scanner.
    /// </summary>
    [Fact]
    public void A_dead_dot_directory_citation_reports_high()
    {
        var findings = Audit("Setup runs via `.kilo/no-such-setup-2026.ps1` before the first task.\n");

        Assert.Contains(".kilo/no-such-setup-2026.ps1", findings, StringComparison.Ordinal);
        Assert.Contains("HIGH", findings, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counter-case that keeps the fix from inventing citations: a leading dot alone is not a
    /// path. `.v1.json`, `.test.ts` and `.Tests.csproj` are the TAIL of a citation whose head sits in
    /// an earlier backtick run, and matching them would report a dead file for every such fragment
    /// in the corpus. Only a dot-DIRECTORY (a slash in the first segment) is a citation.
    /// </summary>
    [Theory]
    [InlineData("The tuning file is `combat-ai.v1.json` and its shape is `.v1.json` in prose.\n")]
    [InlineData("The spec names `.Tests.csproj` as a suffix in this sentence.\n")]
    [InlineData("See `.test.tsx` for the shape of the citation.\n")]
    public void A_bare_extension_fragment_is_not_a_citation(string markdown)
    {
        var findings = Audit(markdown);

        Assert.DoesNotContain("D1", findings, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner ruling of 2026-09-25 keeps `.commandcode/settings.json` local-only behind a narrow
    /// ignore while its taste files stay tracked, so a citation to it is correct and must not be
    /// reported. The two machine-local Kilo runtime files join it for the same reason: both are read
    /// by shipped code (`gk-core/scripts/worktree_cleanup_core.py:215` reads the legacy manager registry) and
    /// both are excluded through `.git/info/exclude`, so a citation to them is correct too. They join
    /// the closed EXEMPT 1 list — and the "invented name still reports" test above is what stops that
    /// list from becoming a hole.
    /// </summary>
    [Theory]
    [InlineData("Taste lives in `.commandcode/settings.json` on this machine only.\n")]
    [InlineData("The legacy registry is `.kilo/agent-manager.json`.\n")]
    [InlineData("Worktree setup is `.kilo/setup-script.ps1`.\n")]
    public void A_real_but_untracked_dot_file_is_exempt(string markdown)
    {
        var findings = Audit(markdown);

        Assert.Equal("[]", findings.Trim());
    }
}
