using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/guard-dal.py, asserts Sqlite package ownership, and that the DAL gate is gated (Slice E).
///
/// Repointed from the retired `guard-dal.ps1` on 2026-09-26. This is the THIRD coupling a guard port
/// has, not the two the registry rows represent: the enforcement-registry row, the
/// verification-boundaries owner row, and the C# test that shells the script. The port commit fixed
/// the first two and this file kept pointing at a deleted path, so both script tests failed at
/// `File.Exists` - found by grepping for an INVOCATION rather than a mention, which is the only
/// question that matters here.
/// </summary>
public class DalGuardTests
{
    /// <summary>The ported guard. Same stem, same `--root` CLI, same verdict strings.</summary>
    private static string GuardPath(string repoRoot) =>
        Path.Combine(repoRoot, "scripts", "guard-dal.py");

    [Fact]
    public void dal_guard_is_gated_by_its_owning_phase()
    {
        var repoRoot = FindRepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(repoRoot, "dal");
    }

    [Fact]
    public void DalGuard_script_exits_zero()
    {
        var repoRoot = FindRepoRoot();
        var script = GuardPath(repoRoot);
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
        Assert.Contains("DAL GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void DalGuard_script_exits_nonzero_when_Sqlite_outside_Data()
    {
        var repoRoot = FindRepoRoot();
        var script = GuardPath(repoRoot);
        Assert.True(File.Exists(script), "missing " + script);

        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-guard-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "src", "FusionRpg.Data"));
            var badDir = Path.Combine(fixture, "src", "FusionRpg.Server");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(
                Path.Combine(badDir, "BadSql.cs"),
                "using Microsoft.Data.Sqlite;\nnamespace X { class Bad { SqliteConnection C; } }\n");

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{script}\" --root \"{fixture}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var (exit, stdout, stderr) = ExternalProcess.Run(psi, 60_000, "guard script timed out");
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            // STDERR, not stdout, and that is a real difference from the PowerShell original rather
            // than a port slip. The original emitted its failure banner with `Write-Host`, which
            // writes the INFORMATION stream and is invisible to a `2>&1` capture - the exact trap
            // this whole migration exists to remove. It reached `stdout` here only because the
            // redirection around a host-stream write is unreliable. The port puts a failure verdict
            // on stderr where it belongs and the OK verdict on stdout, so a caller can branch on
            // the stream instead of scraping text.
            Assert.Contains("DAL GUARD FAILED", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("DAL GUARD OK", stdout, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Server_csproj_has_no_Sqlite_PackageReference()
    {
        var repoRoot = FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "FusionRpg.Server", "FusionRpg.Server.csproj");
        Assert.True(File.Exists(csproj), "missing " + csproj);
        var text = File.ReadAllText(csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Data_csproj_references_Sqlite()
    {
        var repoRoot = FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "FusionRpg.Data", "FusionRpg.Data.csproj");
        Assert.True(File.Exists(csproj), "missing " + csproj);
        var text = File.ReadAllText(csproj);
        Assert.Contains("Microsoft.Data.Sqlite", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Server_csproj_ProjectReferences_Data()
    {
        var repoRoot = FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "FusionRpg.Server", "FusionRpg.Server.csproj");
        Assert.True(File.Exists(csproj), "missing " + csproj);
        var text = File.ReadAllText(csproj);
        Assert.Contains("FusionRpg.Data", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProjectReference", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Injector_and_Core_csproj_have_no_Sqlite()
    {
        var repoRoot = FindRepoRoot();
        foreach (var rel in new[]
                 {
                     Path.Combine("src", "FusionRpg.Injector", "FusionRpg.Injector.csproj"),
                     Path.Combine("src", "FusionRpg.Core", "FusionRpg.Core.csproj")
                 })
        {
            var csproj = Path.Combine(repoRoot, rel);
            Assert.True(File.Exists(csproj), "missing " + csproj);
            var text = File.ReadAllText(csproj);
            Assert.DoesNotContain("Microsoft.Data.Sqlite", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sqlite", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
