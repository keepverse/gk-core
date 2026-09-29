using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>
/// A10 `battle-board`'s normal-encounter wiring end to end, through the REAL production seam
/// `BattleEngine.PositionAndSnapshotForTest` already proves for siege (`SiegePositionsTests.cs`) —
/// this is the same mechanism, a different caller (`NormalBattleBoard.Build`, not a district layout).
/// </summary>
public class NormalBattleBoardWiringTests
{
    static BattleActorSetup Actor(string key, string side) => new()
    {
        Key = key, Side = side, SpeciesId = "sp-species", TypeId = 30_001, Level = 3,
        MaxHp = 1000, Atk = 10, Defense = 0,
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "sp-wave",
        Squad = new[] { Actor("squad:0", "squad") },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    [Fact]
    public void A_normal_battle_board_gives_squad_0_a_real_position_through_the_real_engine_seam()
    {
        var board = NormalBattleBoard.Build(new[] { "squad:0" }, new[] { "wave:0" }, seed: 1UL);

        var (pos, snapshot) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "squad:0", board);

        Assert.NotNull(pos);
        Assert.Equal(0, pos!.Value.Col); // left edge, per NormalBattleBoard's own convention
        Assert.NotNull(snapshot);
    }

    [Fact]
    public void Squad_and_wave_sit_on_opposite_edges_through_the_real_engine_seam()
    {
        var board = NormalBattleBoard.Build(new[] { "squad:0" }, new[] { "wave:0" }, seed: 1UL);

        var (squadPos, _) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "squad:0", board);
        var (wavePos, _) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "wave:0", board);

        Assert.NotNull(squadPos);
        Assert.NotNull(wavePos);
        Assert.NotEqual(squadPos!.Value.Col, wavePos!.Value.Col);
    }

    [Fact]
    public void The_same_seed_that_seeds_the_battle_also_seeds_the_board_deterministically()
    {
        const ulong seed = 777UL;
        var boardA = NormalBattleBoard.Build(new[] { "squad:0" }, new[] { "wave:0" }, seed);
        var boardB = NormalBattleBoard.Build(new[] { "squad:0" }, new[] { "wave:0" }, seed);

        var (posA, _) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed, "squad:0", boardA);
        var (posB, _) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed, "squad:0", boardB);

        Assert.Equal(posA, posB);
    }
}
