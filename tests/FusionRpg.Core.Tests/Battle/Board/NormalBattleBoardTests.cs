using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>
/// A10 `battle-board`'s own normal-encounter wiring (`NormalBattleBoard.cs`) — the caller
/// `DistrictAssaultResolver.cs` already is for siege. Proves squad/wave land on opposite edges,
/// determinism holds, and the board widens rather than refuses a real, larger roster.
/// </summary>
public class NormalBattleBoardTests
{
    [Fact]
    public void Squad_lands_on_the_left_edge_and_wave_on_the_right_edge()
    {
        var board = NormalBattleBoard.Build(
            new[] { "squad:0", "squad:1" }, new[] { "wave:0" }, seed: 1UL);

        Assert.All(new[] { "squad:0", "squad:1" }, key =>
            Assert.Equal(0, board.Positions[key].Col));
        Assert.Equal(board.Spec.Cols - 1, board.Positions["wave:0"].Col);
    }

    [Fact]
    public void Same_inputs_place_every_actor_on_the_identical_cell_across_two_independent_builds()
    {
        var squad = new[] { "squad:0", "squad:1", "squad:2" };
        var wave = new[] { "wave:0", "wave:1" };

        var a = NormalBattleBoard.Build(squad, wave, seed: 42UL);
        var b = NormalBattleBoard.Build(squad, wave, seed: 42UL);

        Assert.Equal(a.Spec.Rows, b.Spec.Rows);
        foreach (var key in squad.Concat(wave))
            Assert.Equal(a.Positions[key], b.Positions[key]);
    }

    [Fact]
    public void A_roster_larger_than_the_tuned_max_side_widens_the_board_rather_than_refusing_placement()
    {
        var bigSquad = Enumerable.Range(0, BattleBoardTuningPolicy.MaxSide + 4)
            .Select(i => $"squad:{i}").ToList();

        var board = NormalBattleBoard.Build(bigSquad, new[] { "wave:0" }, seed: 5UL);

        Assert.True(board.Spec.Rows >= bigSquad.Count,
            $"board rows ({board.Spec.Rows}) must seat every squad member ({bigSquad.Count})");
        foreach (var key in bigSquad)
            Assert.True(board.Positions.ContainsKey(key));
    }

    [Fact]
    public void An_empty_side_places_nothing_and_never_throws()
    {
        var board = NormalBattleBoard.Build(new[] { "squad:0" }, Array.Empty<string>(), seed: 9UL);

        Assert.Single(board.Positions);
        Assert.True(board.Positions.ContainsKey("squad:0"));
    }

    [Fact]
    public void Two_actors_never_share_a_cell_even_when_both_sides_are_large()
    {
        var squad = Enumerable.Range(0, 6).Select(i => $"squad:{i}").ToList();
        var wave = Enumerable.Range(0, 6).Select(i => $"wave:{i}").ToList();

        var board = NormalBattleBoard.Build(squad, wave, seed: 3UL);

        var seen = new HashSet<GridPos>();
        foreach (var key in squad.Concat(wave))
            Assert.True(seen.Add(board.Positions[key]), $"cell {board.Positions[key]} occupied twice");
    }
}
