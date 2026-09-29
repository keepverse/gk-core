using System.Reflection;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>base-defense siege-board (spec-siege-board.md). `BoardState`'s own test table.</summary>
public class BoardStateTests
{
    static GridSpec Board(int rows, int cols, params (GridPos Pos, CellTerrain Terrain)[] overrides)
    {
        var cells = new CellTerrain[rows * cols];
        var spec = new GridSpec(rows, cols);
        foreach (var (pos, terrain) in overrides)
            cells[spec.IndexOf(pos)] = terrain;
        return new GridSpec(rows, cols, cells);
    }

    [Fact]
    public void Gap_blocks_movement_but_not_sight()
    {
        var gapCell = new GridPos(1, 1);
        var spec = Board(3, 3, (gapCell, CellTerrain.Gap));
        var board = new BoardState(spec);

        // Blocks movement: CanEnter is false, and Move/Place both refuse it.
        Assert.False(board.CanEnter(gapCell));
        board.Place("archer", new GridPos(0, 0));
        Assert.Throws<BoardStateRejection>(() => board.Move("archer", gapCell));

        // Does NOT block line of sight: GridDistance/range checks read positions, not terrain --
        // a target standing across a Gap is still a legal target by distance alone. BoardState
        // itself carries no LOS concept (deliberately -- LOS is a targeting concern, not occupancy),
        // so the proof is that Gap is a DISTINCT value from Blocking, not folded into it.
        Assert.NotEqual(CellTerrain.Blocking, spec.TerrainAt(gapCell));
        Assert.Equal(CellTerrain.Gap, spec.TerrainAt(gapCell));
    }

    [Fact]
    public void One_occupant_per_cell_is_enforced()
    {
        var spec = new GridSpec(3, 3);
        var board = new BoardState(spec);
        var cell = new GridPos(1, 1);

        board.Place("a", cell);
        Assert.Throws<BoardStateRejection>(() => board.Place("b", cell));
    }

    [Fact]
    public void Move_into_blocking_throws()
    {
        var wall = new GridPos(2, 2);
        var spec = Board(3, 3, (wall, CellTerrain.Blocking));
        var board = new BoardState(spec);
        board.Place("a", new GridPos(0, 0));

        Assert.Throws<BoardStateRejection>(() => board.Move("a", wall));
        // Not a silent no-op: the actor's position is unchanged, not merely "still valid."
        Assert.Equal(new GridPos(0, 0), board.Positions["a"]);
    }

    [Fact]
    public void Move_updates_occupancy_on_both_the_old_and_new_cell()
    {
        var spec = new GridSpec(3, 3);
        var board = new BoardState(spec);
        var from = new GridPos(0, 0);
        var to = new GridPos(0, 1);

        board.Place("a", from);
        board.Move("a", to);

        Assert.Null(board.OccupantAt(from));
        Assert.Equal("a", board.OccupantAt(to));
        Assert.Equal(to, board.Positions["a"]);
    }

    [Fact]
    public void Remove_frees_the_cell_and_forgets_the_actor()
    {
        var spec = new GridSpec(3, 3);
        var board = new BoardState(spec);
        var cell = new GridPos(1, 1);
        board.Place("a", cell);

        board.Remove("a");

        Assert.Null(board.OccupantAt(cell));
        Assert.DoesNotContain("a", board.Positions.Keys);
        // A second removal is a no-op, not a throw -- the caller is reacting to a state change
        // (died, routed) it does not itself track board membership for.
        board.Remove("a");
    }

    [Fact]
    public void Board_has_no_order_dependent_enumeration()
    {
        // Positions is typed IReadOnlyDictionary<string, GridPos> -- there is no ordered accessor
        // to bypass, and the only way to consume it safely (per spec §3) is for the CALLER to sort
        // by actor key. Proven structurally: the public surface exposes exactly one enumerable
        // member for occupancy, and it is the dictionary itself -- no separate "ordered list" API
        // exists that a caller might reach for instead and forget to sort.
        var members = typeof(BoardState).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m is PropertyInfo or MethodInfo)
            .Where(m => m is not MethodInfo mi || !mi.IsSpecialName) // drop property get_/set_ accessors
            .Where(m => m.Name is not (nameof(Equals) or nameof(GetHashCode) or nameof(ToString) or nameof(GetType)))
            .ToList();

        var enumerableMembers = members.Where(m =>
        {
            var type = m is PropertyInfo p ? p.PropertyType : ((MethodInfo)m).ReturnType;
            return type != typeof(void) && type != typeof(bool) && type != typeof(string)
                   && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
        }).ToList();

        var only = Assert.Single(enumerableMembers);
        Assert.Equal(nameof(BoardState.Positions), only.Name);
    }
}
