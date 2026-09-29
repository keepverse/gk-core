using FusionRpg.SquadHarness.Tests.TestSupport;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md "Code style"/"Testing strategy": "Each row of <c>MeasurementModes.All</c>, run
/// twice in one process at a small <c>--trials</c>, produces the identical determinism hash. Enumerates
/// the table, so a new mode is covered without editing the test." Uses
/// <see cref="MeasurementModes.AllWithRosters"/> with <see cref="TinyClassifiedRoster"/> so the table's
/// modes (duel, squad, transfer -- F2's scope; concentration, crossunlock -- F4's own todo line "verify
/// covers both new modes by enumerating the mode table"; erosion/budget are F3/F6 and, like F3, stay
/// covered by their own dedicated determinism tests instead) stay fast to run twice each.
/// </summary>
public class MeasurementModesTests
{
    [Fact]
    public void The_mode_table_has_the_F2_and_F4_modes()
    {
        var modes = MeasurementModes.AllWithRosters(TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads());
        Assert.Equal(new[] { "duel", "squad", "transfer", "concentration", "crossunlock" }, modes.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void Squad_and_transfer_modes_declare_their_named_default_artifact_paths()
    {
        var modes = MeasurementModes.AllWithRosters(TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads());
        Assert.Null(modes.Single(m => m.Name == "duel").DefaultArtifactPath);
        Assert.Equal("docs/research/passive-tree/_squad-scope.json", modes.Single(m => m.Name == "squad").DefaultArtifactPath);
        Assert.Equal("docs/research/passive-tree/_scope-transfer.json", modes.Single(m => m.Name == "transfer").DefaultArtifactPath);
        Assert.Equal("docs/research/passive-tree/_concentration-sweep.json", modes.Single(m => m.Name == "concentration").DefaultArtifactPath);
        Assert.Equal("docs/research/passive-tree/_crossunlock-sweep.json", modes.Single(m => m.Name == "crossunlock").DefaultArtifactPath);
    }

    [Fact]
    public void Every_mode_repeats_byte_identically()
    {
        var spec = new RunSpec(Theta: 60, Trials: 2, RunSeed: 20260906);
        var modes = MeasurementModes.AllWithRosters(TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads());

        foreach (var mode in modes)
        {
            var first = mode.Run(spec);
            var second = mode.Run(spec);
            Assert.Equal(first.Hash, second.Hash);
            Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        }
    }
}
