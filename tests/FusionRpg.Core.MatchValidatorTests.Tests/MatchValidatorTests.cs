using FusionRpg.Contracts;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

public class MatchValidatorTests
{
    [Fact]
    public void Replay_same_steps_same_snapshot_counts_and_phase()
    {
        MatchReplayStep[] steps =
        {
            new("board.start", new Dictionary<string, object> { ["matchKey"] = "m-replay-1" }),
            new("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1", ["type"] = 1 }),
            new("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xZ1", ["type"] = 2 }),
            new("plant.place", new Dictionary<string, object> { ["ptr"] = "0xIGNORE" }),
            new("plant.die", new Dictionary<string, object> { ["ptr"] = "0xP1" }),
            new("board.end")
        };

        var a = MatchValidator.Replay(steps);
        var b = MatchValidator.Replay(steps);

        Assert.Equal(MatchPhase.Idle, a.Phase);
        Assert.Equal(a.Phase, b.Phase);
        Assert.Equal(a.PlantCount, b.PlantCount);
        Assert.Equal(a.ZombieCount, b.ZombieCount);
        Assert.Equal(0, a.PlantCount);
        Assert.Equal(0, a.ZombieCount);
        Assert.Null(a.MatchKey);
        Assert.Equal(a.Revision, b.Revision);
    }

    [Fact]
    public void Replay_mid_match_counts_before_end()
    {
        var snap = MatchValidator.Replay(new[]
        {
            new MatchReplayStep("board.start", new Dictionary<string, object> { ["matchKey"] = "m-mid" }),
            new MatchReplayStep("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xA" }),
            new MatchReplayStep("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xB" })
        });

        Assert.Equal(MatchPhase.InMatch, snap.Phase);
        Assert.Equal("m-mid", snap.MatchKey);
        Assert.Equal(1, snap.PlantCount);
        Assert.Equal(1, snap.ZombieCount);
    }

    [Fact]
    public void Replay_pause_step_then_spawn_allowed()
    {
        var snap = MatchValidator.Replay(new[]
        {
            new MatchReplayStep("board.start", new Dictionary<string, object> { ["matchKey"] = "m-p" }),
            new MatchReplayStep(setPaused: true),
            new MatchReplayStep("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xZ" })
        });

        Assert.Equal(MatchPhase.Paused, snap.Phase);
        Assert.Equal(1, snap.ZombieCount);
    }

    [Fact]
    public void Replay_place_ignored_for_living()
    {
        var snap = MatchValidator.Replay(new[]
        {
            new MatchReplayStep("board.start", new Dictionary<string, object> { ["matchKey"] = "m-pl" }),
            new MatchReplayStep("plant.place", new Dictionary<string, object> { ["ptr"] = "0xP", ["type"] = 7 })
        });

        Assert.Equal(0, snap.PlantCount);
    }

    [Fact]
    public void Replay_isolates_runtimes()
    {
        var first = MatchValidator.Replay(new[]
        {
            new MatchReplayStep("board.start", new Dictionary<string, object> { ["matchKey"] = "m-1" }),
            new MatchReplayStep("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1" })
        });
        var second = MatchValidator.Replay(new[]
        {
            new MatchReplayStep("board.start", new Dictionary<string, object> { ["matchKey"] = "m-2" })
        });

        Assert.Equal(1, first.PlantCount);
        Assert.Equal(0, second.PlantCount);
        Assert.Equal("m-2", second.MatchKey);
    }

    [Fact]
    public void Replay_empty_is_Idle()
    {
        var snap = MatchValidator.Replay(Array.Empty<MatchReplayStep>());
        Assert.Equal(MatchPhase.Idle, snap.Phase);
        Assert.Equal(0, snap.PlantCount);
        Assert.Null(snap.MatchKey);
    }

    [Fact]
    public void Replay_envelopes_inject_matchKey()
    {
        var env = new EventEnvelope
        {
            Kind = "board.start",
            MatchKey = "m-env",
            Payload = new Dictionary<string, object> { ["levelName"] = "L" }
        };
        var snap = MatchValidator.Replay(new[] { env });
        Assert.Equal(MatchPhase.InMatch, snap.Phase);
        Assert.Equal("m-env", snap.MatchKey);
    }

    [Fact]
    public void Replay_pause_unpause_then_spawn()
    {
        var snap = MatchValidator.Replay(new[]
        {
            new MatchReplayStep("board.start", new Dictionary<string, object> { ["matchKey"] = "m-pu" }),
            new MatchReplayStep(setPaused: true),
            new MatchReplayStep(setPaused: false),
            new MatchReplayStep("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP" })
        });

        Assert.Equal(MatchPhase.InMatch, snap.Phase);
        Assert.Equal(1, snap.PlantCount);
    }

    [Fact]
    public void Replay_match_result_ends()
    {
        var snap = MatchValidator.Replay(new[]
        {
            new MatchReplayStep("board.start", new Dictionary<string, object> { ["matchKey"] = "m-mr" }),
            new MatchReplayStep("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1" }),
            new MatchReplayStep("match.result")
        });

        Assert.Equal(MatchPhase.Idle, snap.Phase);
        Assert.Equal(0, snap.PlantCount);
        Assert.Null(snap.MatchKey);
    }
}
