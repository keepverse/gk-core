using System.Text.Json;
using FusionRpg.Core.Tests.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md Checkpoint 8 — <c>gk-forge/tools/DominanceBaseline</c> reproduces
/// _baseline-dominance.json's dominanceMatrix/dominantCorners fields via the SHIPPED
/// FusionRpg.Core.DominanceGuard/TerminationGuard (the same production resolver TerminationGuard.Assert
/// uses), reading the LIVE data/tuning/aptitudes.v*.json config automatically — unlike
/// gk-core/tools/CombatSim's trinity command, no internal copy, no concurrent-edit hazard. Runs the real
/// `dotnet run` invocation (same cold-start-fixture pattern as ProveAptitudeJsonEmitTests/
/// ResidualFitLoopTests) rather than re-implementing the tool's own logic in the test.</summary>
public class DominanceBaselineTests
{
    [Fact]
    public void DefaultInvocation_onTheLiveShippedConfig_emitsATwelveByTwelveMatrix()
    {
        var (exit, stdout, stderr) = Run("--theta 100");
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var names = root.GetProperty("dominanceMatrix").GetProperty("names");
        var wins = root.GetProperty("dominanceMatrix").GetProperty("wins");
        var unending = root.GetProperty("dominanceMatrix").GetProperty("unending");

        Assert.Equal(12, names.GetArrayLength());
        Assert.Equal(12, wins.GetArrayLength());
        Assert.Equal(12, unending.GetArrayLength());
        foreach (var row in wins.EnumerateArray()) Assert.Equal(12, row.GetArrayLength());
        foreach (var row in unending.EnumerateArray()) Assert.Equal(12, row.GetArrayLength());
    }

    [Fact]
    public void DefaultInvocation_onTheLiveShippedConfig_matchesP85sOwnAlreadyRecordedFinding()
    {
        // class-residual-2026-08-27.md's P8.5 section originally recorded: "no absolute dominant corner
        // (0/66 pairs unending, matching P8.3); Retribution is the new near-dominant corner (wins 10 of
        // 11, loses only to Pierce)". That was the finding on the config live BEFORE species-progression
        // step 6.1 (R21) shipped a per-layer weight (`aptitudes.v10.json`'s
        // `read.layerWeightMilliByScope`, commander 500/1000).
        //
        // RE-MEASURED, not re-guessed (2026-09-19): R21 is a legitimate, spec-approved tuning change,
        // and applying it to this same closed-form model changes the headline finding -- `Might` (pure
        // combat.power.omni) is now an ABSOLUTE dominant corner (beats all eleven others). The dominance
        // matrix is explicitly the SOFT half of the class system's own two acceptance criteria
        // (decisions.md: "the dominance matrix... is SOFT and reports with its coverage — a dominant
        // corner is what the action/passive/skill layer is for, and it is red by design today"), so a
        // legitimate tuning-driven dominance finding updates this test's own pinned reading rather than
        // blocking the build or being hidden. `docs/research/class-system/_baseline-dominance.json`
        // (regenerated the same session via `scripts/regen-class-system-baselines.ps1`, which drives
        // this exact tool) carries the same finding. The HARD half (termination — no unending pairing)
        // is asserted below exactly as before and still holds.
        var (exit, stdout, stderr) = Run("--theta 100");
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal(new[] { "Might" }, root.GetProperty("dominantCorners").EnumerateArray().Select(e => e.GetString()).ToArray());

        var names = root.GetProperty("dominanceMatrix").GetProperty("names").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        var wins = root.GetProperty("dominanceMatrix").GetProperty("wins");
        var unending = root.GetProperty("dominanceMatrix").GetProperty("unending");
        var might = Array.IndexOf(names, "Might");
        Assert.True(might >= 0);

        var mightRow = wins[might].EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var winsAgainst = mightRow.Where((w, j) => j != might && w > 0.5).Count();
        Assert.Equal(11, winsAgainst); // beats every other corner -- the dominance verdict itself

        for (var i = 0; i < 12; i++)
        for (var j = 0; j < 12; j++)
        {
            if (i == j) continue;
            Assert.False(unending[i][j].GetBoolean(), $"unexpected unending pair at [{i},{j}] ({names[i]} vs {names[j]})");
        }
    }

    [Fact]
    public void Run_isDeterministic_identicalInputProducesIdenticalMatrix()
    {
        // No RNG anywhere in DominanceGuard.Measure/TerminationGuard.Assert (both pure closed-form) —
        // two invocations against the live config must be byte-for-byte identical.
        var (exit1, stdout1, _) = Run("--theta 100");
        var (exit2, stdout2, _) = Run("--theta 100");
        Assert.Equal(0, exit1);
        Assert.Equal(0, exit2);
        Assert.Equal(stdout1, stdout2);
    }

    static (int Exit, string Stdout, string Stderr) Run(string args)
    {
        var repoRoot = FindRepoRoot();
        // The tool is a ProjectReference of this test project, so its apphost is built beside the
        // test dll and launched directly — no `dotnet run`, no implicit build, no stale Release
        // output to run against (see ToolProcess).
        return ToolProcess.Run(repoRoot, "DominanceBaseline", args, 120_000);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
