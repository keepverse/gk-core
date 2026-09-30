using System.Diagnostics;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests;

/// <summary>
/// T4.6's own CLI-level acceptance line: "a stale generated tree refuses." A real, cold run of
/// `gk-forge/tools/CreatureSpeciesImport` as a genuinely separate process, the same pattern
/// `AtomImporter.Tests`' `RealColdProcessTests.cs` already established — `SpeciesImportStoreTests.cs`
/// covers the DAL half (`RpgStore.ImportSpecies`) directly; this covers the pre-flight only the CLI
/// itself runs.
///
/// <para><b>Why the apphost and not `dotnet run` (2026-09-12):</b> this test used to launch
/// `dotnet run --no-restore --no-build`, which needs a prebuilt tool. Nothing in the build or CI ever
/// built `CreatureSpeciesImport` — it was referenced by no test project and CI had no `dotnet build`
/// step — so from a clean checkout the tool apphost did not exist and both tests failed with
/// "cannot find the file specified". It only ever passed where the tool happened to have been built
/// by hand. The test project now references the tool, so the test host's own build produces the
/// apphost beside the test dll, and the test launches it directly. That also removes the old
/// 3-attempt retry: the retry guarded an implicit-build MSB3026/MSB3027 file-lock race, and with no
/// `dotnet run` there is no build to race (the `--no-build` form could never produce that failure
/// anyway, so the retry was dead code). It remains a separate process with its own statics and
/// composition root — the cold-process property this test exists for is unchanged.</para>
/// </summary>
[Trait("Category", "DiskSemantics")]
[Trait("VerificationId", "data.species-import-cli")]
public class CreatureSpeciesImportCliTests : IDisposable
{
    readonly string _dbDir;

    public CreatureSpeciesImportCliTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "fusionrpg-species-import-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>
    /// Launches the tool apphost the test host's own build produced (see the class doc). Drains both
    /// streams asynchronously: the sequential `StandardOutput.ReadToEnd()` then
    /// `StandardError.ReadToEnd()` before `WaitForExit` that this file once used is the classic .NET
    /// process-redirection deadlock — if the child fills its stderr pipe buffer while the parent is
    /// still blocked reading stdout, both stall forever (reproduced for real 2026-09-06 in a
    /// 17-minute run that never finished). The working directory must be the repo root: the tool's own
    /// <c>FindUp("data", ...)</c> walks up from it, so the test exercises that real default.
    /// </summary>
    static (int ExitCode, string Stdout, string Stderr) Run(string repoRoot, string args)
    {
        var psi = ToolStartInfo(repoRoot, args);

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Sized from measurement, not guesswork (2026-09-12): this launches a real cold process that
        // imports the real committed tree, so its runtime scales with disk/CPU contention. Measured
        // ~3.5s isolated, but 31s and 41.5s inside a full-suite run while this machine had four
        // concurrent agent streams (CPU ~79%) — roughly a 12x inflation. The old 60s cap sat just
        // above that, so a slightly heavier run failed intermittently at full-suite scale while
        // passing in isolation. 300s keeps ~7x headroom over the measured worst case (and stays well
        // under the 10min per-test cap in test.runsettings); it still fails with a name, never hangs.
        var exited = proc.WaitForExit(300_000);
        Assert.True(exited, "CreatureSpeciesImport did not exit within 300s");
        // Drains any output still in flight after the process handle reports exited — otherwise a
        // race can read a truncated tail (WaitForExit(int) does not itself guarantee the async
        // stream callbacks have all fired yet).
        proc.WaitForExit();

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// The tool the test project references, resolved from the test's own output directory. Prefer the
    /// apphost — a true standalone executable, exactly what a deploy runs — and fall back to
    /// <c>dotnet &lt;dll&gt;</c> on hosts that do not emit one. Neither path builds.
    /// </summary>
    static ProcessStartInfo ToolStartInfo(string repoRoot, string args)
    {
        var dir = AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "CreatureSpeciesImport.exe" : "CreatureSpeciesImport");
        var dll = Path.Combine(dir, "CreatureSpeciesImport.dll");

        Assert.True(File.Exists(apphost) || File.Exists(dll),
            $"CreatureSpeciesImport was not built beside the test dll ({dir}); the test project's ProjectReference should have produced it");

        return File.Exists(apphost)
            ? new ProcessStartInfo { FileName = apphost, Arguments = args, WorkingDirectory = repoRoot, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
            : new ProcessStartInfo { FileName = "dotnet", Arguments = $"\"{dll}\" {args}", WorkingDirectory = repoRoot, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    }

    [Fact]
    public void A_real_import_against_the_real_committed_tree_succeeds_and_writes_a_real_store()
    {
        var repoRoot = FindRepoRoot();

        var (exitCode, stdout, stderr) = Run(repoRoot, $"--db \"{_dbDir}\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("written", stdout, StringComparison.Ordinal);

        var store = new RpgStore(_dbDir);
        store.Init();
        Assert.NotEmpty(store.ListSpeciesIds());
        Assert.Contains("Peashooter", store.ListSpeciesIds());
    }

    [Fact]
    public void A_stale_committed_file_refuses_the_whole_import_and_writes_nothing()
    {
        var repoRoot = FindRepoRoot();
        var realOutDir = Path.Combine(repoRoot, "data", "generated", "creatures");
        var scratchOutDir = Path.Combine(_dbDir, "stale-generated");
        Directory.CreateDirectory(scratchOutDir);

        // A committed tree that does not match what the real anchors would re-derive to — every
        // real generated file, but with a body no re-derivation could ever produce.
        foreach (var file in Directory.GetFiles(realOutDir, "*.json"))
            File.WriteAllText(Path.Combine(scratchOutDir, Path.GetFileName(file)), "{\"stale\":true}\n");

        var (exitCode, stdout, stderr) = Run(repoRoot, $"--db \"{_dbDir}\" --out \"{scratchOutDir}\"");

        Assert.Equal(1, exitCode);
        Assert.Contains("stale", stderr, StringComparison.OrdinalIgnoreCase);

        var store = new RpgStore(_dbDir);
        store.Init();
        Assert.Empty(store.ListSpeciesIds()); // the whole import refused — nothing written
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
