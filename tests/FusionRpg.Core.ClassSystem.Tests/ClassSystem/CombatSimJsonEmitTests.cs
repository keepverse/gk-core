using System.Text.Json;
using FusionRpg.Core.Tests.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md V1 — CombatSim's `predict`/`trinity`/`marginal --json` emit
/// schema-valid documents with every documented key present. Runs the real `dotnet run` invocation
/// once per document (shared fixture — three cold `dotnet run` calls is already the expensive part,
/// no reason to pay it per assertion) and asserts the key set, not just "it parsed".</summary>
[Trait("VerificationId", "core.combat-sim")]
public class CombatSimJsonEmitTests : IClassFixture<CombatSimJsonEmitTests.Fixture>
{
    readonly Fixture _fx;
    public CombatSimJsonEmitTests(Fixture fx) => _fx = fx;

    [Fact]
    public void Predict_emitsDocumentedKeys()
    {
        Assert.True(_fx.PredictExit == 0, $"predict --json failed:\n{_fx.PredictStdout}\n{_fx.PredictStderr}");
        var root = _fx.PredictDoc!.RootElement;
        foreach (var key in new[] { "model", "modelDescription", "thetas", "archetypes", "verified", "arrows", "unendingCount" })
            Assert.True(root.TryGetProperty(key, out _), $"predict document missing key '{key}'");

        var arrows = root.GetProperty("arrows");
        Assert.True(arrows.GetArrayLength() > 0, "predict emitted zero arrows");
        var arrow = arrows[0];
        foreach (var key in new[] { "theta", "attacker", "defender", "predictedWinShareA", "roundsA", "roundsB", "netAttritionA", "netAttritionB", "neverEnds" })
            Assert.True(arrow.TryGetProperty(key, out _), $"predict arrow missing key '{key}'");

        Assert.True(root.TryGetProperty("residual", out var residual));
        foreach (var key in new[] { "mean", "max", "count" })
            Assert.True(residual.TryGetProperty(key, out _), $"predict residual summary missing key '{key}'");
    }

    [Fact]
    public void Trinity_emitsTwelveByTwelveMatrixAndCoverage()
    {
        Assert.True(_fx.TrinityExit == 0, $"trinity --json failed:\n{_fx.TrinityStdout}\n{_fx.TrinityStderr}");
        var root = _fx.TrinityDoc!.RootElement;
        foreach (var key in new[] { "model", "theta", "chains", "dominanceMatrix", "dominantCorners", "coverage" })
            Assert.True(root.TryGetProperty(key, out _), $"trinity document missing key '{key}'");

        var matrix = root.GetProperty("dominanceMatrix");
        var names = matrix.GetProperty("names");
        var wins = matrix.GetProperty("wins");
        var unending = matrix.GetProperty("unending");
        Assert.Equal(12, names.GetArrayLength());
        Assert.Equal(12, wins.GetArrayLength());
        Assert.Equal(12, unending.GetArrayLength());
        Assert.Equal(12, wins[0].GetArrayLength());
        Assert.Equal(12, unending[0].GetArrayLength());

        var coverage = root.GetProperty("coverage");
        foreach (var key in new[] { "elementAxis", "actionsActive", "reservedFamilies" })
            Assert.True(coverage.TryGetProperty(key, out _), $"trinity coverage block missing key '{key}'");
    }

    [Fact]
    public void Marginal_emitsTwelveByNGradient()
    {
        Assert.True(_fx.MarginalExit == 0, $"marginal --json failed:\n{_fx.MarginalStdout}\n{_fx.MarginalStderr}");
        var root = _fx.MarginalDoc!.RootElement;
        foreach (var key in new[] { "model", "theta", "subjects" })
            Assert.True(root.TryGetProperty(key, out _), $"marginal document missing key '{key}'");

        var subjects = root.GetProperty("subjects");
        Assert.True(subjects.GetArrayLength() > 0, "marginal emitted zero subjects");
        var subject = subjects[0];
        foreach (var key in new[] { "subject", "opponents", "rows" })
            Assert.True(subject.TryGetProperty(key, out _), $"marginal subject missing key '{key}'");

        var rows = subject.GetProperty("rows");
        Assert.Equal(12, rows.GetArrayLength()); // twelve aptitudes -> twelve gradient rows
        var row = rows[0];
        foreach (var key in new[] { "aptitude", "currentPoints", "deltaWinPerOpponent", "best", "worst", "spread", "mandatory", "dead" })
            Assert.True(row.TryGetProperty(key, out _), $"marginal row missing key '{key}'");
    }

    public sealed class Fixture : IDisposable
    {
        public int PredictExit, TrinityExit, MarginalExit;
        public string PredictStdout = "", PredictStderr = "", TrinityStdout = "", TrinityStderr = "", MarginalStdout = "", MarginalStderr = "";
        public JsonDocument? PredictDoc, TrinityDoc, MarginalDoc;

        readonly List<string> _tempFiles = new();

        public Fixture()
        {
            var repoRoot = FindRepoRoot();
            var predictPath = TempFile();
            (PredictExit, PredictStdout, PredictStderr) = RunCombatSim(repoRoot,
                $"predict --json --out \"{predictPath}\" --archetypes force,finesse,bastion --theta 100 --seed 8888");
            if (PredictExit == 0) PredictDoc = JsonDocument.Parse(File.ReadAllText(predictPath));

            var trinityPath = TempFile();
            (TrinityExit, TrinityStdout, TrinityStderr) = RunCombatSim(repoRoot, $"trinity --json --out \"{trinityPath}\" --seed 20260826");
            if (TrinityExit == 0) TrinityDoc = JsonDocument.Parse(File.ReadAllText(trinityPath));

            var marginalPath = TempFile();
            (MarginalExit, MarginalStdout, MarginalStderr) = RunCombatSim(repoRoot, $"marginal --json --out \"{marginalPath}\" --theta 100");
            if (MarginalExit == 0) MarginalDoc = JsonDocument.Parse(File.ReadAllText(marginalPath));
        }

        string TempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "fusionrpg-combatsim-schema-" + Guid.NewGuid().ToString("N") + ".json");
            _tempFiles.Add(path);
            return path;
        }

        static (int Exit, string Stdout, string Stderr) RunCombatSim(string repoRoot, string args)
        {
            // The old `dotnet run -c Release --no-build` was a workaround, not a speed tweak: without
            // it the child rebuilt CombatSim (which references Core) while the parent `dotnet test`
            // still held Core's output, so the child died with CS2012 (file locked by VBCSCompiler) —
            // red inside a full-suite run, green alone. The apphost pattern removes the problem at the
            // root: CombatSim is a ProjectReference of this test project, so this test host's own build
            // produces CombatSim.exe beside the test dll and the test launches it directly — no child
            // build to race, and no reliance on a pre-existing Release output that a clean clone lacks.
            return ToolProcess.Run(repoRoot, "CombatSim", args, 120_000);
        }

        public void Dispose()
        {
            PredictDoc?.Dispose();
            TrinityDoc?.Dispose();
            MarginalDoc?.Dispose();
            // solid-enforcement tuning-immutability SE2.3: a failed temp-delete is a failure, never
            // a swallowed catch -- every entry in _tempFiles is this fixture's own Path.GetTempPath()
            // scratch file.
            foreach (var f in _tempFiles) File.Delete(f);
        }
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
