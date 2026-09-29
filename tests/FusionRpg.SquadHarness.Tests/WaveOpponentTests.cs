using FusionRpg.SquadHarness.Tests.TestSupport;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §2: "Squad-vs-wave ships as a mode behind <c>--opponent wave</c> and is
/// reported separately, never mixed into the transfer table." <c>WaveCatalog</c> pins every wave's
/// enemy level to its own content index (rift-skirmish = 1), so this mode never lets <c>--theta</c>
/// override the content author's own level (a stomp at a mismatched Θ measures nothing, §2).
/// </summary>
public class WaveOpponentTests
{
    [Fact]
    public void Runs_every_named_squad_against_the_one_fixed_wave_at_the_waves_own_theta()
    {
        var squads = TinyClassifiedRoster.Squads().Select(RosterEntry.From).ToList();

        var report = WaveOpponent.Run(squads, "rift-skirmish", runSeed: 20260906, trials: 3);

        Assert.Equal("rift-skirmish", report.WaveId);
        Assert.Equal(1, report.WaveTheta); // rift-skirmish's ContentIndex (WaveCatalog.cs)
        Assert.Equal(squads.Count, report.Results.Count);
        foreach (var result in report.Results)
        {
            Assert.InRange(result.WinShareMilli, 0, 1000);
            Assert.True(result.HalfWidthMilli > 0);
            Assert.Equal("rift-skirmish", result.WaveId);
        }
    }

    [Fact]
    public void Is_deterministic_for_the_same_seed()
    {
        var squads = TinyClassifiedRoster.Squads().Select(RosterEntry.From).ToList();

        var r1 = WaveOpponent.Run(squads, "rift-skirmish", runSeed: 777, trials: 3);
        var r2 = WaveOpponent.Run(squads, "rift-skirmish", runSeed: 777, trials: 3);

        Assert.Equal(r1.Results.Select(r => r.WinShareMilli), r2.Results.Select(r => r.WinShareMilli));
    }

    [Fact]
    public void An_unknown_wave_id_throws_rather_than_silently_measuring_nothing()
    {
        var squads = TinyClassifiedRoster.Squads().Select(RosterEntry.From).ToList();
        Assert.Throws<ArgumentException>(() => WaveOpponent.Run(squads, "not-a-real-wave", runSeed: 1, trials: 1));
    }
}
