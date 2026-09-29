using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Movement;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Movement;

/// <summary>
/// A9 `movement-actions` (spec-movement-actions.md) — `MoveAction`'s own test table, per the spec's
/// own testing strategy: destination legality is A10's (never a second occupancy rule), a destination
/// taken between commit and resolve never throws, and movement never touches lawn geometry (structural
/// — this whole module is Battle-grid types only, asserted by the total absence of any lawn/PvZ
/// reference anywhere in the file, not a dedicated test).
/// </summary>
public class MoveActionTests
{
    static BoardState Board(int size = 9) => new(new GridSpec(size, size));

    [Fact]
    public void One_step_moves_directly_toward_an_adjacent_target()
    {
        var board = Board();
        board.Place("mover", new GridPos(0, 0));

        var moved = MoveAction.MoveToward(board, "mover", target: new GridPos(2, 2), maxCells: 1);

        Assert.Equal(1, moved);
        Assert.Equal(new GridPos(1, 1), board.Positions["mover"]); // diagonal, Chebyshev-optimal
    }

    [Fact]
    public void Multiple_cells_walk_the_full_budget_when_the_path_stays_open()
    {
        var board = Board();
        board.Place("mover", new GridPos(0, 0));

        var moved = MoveAction.MoveToward(board, "mover", target: new GridPos(5, 5), maxCells: 3);

        Assert.Equal(3, moved);
        Assert.Equal(new GridPos(3, 3), board.Positions["mover"]);
    }

    [Fact]
    public void Never_steps_onto_the_targets_own_occupied_cell_even_when_adjacent()
    {
        var board = Board();
        board.Place("mover", new GridPos(0, 0));
        board.Place("enemy", new GridPos(1, 1)); // the real target IS occupied, in every real caller

        // A budget of 5 against an enemy 1 cell away: the only improving neighbour of (0,0) toward
        // (1,1) IS (1,1) itself, and it is occupied -- MoveToward must never place "mover" there.
        var moved = MoveAction.MoveToward(board, "mover", target: new GridPos(1, 1), maxCells: 5);

        Assert.Equal(0, moved); // no OTHER neighbour of (0,0) also reduces distance to (1,1)
        Assert.Equal(new GridPos(0, 0), board.Positions["mover"]);
        Assert.NotEqual(new GridPos(1, 1), board.Positions["mover"]); // never onto the target's own cell
    }

    [Fact]
    public void A_destination_taken_between_commit_and_resolve_never_throws_it_falls_back_to_the_next_best_step()
    {
        var board = Board();
        board.Place("mover", new GridPos(0, 0));
        // (0,1) is the FIRST-found, tied-best improving step toward (0,4) -- taken by the time this
        // resolves, the exact "commit vs resolve" race the spec names. (1,1) is equally good
        // (Chebyshev distance 3, same as (0,1)) and still open.
        board.Place("blocker", new GridPos(0, 1));

        var moved = MoveAction.MoveToward(board, "mover", target: new GridPos(0, 4), maxCells: 1);

        Assert.Equal(1, moved);
        Assert.Equal(new GridPos(1, 1), board.Positions["mover"]);
        Assert.True(GridDistance.Chebyshev(board.Positions["mover"], new GridPos(0, 4))
                    < GridDistance.Chebyshev(new GridPos(0, 0), new GridPos(0, 4)));
    }

    [Fact]
    public void Already_at_the_target_moves_zero_cells_and_never_throws()
    {
        var board = Board();
        board.Place("mover", new GridPos(2, 2));

        var moved = MoveAction.MoveToward(board, "mover", target: new GridPos(2, 2), maxCells: 3);

        Assert.Equal(0, moved);
        Assert.Equal(new GridPos(2, 2), board.Positions["mover"]);
    }

    [Fact]
    public void A_boxed_in_actor_moves_zero_cells_rather_than_throwing()
    {
        var board = Board(3);
        board.Place("mover", new GridPos(1, 1));
        // Every one of the 8 neighbours occupied -- no legal step exists at all.
        var ring = new[]
        {
            new GridPos(0, 0), new GridPos(0, 1), new GridPos(0, 2),
            new GridPos(1, 0), new GridPos(1, 2),
            new GridPos(2, 0), new GridPos(2, 1), new GridPos(2, 2),
        };
        for (var i = 0; i < ring.Length; i++) board.Place($"wall{i}", ring[i]);

        var moved = MoveAction.MoveToward(board, "mover", target: new GridPos(2, 2), maxCells: 3);

        Assert.Equal(0, moved);
        Assert.Equal(new GridPos(1, 1), board.Positions["mover"]);
    }

    [Fact]
    public void Zero_or_negative_budget_moves_nothing()
    {
        var board = Board();
        board.Place("mover", new GridPos(0, 0));

        Assert.Equal(0, MoveAction.MoveToward(board, "mover", new GridPos(5, 5), maxCells: 0));
        Assert.Equal(0, MoveAction.MoveToward(board, "mover", new GridPos(5, 5), maxCells: -1));
        Assert.Equal(new GridPos(0, 0), board.Positions["mover"]);
    }

    [Fact]
    public void An_actor_not_on_the_board_moves_nothing_rather_than_throwing()
    {
        var board = Board();
        var moved = MoveAction.MoveToward(board, "ghost", new GridPos(5, 5), maxCells: 3);
        Assert.Equal(0, moved);
    }
}
