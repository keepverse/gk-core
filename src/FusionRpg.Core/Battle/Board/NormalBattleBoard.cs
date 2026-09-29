namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// A10 `battle-board`'s own wiring for a normal (non-siege) squad-vs-wave encounter — the caller
/// `DistrictAssaultResolver.cs` already is for siege. A10's own module (<see cref="BoardGenerator"/>,
/// <see cref="Placement"/>) is deliberately side-count/placement-agnostic (spec-battle-board.md's
/// "Structure" section names only size, occupancy, distance, pathing); which edge each side starts on
/// is this program's own wiring decision, exactly as `DistrictAssaultResolver`'s approach/core zones
/// are ITS wiring decision, not `Placement.PlaceActors`'s.
///
/// <para><b>Squad on the left edge, wave on the right edge</b> — the simplest reading of "give the
/// battle a space" for a two-sided fight with no district geometry to derive zones from. The board is
/// always widened (never shrunk) to seat the larger side's full roster along its edge, so placement
/// never fails for a real squad/wave built by `WebMatchService`.</para>
/// </summary>
public static class NormalBattleBoard
{
    public static BoardState Build(IReadOnlyList<string> squadKeys, IReadOnlyList<string> waveKeys, ulong seed)
    {
        var minSide = Math.Max(squadKeys.Count, waveKeys.Count);
        var spec = BoardGenerator.Generate(seed, minSide);
        var board = new BoardState(spec);

        var squadCells = Enumerable.Range(0, squadKeys.Count)
            .Select(row => new Actions.GridPos(row, 0))
            .ToList();
        var waveCells = Enumerable.Range(0, waveKeys.Count)
            .Select(row => new Actions.GridPos(row, spec.Cols - 1))
            .ToList();

        if (squadKeys.Count > 0) Placement.PlaceActors(board, squadKeys, squadCells);
        if (waveKeys.Count > 0) Placement.PlaceActors(board, waveKeys, waveCells);

        return board;
    }
}
