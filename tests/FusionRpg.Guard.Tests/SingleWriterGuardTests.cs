using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-fusion/scripts/guard-single-writer.py under dotnet test so CI/local test runs
/// enforce the EntityStatWriter-only combat field invariant.
/// </summary>
public class SingleWriterGuardTests
{
    [Fact]
    public void Guard_script_exits_zero()
    {
        // The guard scans `<root>/src/FusionRpg.Injector`, which is gk-FUSION's, so `--root` must be
        // gk-fusion. Against gk-core it refused with exit 64 and
        // `SINGLE-WRITER GUARD REFUSED MISSING_INJECTOR: ...\gk-core\src\FusionRpg.Inject` — a named
        // refusal, correctly, because the tree it was asked to scan does not exist there. The script
        // path was already routed and the root was not, which is the half-migrated shape again.
        var repoRoot = KeepverseRoots.Fusion();
        var script = Path.Combine(repoRoot, "scripts", "guard-single-writer.py");
        Assert.True(File.Exists(script), "missing " + script);

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{repoRoot}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var (exit, stdout, stderr) = ExternalProcess.Run(psi, 60_000, "guard script timed out");
        Assert.True(exit == 0,
            $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("SINGLE-WRITER GUARD OK", stdout, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
