using System.Diagnostics;
using Xunit;

namespace FusionRpg.Core.Tests.TestSupport;

/// <summary>
/// Launches a `tools/` cold-process fixture as a real separate process WITHOUT an implicit build.
///
/// <para><b>Why not <c>dotnet run</c> (2026-09-12):</b> `dotnet run --no-restore --no-build -c Release`
/// assumes a Release binary already exists, but nothing ever built these tools — their `bin/` folders
/// are untracked and the test project did not reference them, so a clean clone or CI either ran a
/// stale binary or found none. A stale <c>FusionRpg.Core</c> embedded in a tool's Release output is
/// how the already-renamed <c>demonType</c> survived into a passing-looking test run and made the
/// Balance fixtures throw <c>missing required key 'demonType'</c>. The tool is now a
/// <c>ProjectReference</c> of this test project, so the test host's own build produces its apphost;
/// this helper launches that directly. No build, no configuration mismatch (a Debug `dotnet test`
/// builds Debug tools, not the Release ones an implicit <c>-c Release</c> run would need), no stale
/// output. Mirrors the convention FusionRpg.AtomImporter.Tests established for the same reason.</para>
/// </summary>
public static class ToolProcess
{
    /// <summary>
    /// Resolve the referenced tool's executable from the test's output directory. Prefer the apphost
    /// (a true standalone exe) and fall back to <c>dotnet &lt;dll&gt;</c> where none is emitted.
    /// </summary>
    public static ProcessStartInfo StartInfo(string repoRoot, string toolName, string arguments)
    {
        var dir = AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? toolName + ".exe" : toolName);
        var dll = Path.Combine(dir, toolName + ".dll");

        Assert.True(File.Exists(apphost) || File.Exists(dll),
            $"{toolName} was not built beside the test dll ({dir}); the test project's ProjectReference should have produced it");

        return File.Exists(apphost)
            ? new ProcessStartInfo
            {
                FileName = apphost,
                Arguments = arguments,
                WorkingDirectory = repoRoot,
                CreateNoWindow = true,
            }
            : new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dll}\" {arguments}",
                WorkingDirectory = repoRoot,
                CreateNoWindow = true,
            };
    }

    /// <summary>Run the tool and return its exit/stdout/stderr with concurrent draining and a timeout.</summary>
    public static (int ExitCode, string Stdout, string Stderr) Run(
        string repoRoot, string toolName, string arguments, int timeoutMs)
    {
        var psi = StartInfo(repoRoot, toolName, arguments);
        return ExternalProcess.Run(psi, timeoutMs, $"{toolName} invocation timed out");
    }
}
