using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// No test may drain a redirected child process with sequential
/// <c>StandardOutput.ReadToEnd()</c> then <c>StandardError.ReadToEnd()</c>. That is a documented
/// pipe deadlock: if the child fills the stderr pipe buffer while the parent is still blocked
/// draining stdout to EOF, both stall, and any later <c>WaitForExit(ms)</c> is unreachable because
/// the code never gets there. The fix is the concurrent-drain
/// <c>TestSupport/ExternalProcess.Run</c> helper (or <c>OutputDataReceived</c>/<c>ErrorDataReceived</c>).
///
/// <para><b>Why a guard, not just the fix:</b> the same pattern was fixed once before (the
/// Core.Tests helper's own doc comment records a 15–45 minute hang) and still reappeared across
/// ~18 sites. A local run had no per-test timeout, so the symptom was silence — no output, no exit
/// code, no report — and an agent tool call blocked for 1h30. This test makes the pattern a
/// build-time fact rather than a doc comment nobody re-reads. Comment and doc lines are ignored;
/// only live code counts.</para>
/// </summary>
public class SubprocessPipeDrainGuardTests
{
    [Fact]
    public void No_test_file_reads_stdout_then_stderr_synchronously()
    {
        var testsRoot = Path.Combine(FindRepoRoot(), "tests");
        Assert.True(Directory.Exists(testsRoot), "missing " + testsRoot);

        // Built from parts so this guard's own file never literally contains the sequence it hunts.
        var stdoutRead = "Standard" + "Output.ReadToEnd()";
        var stderrRead = "Standard" + "Error.ReadToEnd()";
        var commentPrefix = "//";
        var docPrefix = "*";

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains(stdoutRead, StringComparison.Ordinal)) continue;

            // Ignore comment/doc lines: the hazard is live code, and helper doc comments quote the
            // pattern they exist to kill.
            var hasLiveStdoutRead = text.Split('\n').Any(line =>
            {
                var t = line.TrimStart();
                if (t.StartsWith(commentPrefix, StringComparison.Ordinal) || t.StartsWith(docPrefix, StringComparison.Ordinal))
                    return false;
                return t.Contains(stdoutRead, StringComparison.Ordinal);
            });
            if (!hasLiveStdoutRead) continue;

            if (text.Contains(stderrRead, StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(FindRepoRoot(), file));
        }

        Assert.True(offenders.Count == 0,
            "sequential stdout-then-stderr ReadToEnd() is a pipe deadlock and makes WaitForExit " +
            "unreachable; use TestSupport/ExternalProcess.Run (concurrent drain) instead:\n  " +
            string.Join("\n  ", offenders));
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
