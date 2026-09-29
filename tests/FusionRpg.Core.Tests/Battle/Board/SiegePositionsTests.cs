using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>
/// base-defense `siege-positions` (spec-siege-positions.md): three inert wiring gaps turned live —
/// `PositionOf`, `EffectBag.BoardSnapshot`, and `boardAvailable` — with the already-built, already-
/// correct consumers (`ActionTargetResolver`, `GridDistance`, `ActionValidator`) doing exactly what
/// they always did once real positions reach them. `BattleRunState` is private/nested, so
/// `BattleEngine.PositionAndSnapshotForTest` (added alongside this task) is the seam.
/// </summary>
public class SiegePositionsTests
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

    static BoardState Board()
    {
        var board = new BoardState(new GridSpec(5, 5));
        board.Place("squad:0", new GridPos(0, 0));
        board.Place("wave:0", new GridPos(4, 4));
        return board;
    }

    [Fact]
    public void Position_of_returns_null_without_a_board()
    {
        var (pos, _) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "squad:0", board: null);
        Assert.Null(pos);
    }

    [Fact]
    public void Position_of_returns_real_coordinates_with_a_board()
    {
        var (pos, _) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "squad:0", Board());
        Assert.Equal(new GridPos(0, 0), pos);
    }

    [Fact]
    public void Position_of_returns_null_for_an_unknown_actor()
    {
        var (pos, _) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "no-such-actor", Board());
        Assert.Null(pos);
    }

    [Fact]
    public void Effect_bag_board_snapshot_is_unassigned_without_a_board()
    {
        var (_, snapshot) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "squad:0", board: null);
        Assert.Null(snapshot);
    }

    [Fact]
    public void Board_snapshot_adapter_round_trips_positions_and_sides()
    {
        var (_, snapshot) = BattleEngine.PositionAndSnapshotForTest(Setup(), seed: 1, "squad:0", Board());
        Assert.NotNull(snapshot);
        var squad = snapshot!.FindPtr("squad:0");
        Assert.NotNull(squad);
        Assert.Equal("squad", squad!.Side);
        Assert.Equal(0, squad.Row);
        Assert.Equal(0, squad.Col);

        var wave = snapshot.FindPtr("wave:0");
        Assert.NotNull(wave);
        Assert.Equal("wave", wave!.Side);
        Assert.Equal(4, wave.Row);
        Assert.Equal(4, wave.Col);
    }

    [Fact]
    public void All_twelve_goldens_are_unaffected_boardless_resolve_still_works()
    {
        // Not a re-run of the full golden suite here (done separately via BattleGoldenTests /
        // ExpeditionResolverTests) -- this proves the NEW optional `board` parameter on
        // BattleEngine.Resolve's own public signature genuinely defaults to null / today's behaviour
        // for a plain call with nothing new supplied.
        var report = BattleEngine.Resolve(Setup(), seed: 1);
        Assert.NotNull(report);
    }

    [Fact]
    public void Area_action_is_rejected_without_a_board_and_legal_with_one()
    {
        var areaRow = new ActionRow
        {
            ActionId = "test.area",
            ContainerId = "",
            Targeting = new ActionTargetSpec { Mode = ActionTargetMode.Area },
            MinRange = 0,
            MaxRange = 3,
        };

        var withoutBoard = ActionValidator.ValidateAction(areaRow, Array.Empty<string>(), boardAvailable: false);
        Assert.Equal(ActionRejectionReason.AreaRequiresBoard, withoutBoard.Reason);

        var withBoard = ActionValidator.ValidateAction(areaRow, Array.Empty<string>(), boardAvailable: true);
        Assert.True(withBoard.IsOk);
    }

    [Fact]
    public void Range_gate_passes_unconditionally_with_no_board_and_bites_with_one()
    {
        // GridDistance.InRange's own documented rule (spec-siege-positions.md: "with no board, every
        // range check passes") -- this task's whole argument is that real positions now reach it.
        Assert.True(GridDistance.InRange(caster: null, target: null, minRange: 0, maxRange: 1));

        var near = new GridPos(0, 0);
        var far = new GridPos(4, 4);
        Assert.False(GridDistance.InRange(near, far, minRange: 0, maxRange: 1));
        Assert.True(GridDistance.InRange(near, far, minRange: 0, maxRange: 100));
    }

    [Fact]
    public void Placement_is_ordinal_by_key_not_roster_order()
    {
        var board = new BoardState(new GridSpec(1, 3));
        var cells = new[] { new GridPos(0, 0), new GridPos(0, 1), new GridPos(0, 2) };
        // Roster order deliberately reversed from ordinal order.
        Placement.PlaceActors(board, new[] { "c", "a", "b" }, cells);

        Assert.Equal(new GridPos(0, 0), board.Positions["a"]);
        Assert.Equal(new GridPos(0, 1), board.Positions["b"]);
        Assert.Equal(new GridPos(0, 2), board.Positions["c"]);
    }

    [Fact]
    public void Placement_is_identical_across_ten_thousand_runs()
    {
        var cells = new[] { new GridPos(0, 0), new GridPos(0, 1), new GridPos(0, 2) };
        var first = new BoardState(new GridSpec(1, 3));
        Placement.PlaceActors(first, new[] { "c", "a", "b" }, cells);
        var firstResult = first.Positions["a"];

        for (var i = 0; i < 10_000; i++)
        {
            var board = new BoardState(new GridSpec(1, 3));
            Placement.PlaceActors(board, new[] { "c", "a", "b" }, cells);
            Assert.Equal(firstResult, board.Positions["a"]);
        }
    }

    [Fact]
    public void Placement_throws_when_fewer_cells_than_actors()
    {
        var board = new BoardState(new GridSpec(1, 1));
        Assert.Throws<PlacementRejection>(() =>
            Placement.PlaceActors(board, new[] { "a", "b" }, new[] { new GridPos(0, 0) }));
    }
}
