using System.Diagnostics;
using Xunit;

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
        var repoRoot = FindRepoRoot();
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var scripts = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(scripts)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root with Directory.Build.props");
    }
}
