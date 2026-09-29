using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// guard-runner (solid-enforcement wave 0). Drives the real <c>gk-core/scripts/run_guards.py</c> against a
/// temporary registry and fake guard scripts under a temp root -- this IS the disk under test, so the
/// tree is created and removed here with a throwing delete (testing-standard.md), never a swallowed one.
///
/// <para>The falsifiers of interest are the masking ones: one red guard must not hide a second. That is
/// the defect ci.yml's old <c>dotnet test</c> step met when only the last exit code decided the step.</para>
///
/// <para><b>Repointed to the Python runner.</b> The fake guards are <c>.py</c>, so this class no longer
/// needs an interpreter beyond <c>python</c> -- which matters because the two tests that used to be
/// parameterised by host existed only to compare <c>pwsh</c> against Windows PowerShell 5.1, a question
/// a Python runner does not raise. Their SUBJECTS survive the port and are kept, renamed to what they
/// actually assert; the host comparison is gone because there is no longer a host to compare.</para>
/// </summary>
[Trait("VerificationId", "guard.guard-runner")]
public sealed class GuardRunnerTests
{
    const int TimeoutMs = 180_000;

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "run_guards.py"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>
    /// Runs the real runner under <c>python</c>. The flags are the PORT's dialect, not the retired
    /// PowerShell spelling -- a caller that hands argparse <c>-Only</c> gets <c>unrecognized
    /// arguments</c> and exit 2, which is a different tool's failure from the one under test.
    /// </summary>
    static (int Exit, string Stdout, string Stderr) RunRunner(string tempRoot, params string[] extra)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot(),
        };
        psi.ArgumentList.Add(Path.Combine(RepoRoot(), "scripts", "run_guards.py"));
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(tempRoot);
        psi.ArgumentList.Add("--tier");
        psi.ArgumentList.Add("ci");
        foreach (var arg in extra) psi.ArgumentList.Add(arg);
        return ExternalProcess.Run(psi, TimeoutMs, "guard runner timed out");
    }

    /// <summary>A fake Python guard. Deliberately a separate interpreter process, as a real one is.</summary>
    static string PyGuard(string? body = null) =>
        body ?? "import sys\nsys.exit(0)\n";

    static string Row(string id, string scriptFile, string tier, string status, string extra = "") =>
        $"\"{id}\":{{\"script\":\"scripts/{scriptFile}\",\"tier\":\"{tier}\",\"status\":\"{status}\"," +
        $"\"backlogModule\":null,\"localReason\":null{extra}}}";

    static string RegistryJson(params string[] rows) =>
        "{\"schemaVersion\":1,\"guards\":{" + string.Join(",", rows) + "},\"invariants\":[]}";

    [Fact]
    public void A_red_gating_guard_does_not_stop_a_later_guard()
    {
        using var tree = new FakeTree();
        var marker = Path.Combine(tree.Root, "ran.marker");
        tree.Script("guard-red.py", "import sys\nsys.stderr.write('red guard\\n')\nsys.exit(1)\n");
        tree.Script("guard-marker.py", $"open({Q(marker)}, 'w').write('ran')\n");
        tree.Registry(RegistryJson(
            Row("first-red", "guard-red.py", "ci", "gating"),
            Row("second-marker", "guard-marker.py", "ci", "gating")));

        var (exit, stdout, stderr) = RunRunner(tree.Root);

        Assert.True(exit != 0, $"a red gating guard must fail the run; exit={exit}\n{stdout}\n{stderr}");
        Assert.True(File.Exists(marker), "the guard AFTER the red one never ran - one guard masked another");
        Assert.Contains("Guard runner", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_red_backlog_guard_reports_under_BACKLOG_and_never_fails_the_run()
    {
        using var tree = new FakeTree();
        tree.Script("guard-backlog.py", "import sys\nsys.stderr.write('backlog finding\\n')\nsys.exit(1)\n");
        tree.Registry(RegistryJson(Row("only-backlog", "guard-backlog.py", "ci", "backlog")));

        var (exit, stdout, stderr) = RunRunner(tree.Root, "--include-backlog");

        Assert.True(exit == 0, $"a backlog guard must never fail the run; exit={exit}\n{stdout}\n{stderr}");
        Assert.Contains("BACKLOG", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Only_backlog_guard_reports_and_never_fails()
    {
        using var tree = new FakeTree();
        tree.Script("guard-backlog.py", "import sys\nsys.stderr.write('backlog finding\\n')\nsys.exit(1)\n");
        tree.Registry(RegistryJson(Row("only-backlog", "guard-backlog.py", "ci", "backlog")));

        var (exit, stdout, stderr) = RunRunner(tree.Root, "--only", "only-backlog");

        // --only must still honour the registry's status: verify-change can name a backlog guard without
        // failing an edit on findings the module has not cleared yet.
        Assert.True(exit == 0, $"a backlog guard named by --only must not fail; exit={exit}\n{stdout}\n{stderr}");
        Assert.Contains("BACKLOG", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_Only_id_is_REFUSED_by_name()
    {
        using var tree = new FakeTree();
        tree.Script("guard-ok.py", PyGuard());
        tree.Registry(RegistryJson(Row("ok", "guard-ok.py", "ci", "gating")));

        var (exit, stdout, stderr) = RunRunner(tree.Root, "--only", "not-a-guard");

        Assert.True(exit != 0, "an unknown --only id must fail loudly");
        // The refusal NAME, not its prose. The name is a closed vocabulary the runner's own contract
        // suite enumerates; the sentence around it is free to be reworded, so asserting the sentence
        // would make this test fail on a change that improved the message.
        Assert.Contains("UNKNOWN-GUARD", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guard_that_exits_three_does_not_stop_the_next()
    {
        using var tree = new FakeTree();
        var marker = Path.Combine(tree.Root, "ran.marker");
        tree.Script("guard-exit3.py", "import sys\nsys.exit(3)\n");
        tree.Script("guard-marker.py", $"open({Q(marker)}, 'w').write('ran')\n");
        tree.Registry(RegistryJson(
            Row("first-exit3", "guard-exit3.py", "ci", "gating"),
            Row("second-marker", "guard-marker.py", "ci", "gating")));

        var (exit, stdout, stderr) = RunRunner(tree.Root);

        Assert.True(exit != 0, $"exit 3 must fail the run; exit={exit}\n{stdout}\n{stderr}");
        Assert.True(File.Exists(marker), "the guard after the exit-3 one never ran");
    }

    [Fact]
    public void A_guard_that_exits_three_is_REPORTED_as_three_rather_than_collapsed_to_one()
    {
        using var tree = new FakeTree();
        tree.Script("guard-exit3.py", "import sys\nsys.exit(3)\n");
        tree.Registry(RegistryJson(Row("only-exit3", "guard-exit3.py", "ci", "gating")));

        var (_, stdout, stderr) = RunRunner(tree.Root, "--json");

        // The retired PowerShell `throw` collapsed EVERY red guard to exit 1, so a caller could not tell
        // guard 3 from guard 1. The port propagates it, and this is the assertion that keeps it: a
        // report that renders both as "1" is indistinguishable from a report that never read them.
        var text = stdout + stderr;
        var at = text.IndexOf("\"exit\": 3", StringComparison.Ordinal);
        var idAt = text.IndexOf("only-exit3", StringComparison.Ordinal);
        Assert.True(idAt >= 0 && at >= 0, $"the guard's own exit code is not in the report:\n{text}");
    }

    [Fact]
    public void A_RED_run_still_reports_every_guard_it_ran()
    {
        using var tree = new FakeTree();
        var marker = Path.Combine(tree.Root, "ran.marker");
        tree.Script("guard-red.py", "import sys\nsys.exit(1)\n");
        tree.Script("guard-marker.py", $"open({Q(marker)}, 'w').write('ran')\n");
        tree.Registry(RegistryJson(
            Row("first-red", "guard-red.py", "ci", "gating"),
            Row("second-marker", "guard-marker.py", "ci", "gating")));

        var (exit, stdout, _) = RunRunner(tree.Root, "--json");

        Assert.True(exit != 0, "a red gating guard must fail the run");
        // A differential against the retired runner caught this: the first version of the port raised on
        // the red verdict and discarded the whole result set, so a red run reported ZERO guards where
        // the PowerShell original reported all of them. The original printed its table BEFORE it threw.
        Assert.Contains("\"verdict\": \"FAILED\"", stdout, StringComparison.Ordinal);
        foreach (var id in new[] { "first-red", "second-marker" })
            Assert.Contains(id, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void CiRange_is_substituted_from_the_registry_and_dropped_with_no_parent()
    {
        using var tree = new FakeTree();
        var argsFile = Path.Combine(tree.Root, "args.txt");
        tree.Script("guard-args.py",
            $"import sys\nopen({Q(argsFile)}, 'w').write(' '.join(sys.argv[1:]))\n");
        tree.Registry(RegistryJson(Row("args", "guard-args.py", "ci", "gating",
            extra: ",\"args\":{\"ci\":[\"--range\",\"{ciRange}\"]}")));

        var (explicitExit, explicitOut, explicitErr) = RunRunner(tree.Root, "--ci-range", "HEAD~1..HEAD");
        Assert.True(explicitExit == 0, $"{explicitOut}\n{explicitErr}");
        Assert.Contains("--range HEAD~1..HEAD", File.ReadAllText(argsFile), StringComparison.Ordinal);

        // The temp root is not a git repository, so there is no parent commit: the placeholder AND its
        // switch are dropped. Keeping the switch would hand the guard `--range` and nothing after it,
        // and the failure would be an argparse error from a guard that never had a chance to run.
        var (rootExit, rootOut, rootErr) = RunRunner(tree.Root);
        Assert.True(rootExit == 0, $"{rootOut}\n{rootErr}");
        Assert.DoesNotContain("--range", File.ReadAllText(argsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void A_guard_that_writes_to_stderr_does_not_abort_the_batch()
    {
        using var tree = new FakeTree();
        var marker = Path.Combine(tree.Root, "ran.marker");
        tree.Script("guard-stderr.py",
            "import sys\nsys.stderr.write('commit-tool: warning: subject is 999 chars\\n')\n"
            + "sys.stdout.write('warned and fine\\n')\n");
        tree.Script("guard-marker.py", $"open({Q(marker)}, 'w').write('ran')\n");
        tree.Registry(RegistryJson(
            Row("first-stderr", "guard-stderr.py", "ci", "gating"),
            Row("second-marker", "guard-marker.py", "ci", "gating")));

        var (exit, stdout, stderr) = RunRunner(tree.Root);

        Assert.True(exit == 0, $"a stderr-writing guard must not abort the runner; exit={exit}\n{stdout}\n{stderr}");
        Assert.True(File.Exists(marker), "the guard after the stderr writer never ran - the batch aborted");
    }

    [Fact]
    public void A_red_guard_still_fails_the_run_and_is_named()
    {
        using var tree = new FakeTree();
        tree.Script("guard-red.py", "import sys\nsys.stderr.write('boom\\n')\nsys.exit(1)\n");
        tree.Registry(RegistryJson(Row("only-red", "guard-red.py", "ci", "gating")));

        var (exit, stdout, stderr) = RunRunner(tree.Root);

        Assert.True(exit != 0, "a red gating guard must still fail the run");
        Assert.Contains("only-red", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ps1_guard_is_still_dispatched_or_the_missing_interpreter_is_named()
    {
        using var tree = new FakeTree();
        var marker = Path.Combine(tree.Root, "ran.marker");
        tree.Script("guard-ps.ps1", $"Set-Content -LiteralPath '{marker}' -Value ran\nexit 0\n");
        tree.Registry(RegistryJson(Row("legacy", "guard-ps.ps1", "ci", "gating")));

        var (exit, stdout, stderr) = RunRunner(tree.Root);

        // A DISJUNCTION, deliberately, and it is the honest one. The registry holds only `.py` guards
        // today, so the `.ps1` branch exists while the population drains; if `pwsh` is on PATH the guard
        // must be dispatched and its marker written, and if it is not, the runner must REFUSE by name
        // rather than skip the guard quietly. A guard that silently does not run is the one outcome
        // both branches exclude, which is what makes this weaker than two separate tests and better
        // than a test that needs an interpreter the suite cannot assume.
        var text = stdout + stderr;
        if (File.Exists(marker)) return;
        Assert.Contains("POWERSHELL-NOT-ON-PATH", text, StringComparison.Ordinal);
        Assert.True(exit != 0, $"a .ps1 guard that neither ran nor refused is a silent green; exit={exit}\n{text}");
    }

    /// <summary>A C# string literal for a Python one, so a Windows path never reaches the guard raw.</summary>
    static string Q(string value) => "\"" + value.Replace("\\", "\\\\") + "\"";

    /// <summary>The disk under test: a temp root with scripts/ and a registry, deleted with a throwing
    /// delete (a failed delete is a failure, never a swallowed catch).</summary>
    sealed class FakeTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "guard-runner-" + Guid.NewGuid().ToString("N"));

        public FakeTree() => Directory.CreateDirectory(Path.Combine(Root, "scripts"));

        public void Script(string name, string body) =>
            File.WriteAllText(Path.Combine(Root, "scripts", name), body);

        public void Registry(string json) =>
            File.WriteAllText(Path.Combine(Root, "scripts", "enforcement-registry.v1.json"), json);

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"failed to delete the runner scratch dir {Root}", ex);
            }
        }
    }
}
