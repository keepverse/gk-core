using System.Text.Json;
using FusionRpg.Core.Tests.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P8.6/P8.7 — <c>gk-core/tools/ResidualFitLoop</c> drives the real
/// run→emitted-metrics→aggregate→fit→publish chain as one command, no human step. Runs the real
/// `dotnet run` invocation (same cold-start-fixture pattern as <c>ProveAptitudeJsonEmitTests</c>) rather
/// than re-implementing the tool's own logic in the test — this proves the SHIPPED tool, not a copy of
/// its intent.</summary>
public class ResidualFitLoopTests
{
    [Fact]
    public void DefaultInvocation_onTheLiveShippedConfig_findsNothingToFit()
    {
        // The real, safe, no-args production shape (P8.6's own "no human step" invocation). v2 already
        // contains P8.2/P8.3's own published fix, so a correct loop must find zero changes and never
        // reach publish.py at all — the strongest possible proof the loop does not silently touch
        // gk-core/data/tuning/ when there is nothing to fix.
        var (exit, stdout, stderr) = Run("");
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("0 change(s) computed", stdout, StringComparison.Ordinal);
        Assert.Contains("nothing to publish", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_isDeterministic_identicalInputProducesIdenticalComputedChanges()
    {
        // P8.6's own verify line: "re-running on identical input republishes an identical file." Two
        // separate --dry-run invocations against the SAME (historical, v1) input must compute the
        // exact same fit, byte for byte — no randomness anywhere in the pipeline.
        var (exit1, stdout1, _) = Run("--dry-run --input data/tuning/aptitudes.v1.json");
        var (exit2, stdout2, _) = Run("--dry-run --input data/tuning/aptitudes.v1.json");
        Assert.Equal(0, exit1);
        Assert.Equal(0, exit2);
        Assert.Equal(ExtractFitBlock(stdout1), ExtractFitBlock(stdout2));
    }

    [Fact]
    public void Run_againstV1_reproducesP82sOwnStaminaFit_notJustNoOp()
    {
        // Proves the fit ALGORITHM itself is correct, not merely that it is a no-op on an
        // already-fixed file: against the known-broken v1 config, it must independently recompute a
        // real cut to Vigor/Agility's own resource.regen.stamina kMilli — the same channel/sources
        // P8.2 fixed by hand — not some other, wrong pair.
        var (exit, stdout, stderr) = Run("--dry-run --input data/tuning/aptitudes.v1.json");
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("2 change(s) computed", stdout, StringComparison.Ordinal);
        Assert.Contains("channel=resource.regen.stamina,source=Vigor", stdout, StringComparison.Ordinal);
        Assert.Contains("channel=resource.regen.stamina,source=Agility", stdout, StringComparison.Ordinal);
        Assert.Contains("30", stdout, StringComparison.Ordinal); // the aggregate step's own violation count.
    }

    [Fact]
    public void PlantedReservedCoefficient_isRefusedAndSaidSo_neverFit()
    {
        // class-system-todo.md P8.7: "a planted run with a reserved coefficient must leave that
        // coefficient unchanged, and say so in the emitted report." The tool's own --plant-reserved-
        // coefficient flag plants resource.efficiency.qi (a real, currently reader-less family per
        // _meta.measurable) as a synthetic fit target and must refuse it, not silently skip it.
        var (exit, stdout, stderr) = Run("--dry-run --plant-reserved-coefficient");
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("REFUSED", stdout, StringComparison.Ordinal);
        Assert.Contains("resource.efficiency.qi", stdout, StringComparison.Ordinal);
        Assert.Contains("1 refused (reserved)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishAttempt_withMismatchedInput_isRefused_notSilentlyAppliedToTheLiveDomain()
    {
        // A real incident during this tool's own development (docs/research/class-residual-
        // 2026-08-27.md, P8.6 section): running with --input pointing at a historical file still
        // published against the REAL, live aptitudes domain, silently bumping it with numbers computed
        // from the wrong input. Caught and reverted the same turn; this test is the permanent
        // regression proof the fix holds. Deliberately NOT --dry-run — this is the exact call shape
        // that caused the incident, and the guard must stop it before it ever reaches publish.py, not
        // merely be documented as something to remember to avoid.
        var (exit, stdout, _) = Run("--input data/tuning/aptitudes.v1.json");
        Assert.NotEqual(0, exit);
        Assert.Contains("REFUSED", stdout, StringComparison.Ordinal);
        Assert.Contains("not the 'aptitudes' domain's own current file", stdout, StringComparison.Ordinal);

        // The real domain must be untouched — no v3 (or any version past the live one) may exist.
        var repoRoot = FindRepoRoot();
        var tuningDir = Path.Combine(repoRoot, "data", "tuning");
        var liveVersion = LiveAptitudesVersion(tuningDir);
        Assert.False(File.Exists(Path.Combine(tuningDir, $"aptitudes.v{liveVersion + 1}.json")),
            "a mismatched --input must never cause a new version to be published against the real domain");
    }

    [Fact]
    public void FullChain_onAThrowawayDomain_actuallyPublishes_andTheResultParsesAndBinds()
    {
        // The literal P8.6 acceptance line: "the chain runs on simulated runs and produces a published
        // v{n+1} whose coefficients differ from v{n}." Run against a THROWAWAY domain copy (never the
        // real `aptitudes` domain — the previous test's own incident is exactly why), so this proves
        // the full run -> metrics -> aggregate -> fit -> publish chain actually reaches publish.py and
        // writes a real, valid, improved file, without any risk to the live shipped config.
        // --domain lets this test drive the TOOL's OWN publish step (not a hand-rolled equivalent)
        // against a disposable name that can never collide with the real `aptitudes` domain — the
        // exact incident this pass's own research note records (docs/research/class-residual-
        // 2026-08-27.md, P8.6 section) happened precisely because an earlier draft had no such
        // parameter and --input alone did not also redirect the publish step.
        //
        // solid-enforcement `tuning-immutability` (SE2.2): the throwaway copy now lives in a TEMP
        // directory, never the tracked gk-core/data/tuning/ (the 2026-09-12 incident this module exists to
        // close: two of this file's own tests committed four loopwarntest*/loopchaintest* files into
        // the real balance surface via exactly this pattern). `--tuning-dir` (this tool's own new
        // flag) is what makes the publish step ALSO write there instead of the real directory — the
        // disk itself is the thing under test, so a temp directory is legitimate; a swallowed delete
        // failure is not (docs/contributing/testing-standard.md).
        var repoRoot = FindRepoRoot();
        var tempDir = Directory.CreateTempSubdirectory("fusionrpg-residualfit-").FullName;
        var domain = "loopchaintest" + Guid.NewGuid().ToString("N")[..8];
        var v1Path = Path.Combine(tempDir, $"{domain}.v1.json");
        var v2Path = Path.Combine(tempDir, $"{domain}.v2.json");
        File.Copy(Path.Combine(repoRoot, "data", "tuning", "aptitudes.v1.json"), v1Path);
        try
        {
            var (exit, stdout, stderr) = Run($"--domain {domain} --input \"{v1Path}\" --tuning-dir \"{tempDir}\" --label \"test throwaway domain\"");
            Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
            Assert.Contains("2 change(s) computed", stdout, StringComparison.Ordinal);
            Assert.Contains("== post-publish verification ==", stdout, StringComparison.Ordinal);
            Assert.True(File.Exists(v2Path), "the tool's own publish step should have written v2 for the throwaway domain, in the temp tuning dir");

            using var doc = JsonDocument.Parse(File.ReadAllText(v2Path));
            Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
            var vigorRegen = doc.RootElement.GetProperty("edges").EnumerateArray()
                .First(e => e.TryGetProperty("channel", out var c) && c.GetString() == "resource.regen.stamina"
                            && e.TryGetProperty("source", out var s) && s.GetString() == "Vigor")
                .GetProperty("kMilli").GetInt64();
            Assert.Equal(1075, vigorRegen);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true); // checked -- a failed cleanup fails the test, never a swallowed catch.
        }
    }

    [Fact]
    public void PostPublishVerification_warnsHonestly_whenTheFitDidNotFullyCloseTermination()
    {
        // The loop's own fit only implements Pattern A (proportional-target, P8.2's own stamina-style
        // fix) -- Pattern B (the guarded search P8.3's own termination-violation fix needed) is
        // deliberately not attempted (see the tool's own comment). Publishing v1's stamina fix ALONE,
        // without the mitigation-dial fix, therefore leaves the 30 known termination violations open —
        // this test proves the post-publish check catches and reports that honestly rather than
        // reporting a false "published successfully" with no further comment. Same temp-dir,
        // checked-delete safety as the test above (SE2.2).
        var repoRoot = FindRepoRoot();
        var tempDir = Directory.CreateTempSubdirectory("fusionrpg-residualfit-").FullName;
        var domain = "loopwarntest" + Guid.NewGuid().ToString("N")[..8];
        var v1Path = Path.Combine(tempDir, $"{domain}.v1.json");
        var v2Path = Path.Combine(tempDir, $"{domain}.v2.json");
        File.Copy(Path.Combine(repoRoot, "data", "tuning", "aptitudes.v1.json"), v1Path);
        try
        {
            var (exit, stdout, stderr) = Run($"--domain {domain} --input \"{v1Path}\" --tuning-dir \"{tempDir}\" --label \"test warns on incomplete fit\"");
            Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}"); // publish.py itself succeeded; the WARNING is a separate signal, not a process failure.
            Assert.Contains("WARNING:", stdout, StringComparison.Ordinal);
            Assert.Contains("did not actually satisfy the termination invariant", stdout, StringComparison.Ordinal);
            Assert.True(File.Exists(v2Path), "publish must still have happened -- the warning informs, it does not silently roll back a T4-governed publish");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true); // checked -- a failed cleanup fails the test, never a swallowed catch.
        }
    }

    static string ExtractFitBlock(string stdout)
    {
        var start = stdout.IndexOf("== fit ==", StringComparison.Ordinal);
        var end = stdout.IndexOf("== publish ==", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "could not locate the fit block in ResidualFitLoop's own output");
        return stdout[start..end];
    }

    static int LiveAptitudesVersion(string tuningDir)
    {
        var max = 0;
        foreach (var f in Directory.EnumerateFiles(tuningDir, "aptitudes.v*.json"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^aptitudes\.v(\d+)\.json$");
            if (m.Success) max = Math.Max(max, int.Parse(m.Groups[1].Value));
        }
        return max;
    }

    static (int Exit, string Stdout, string Stderr) Run(string args)
    {
        var repoRoot = FindRepoRoot();
        // ProjectReference + apphost (see ToolProcess) — no implicit build, no stale Release output.
        return ToolProcess.Run(repoRoot, "ResidualFitLoop", args, 120_000);
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
