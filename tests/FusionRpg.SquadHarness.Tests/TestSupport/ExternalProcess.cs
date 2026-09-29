using System.Diagnostics;
using Xunit;

namespace FusionRpg.SquadHarness.Tests.TestSupport;

/// <summary>
/// Correct stdout/stderr draining for a redirected child process -- copied verbatim from
/// <c>tests/FusionRpg.Core.Tests/TestSupport/ExternalProcess.cs</c> rather than referenced, since test
/// projects in this repo do not reference each other. See that file's own doc comment for the
/// deadlock this exists to kill (<c>Process.StandardOutput.ReadToEnd()</c> immediately followed by
/// <c>Process.StandardError.ReadToEnd()</c> is a documented hazard when the child fills the stderr
/// pipe buffer while the parent still drains stdout).
/// </summary>
public static class ExternalProcess
{
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
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort -- we are already failing */ }
            Assert.Fail(timeoutMessage);
        }

        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(10));

        return (
            p.ExitCode,
            stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "",
            stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "");
    }
}
