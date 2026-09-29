using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// What the pathfinder treats as blocked (spec-siege-pathing.md §3). A parameter, not a fact of the
/// board — a unit standing in a doorway blocks it now and will not next round, so the pathfinder
/// takes a view rather than reading <see cref="BoardState"/> directly.
/// </summary>
public interface IBoardOccupancy
{
    bool IsBlocked(GridPos p);
}

/// <summary>Terrain plus every occupant. Actual movement — you cannot walk through anyone.</summary>
public sealed class SolidOccupancy : IBoardOccupancy
{
    readonly GridSpec _spec;
    readonly BoardState _board;

    public SolidOccupancy(GridSpec spec, BoardState board)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _board = board ?? throw new ArgumentNullException(nameof(board));
    }

    public bool IsBlocked(GridPos p) =>
        !_spec.Contains(p)
        || _spec.TerrainAt(p) is CellTerrain.Blocking or CellTerrain.Gap
        || _board.OccupantAt(p) is not null;
}

/// <summary>
/// Terrain only, no occupants. `siege-ai`'s planning question — "can I ever get there" vs "can I get
/// there this instant." Without this split, an AI surrounded by its own allies concludes the goal is
/// unreachable and stands still — the single most visible AI failure in any tactical game.
/// </summary>
public sealed class TerrainOnlyOccupancy : IBoardOccupancy
{
    readonly GridSpec _spec;

    public TerrainOnlyOccupancy(GridSpec spec) => _spec = spec ?? throw new ArgumentNullException(nameof(spec));

    public bool IsBlocked(GridPos p) =>
        !_spec.Contains(p) || _spec.TerrainAt(p) is CellTerrain.Blocking or CellTerrain.Gap;
}

/// <summary>
/// base-defense `siege-fog` (spec-siege-fog.md §4, module 30, 2026-09-06). Terrain plus only occupants
/// VISIBLE to the pathing side — decision 3's "fog affects pathing too": an unseen enemy does not block
/// a path until spotted. The third sibling in this file, the same shape `SolidOccupancy`/
/// `TerrainOnlyOccupancy` already are; <see cref="Battle.Board.BoardPathfinder"/> itself needs zero
/// changes, since it already takes occupancy as an injected parameter, exactly like `IBattleView` is
/// already the AI's sole injected read.
/// </summary>
public sealed class FoggedOccupancy : IBoardOccupancy
{
    readonly GridSpec _spec;
    readonly BoardState _board;
    readonly int _pathingSide;
    readonly Func<string, int> _sideOf;
    readonly Func<string, int> _visionRangeOf;
    readonly Func<GridPos, bool> _blocksVision;

    public FoggedOccupancy(GridSpec spec, BoardState board, int pathingSide,
        Func<string, int> sideOf, Func<string, int> visionRangeOf, Func<GridPos, bool> blocksVision)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _sideOf = sideOf ?? throw new ArgumentNullException(nameof(sideOf));
        _visionRangeOf = visionRangeOf ?? throw new ArgumentNullException(nameof(visionRangeOf));
        _blocksVision = blocksVision ?? throw new ArgumentNullException(nameof(blocksVision));
        _pathingSide = pathingSide;
    }

    public bool IsBlocked(GridPos p)
    {
        if (!_spec.Contains(p) || _spec.TerrainAt(p) is CellTerrain.Blocking or CellTerrain.Gap) return true;

        var occupant = _board.OccupantAt(p);
        if (occupant is null) return false;
        if (_sideOf(occupant) == _pathingSide) return true; // your own units still block your own path

        return Siege.SiegeVisibility.IsVisible(p, Watchers(), _blocksVision);
    }

    List<Siege.SiegeVisibility.Watcher> Watchers()
    {
        var watchers = new List<Siege.SiegeVisibility.Watcher>();
        foreach (var (key, pos) in _board.Positions)
        {
            if (_sideOf(key) != _pathingSide) continue;
            watchers.Add(new Siege.SiegeVisibility.Watcher(pos, _visionRangeOf(key)));
        }

        return watchers;
    }
}
