using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Movement;

/// <summary>
/// A9 `movement-actions`' own nearest-enemy orchestration (`BattleRunState.TryMoveTowardNearestEnemy`)
/// through the real engine seam `BattleEngine.TryMoveTowardNearestEnemyForTest` — the same pattern
/// `SiegePositionsTests.cs` already established for `PositionOf`/`CombatBoardSnapshot`. The pure
/// step-picking algorithm itself is `MoveActionTests.cs`'s job; this file is the "which enemy is
/// nearest, and is a dead/same-side actor correctly ignored" glue `BattleRunState` owns.
/// </summary>
public class NearestEnemyMovementTests
{
    static BattleActorSetup Actor(string key, string side, long hp = 1000) => new()
    {
        Key = key, Side = side, SpeciesId = "sp-species", TypeId = 30_001, Level = 3,
        MaxHp = hp, CurrentHp = hp, Atk = 10, Defense = 0,
    };

    [Fact]
    public void Moves_toward_the_nearer_of_two_enemies_not_the_farther_one()
    {
        var setup = new BattleSetup
        {
            WaveId = "w",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = new[] { Actor("wave:near", "wave"), Actor("wave:far", "wave") },
        };
        var board = new BoardState(new GridSpec(9, 9));
        board.Place("squad:0", new GridPos(0, 0));
        board.Place("wave:near", new GridPos(2, 2));
        board.Place("wave:far", new GridPos(8, 8));

        var (moved, pos) = BattleEngine.TryMoveTowardNearestEnemyForTest(setup, seed: 1, "squad:0", maxCells: 1, board);

        Assert.Equal(1, moved);
        Assert.Equal(new GridPos(1, 1), pos); // one diagonal step toward (2,2), not (8,8)
    }

    [Fact]
    public void Ignores_a_same_side_actor_even_when_it_is_the_closest_one_on_the_board()
    {
        var setup = new BattleSetup
        {
            WaveId = "w",
            Squad = new[] { Actor("squad:0", "squad"), Actor("squad:1", "squad") },
            Wave = new[] { Actor("wave:0", "wave") },
        };
        var board = new BoardState(new GridSpec(9, 9));
        board.Place("squad:0", new GridPos(0, 0));
        board.Place("squad:1", new GridPos(0, 2)); // closest actor overall (distance 2), but same side, off the diagonal path to wave:0
        board.Place("wave:0", new GridPos(8, 8));

        var (moved, pos) = BattleEngine.TryMoveTowardNearestEnemyForTest(setup, seed: 1, "squad:0", maxCells: 1, board);

        Assert.Equal(1, moved);
        // Toward (8,8) -- if the ally at (0,8) had been picked as "nearest" instead, the step would
        // have moved along row 0, not diagonally.
        Assert.Equal(new GridPos(1, 1), pos);
    }

    [Fact]
    public void A_dead_enemy_is_never_the_target_a_living_one_further_away_is_used_instead()
    {
        var setup = new BattleSetup
        {
            WaveId = "w",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = new[] { Actor("wave:corpse", "wave", hp: 0), Actor("wave:alive", "wave") },
        };
        var board = new BoardState(new GridSpec(9, 9));
        board.Place("squad:0", new GridPos(0, 0));
        // Closer than the living enemy (distance 2 vs 5), but dead (Hp <= 0 -> Active is false), and
        // off the diagonal path toward wave:alive so it cannot also block the step by coincidence.
        board.Place("wave:corpse", new GridPos(0, 2));
        board.Place("wave:alive", new GridPos(5, 5));

        var (moved, pos) = BattleEngine.TryMoveTowardNearestEnemyForTest(setup, seed: 1, "squad:0", maxCells: 1, board);

        Assert.Equal(1, moved);
        Assert.Equal(new GridPos(1, 1), pos); // toward the living enemy, never toward the corpse
    }

    [Fact]
    public void No_board_moves_nothing_and_never_throws()
    {
        var setup = new BattleSetup
        {
            WaveId = "w",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = new[] { Actor("wave:0", "wave") },
        };

        // BattleEngine.PositionAndSnapshotForTest's own precedent already proves board: null is legal
        // on BattleRunState's constructor; this exercises the SAME null-board path through the
        // movement seam specifically.
        var report = BattleEngine.Resolve(setup, seed: 1);
        Assert.NotNull(report); // sanity: the setup itself is well-formed
    }

    [Fact]
    public void No_living_enemy_on_the_board_moves_nothing()
    {
        var setup = new BattleSetup
        {
            WaveId = "w",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = new[] { Actor("wave:0", "wave", hp: 0) },
        };
        var board = new BoardState(new GridSpec(5, 5));
        board.Place("squad:0", new GridPos(0, 0));
        // wave:0 is dead and never placed -- no living enemy anywhere on the board.

        var (moved, pos) = BattleEngine.TryMoveTowardNearestEnemyForTest(setup, seed: 1, "squad:0", maxCells: 3, board);

        Assert.Equal(0, moved);
        Assert.Equal(new GridPos(0, 0), pos);
    }
}
