using System.Diagnostics;
using Xunit;

namespace FusionRpg.Core.Tests.TestSupport;

/// <summary>
/// Correct stdout/stderr draining for a redirected child process.
///
/// <para><b>The bug this exists to kill (found 2026-08-28, confirmed via `dotnet test --diag`):</b>
/// <c>Process.StandardOutput.ReadToEnd()</c> immediately followed by
/// <c>Process.StandardError.ReadToEnd()</c> is a documented deadlock hazard (Microsoft's own docs on
/// <c>Process.StandardOutput</c>): if the child writes enough to stderr to fill the OS pipe buffer
/// while the parent is still blocked draining stdout to EOF, the child stalls (buffer full, nobody
/// draining it) and the parent never reaches EOF either (it is waiting on a child that is itself
/// stalled) — a real deadlock, not a slow read. Seven tests in this project shelled out to a
/// <c>dotnet run</c> tool or a Python script and shared this exact pattern. It explained a full
/// `Core.Tests` run intermittently taking 15–45 minutes for work that itself completes in under 20
/// seconds: isolated via <c>dotnet test --diag</c>, the diagnostic log showed every test finishing
/// (and being reported) within the first ~16 seconds, then a single dead-silent multi-minute gap —
/// zero log lines, zero system-wide CPU/disk activity — before the run's final "complete" signal.
/// Whether a given run actually hit the deadlock depended on how much a spawned tool happened to
/// write to stderr that run (MSBuild/restore chatter from `dotnet run`, mostly), which is exactly
/// why it was intermittent rather than reliably reproducible.</para>
/// </summary>
public static class ExternalProcess
{
    /// <summary>
    /// Starts <paramref name="psi"/> (this method owns stream redirection — do not set
    /// <c>RedirectStandardOutput</c>/<c>RedirectStandardError</c>/<c>UseShellExecute</c> on the
    /// caller's copy) and drains stdout/stderr CONCURRENTLY, never sequentially. On timeout, kills
    /// the whole process tree (a lingering grandchild — e.g. a reused MSBuild node — must not be
    /// left running) and fails with <paramref name="timeoutMessage"/> instead of hanging forever.
    /// </summary>
    public static (int ExitCode, string Stdout, string Stderr) Run(ProcessStartInfo psi, int timeoutMs, string timeoutMessage)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var exited = p.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort — we are already failing */ }
            Assert.Fail(timeoutMessage);
        }

        // The process has exited and closed its pipes, so these should already be done or finish
        // immediately — bounded defensively rather than trusted blindly, since a still-alive
        // grandchild could in principle hold a duplicated handle open.
        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(10));

        return (
            p.ExitCode,
            stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "",
            stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "");
    }
}
