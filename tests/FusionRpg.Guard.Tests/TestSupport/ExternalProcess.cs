using System.Diagnostics;
using System.Text;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Correct stdout/stderr draining for a redirected child process. Copied verbatim from
/// <c>tests/FusionRpg.Core.Tests/TestSupport/ExternalProcess.cs</c> rather than referenced, since
/// test projects in this repo do not reference each other.
///
/// <para><b>The bug this exists to kill:</b> <c>Process.StandardOutput.ReadToEnd()</c> immediately
/// followed by <c>Process.StandardError.ReadToEnd()</c> is a documented deadlock hazard: if the child
/// writes enough to stderr to fill the OS pipe buffer while the parent is still blocked draining
/// stdout to EOF, the child stalls and the parent never reaches EOF either. A later
/// <c>WaitForExit(60_000)</c> can never fire because the code is blocked on the read before it —
/// which is exactly why a hung guard-script test produced no output, no exit code, and no report
/// (observed: a local run blocking for 1h30). This helper drains both streams CONCURRENTLY and, on
/// timeout, kills the whole process tree and fails instead of hanging forever.</para>
/// </summary>
public static class ExternalProcess
{
    /// <summary>
    /// Starts <paramref name="psi"/> (this method owns stream redirection — do not rely on the
    /// caller's <c>RedirectStandardOutput</c>/<c>RedirectStandardError</c>) and drains stdout/stderr
    /// concurrently. On timeout, kills the whole process tree and fails with
    /// <paramref name="timeoutMessage"/>.
    /// </summary>
    public static (int Exit, string Stdout, string Stderr) Run(ProcessStartInfo psi, int timeoutMs, string timeoutMessage)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi)!;

        // A drain must never be abandoned silently. `IsCompletedSuccessfully ? Result : ""` turned a
        // pipe that had not finished into a CONFIDENT EMPTY STRING, so a guard that refused correctly
        // (exit != 0, message on the pipe) read as "printed nothing" and the calling test failed on a
        // missing substring instead of the real one (observed 2026-09-25: VerificationTopologyTests
        // .Explicit_ci_range_is_mandatory_in_both_range_consumers failed at 12 s under full-suite load
        // while passing at 6 s alone; the child exits in ~0.5 s and does print the message).
        //
        // The drain is therefore waited out with the READER'S OWN budget, not a fixed 10 s, and an
        // unfinished drain FAILS the test by name instead of yielding partial text.
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        var stdoutTask = Drain(p.StandardOutput, stdoutBuffer);
        var stderrTask = Drain(p.StandardError, stderrBuffer);

        var exited = p.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort — we are already failing */ }
            Assert.Fail(timeoutMessage);
        }

        // The process has exited and closed its pipes, so these complete immediately in the normal
        // case. A grandchild holding a duplicated handle is the pathological case, and it is now a
        // named failure: a test that asserts on child output cannot be judged from a partial capture.
        if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(60)))
        {
            Assert.Fail(
                $"{timeoutMessage}: the child exited but its output pipes did not close within 60 s " +
                "(a grandchild may hold a duplicated handle). stdout so far: [" +
                Truncate(stdoutBuffer.ToString()) + "] stderr so far: [" + Truncate(stderrBuffer.ToString()) + "]");
        }

        return (p.ExitCode, stdoutBuffer.ToString(), stderrBuffer.ToString());
    }

    static async Task Drain(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            sink.Append(buffer, 0, read);
    }

    static string Truncate(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";
}
