using System.Text.Json;
using FusionRpg.Core.Tests.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P2.6 / V3 — `gk-core/tools/ProveAptitude` drives a real resolve on both
/// engines (overlay `DerivedComposer`, battle `BattleHubCompose` since battle-hub-fuse T6) for the
/// same allocation/Theta and asserts they agree. Runs the real `dotnet run` invocation (shared
/// fixture, same pattern as <see cref="CombatSimJsonEmitTests"/> — a cold `dotnet run` is the
/// expensive part).</summary>
public class ProveAptitudeJsonEmitTests : IClassFixture<ProveAptitudeJsonEmitTests.Fixture>
{
    readonly Fixture _fx;
    public ProveAptitudeJsonEmitTests(Fixture fx) => _fx = fx;

    [Fact]
    public void BothComposersAgree_mightToCombatPowerOmni_checkpointTwoScope()
    {
        // The default invocation IS Checkpoint 2's own proof: `.\scripts\prove-aptitude.ps1` with no
        // args runs exactly this. Exit 0, Pass=true, zero delta.
        Assert.True(_fx.DefaultExit == 0, $"prove-aptitude (default) failed:\n{_fx.DefaultStdout}\n{_fx.DefaultStderr}");
        var root = _fx.DefaultDoc!.RootElement;
        Assert.True(root.GetProperty("Pass").GetBoolean());

        var perChannel = root.GetProperty("PerChannel").GetProperty("combat.power.omni");
        var overlay = perChannel.GetProperty("Overlay").GetDouble();
        var battle = perChannel.GetProperty("Battle").GetDouble();
        Assert.Equal(overlay, battle, 6);
        Assert.True(overlay > 0, "Might funded at 100/100 points should produce a positive combat.power.omni contribution");

        var delta = root.GetProperty("Deltas").GetProperty("combat.power.omni").GetDouble();
        Assert.Equal(0.0, delta, 9);
    }

    [Fact]
    public void DocumentSchema_carriesEveryDocumentedKey()
    {
        Assert.True(_fx.DefaultExit == 0);
        var root = _fx.DefaultDoc!.RootElement;
        foreach (var key in new[] { "Theta", "Source", "Points", "PerChannel", "Deltas", "Pass" })
            Assert.True(root.TryGetProperty(key, out _), $"prove-aptitude document missing key '{key}'");
    }

    [Fact]
    public void UnfilteredRun_theOldCapAsymmetryGapIsClosed_byTheHubFuse()
    {
        // Deliberately NOT Checkpoint 2's scope. Until battle-hub-fuse (T6), the battle side's
        // ChannelMods loop was unconditionally additive with no cap at all (confirmed: zero `Cap(`
        // calls in the deleted BattleStatComposer.cs), so a SumIncreased-kind capped channel
        // (status.resist.*) disagreed once a contribution cleared the overlay-side cap. T6 replaced
        // that loop with AptitudeSubsystem -> the same AptitudeResolver.Resolve call the overlay path
        // makes, through the Hub's own DerivedComposer -- both sides now apply the identical cap, so
        // every status.* channel this allocation touches is confirmed zero-delta, not merely assumed.
        var root = _fx.UnfilteredDoc!.RootElement;
        foreach (var statusChannel in new[] { "status.resist.cc", "status.resist.dot", "status.resist.contagion" })
        {
            var delta = root.GetProperty("Deltas").GetProperty(statusChannel).GetDouble();
            Assert.Equal(0.0, delta, 9);
        }

        // The channel Checkpoint 2 actually cares about must still agree in the unfiltered run too.
        var powerDelta = root.GetProperty("Deltas").GetProperty("combat.power.omni").GetDouble();
        Assert.Equal(0.0, powerDelta, 9);
    }

    [Fact]
    public void UnfilteredRun_stillDisagreesOnResourceMax_becauseBattleAlwaysSeedsAResourceBaseline()
    {
        // The REMAINING, different gap: BattleHubCompose unconditionally registers
        // ResourceBaselineSubsystem (every battle actor needs its six resource pools seeded,
        // independent of whether an aptitude allocation is present), so the battle side's
        // resource.max.* totals are baseline + the aptitude edge's own contribution. The overlay side
        // (a bare DerivedComposer.Compose(overlayMods) with no subsystems registered at all) carries
        // only the aptitude edge. This is not new: the deleted BattleStatComposer.Compose seeded the
        // identical unconditional resource baseline (battle-resources, 2026-09-05), so this comparison
        // artifact predates the fuse -- T6 did not introduce it and this tool's own scope (the
        // aptitude seam alone, per its class doc) does not fix it.
        var root = _fx.UnfilteredDoc!.RootElement;
        Assert.False(root.GetProperty("Pass").GetBoolean());

        var maxHunger = root.GetProperty("Deltas").GetProperty("resource.max.hunger").GetDouble();
        Assert.NotEqual(0.0, maxHunger);
    }

    public sealed class Fixture : IDisposable
    {
        public int DefaultExit, UnfilteredExit;
        public string DefaultStdout = "", DefaultStderr = "", UnfilteredStdout = "", UnfilteredStderr = "";
        public JsonDocument? DefaultDoc, UnfilteredDoc;

        readonly List<string> _tempFiles = new();

        public Fixture()
        {
            var repoRoot = FindRepoRoot();

            var defaultPath = TempFile();
            (DefaultExit, DefaultStdout, DefaultStderr) = RunProveAptitude(repoRoot,
                $"--theta 1000 --source Might --points 100 --channels combat.power.omni --out \"{defaultPath}\"");
            if (File.Exists(defaultPath)) DefaultDoc = JsonDocument.Parse(File.ReadAllText(defaultPath));

            var unfilteredPath = TempFile();
            (UnfilteredExit, UnfilteredStdout, UnfilteredStderr) = RunProveAptitude(repoRoot,
                $"--theta 1000 --source Might --points 100 --out \"{unfilteredPath}\"");
            if (File.Exists(unfilteredPath)) UnfilteredDoc = JsonDocument.Parse(File.ReadAllText(unfilteredPath));
        }

        string TempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "fusionrpg-prove-aptitude-" + Guid.NewGuid().ToString("N") + ".json");
            _tempFiles.Add(path);
            return path;
        }

        static (int Exit, string Stdout, string Stderr) RunProveAptitude(string repoRoot, string args)
        {
            // ProjectReference + apphost (see ToolProcess) — no implicit build, no stale output.
            return ToolProcess.Run(repoRoot, "ProveAptitude", args, 120_000);
        }

        public void Dispose()
        {
            DefaultDoc?.Dispose();
            UnfilteredDoc?.Dispose();
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
