using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Board;

public sealed class BoardStateRejection : Exception
{
    public BoardStateRejection(string message) : base(message) { }
}

/// <summary>
/// Cell occupancy for one battle (spec-siege-board.md). Mutable inside a resolve, never persisted,
/// never hashed — the world stores the district LAYOUT (district-layout) and the structure STATE
/// (structure-state); where a given soldier stood on round 7 is not world state and must not become
/// it. This class does not know about structures, and must not learn — a structure occupying a cell
/// is delivered through this same occupancy API by structure-state, not baked in here.
/// </summary>
public sealed class BoardState
{
    public GridSpec Spec { get; }

    readonly Dictionary<string, GridPos> _positions = new(StringComparer.Ordinal);
    readonly string?[] _occupantByCell;

    public BoardState(GridSpec spec)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _occupantByCell = new string?[spec.Rows * spec.Cols];
    }

    /// <summary>
    /// Actor key → cell. Exposed read-only; never enumerate this for anything that affects the
    /// outcome (spec §3) — a `Dictionary`'s enumeration order is not part of the contract. Where an
    /// ordered walk over actors is needed, order by actor key, ordinal, the same discipline
    /// `LegionSupply` already applies.
    /// </summary>
    public IReadOnlyDictionary<string, GridPos> Positions => _positions;

    /// <summary>
    /// base-defense `siege-obstacles` §5: fired whenever an actor's occupied cell changes — on
    /// <see cref="Place"/> and the destination half of <see cref="Move"/>. This class still does not
    /// know about mines, cover, or anything else a cell might mean — it only reports "someone is now
    /// standing here", and stays exactly as ignorant of structures as its own class doc already
    /// requires. A caller that DOES care (a mine reactor) subscribes here; nothing subscribes by
    /// default, so this event costs nothing for every existing caller.
    /// </summary>
    public event Action<string, GridPos>? Entered;

    /// <summary>Fired on the origin half of <see cref="Move"/> and on <see cref="Remove"/> (death or
    /// withdrawal) — paired with <see cref="Entered"/> so an entry can never be left dangling.</summary>
    public event Action<string, GridPos>? Exited;

    public string? OccupantAt(GridPos p) => Spec.Contains(p) ? _occupantByCell[Spec.IndexOf(p)] : null;

    /// <summary>Passable for MOVEMENT: inside the board, terrain is not Blocking or Gap, and no one
    /// is standing there.</summary>
    public bool CanEnter(GridPos p) =>
        Spec.Contains(p)
        && Spec.TerrainAt(p) is not (CellTerrain.Blocking or CellTerrain.Gap)
        && _occupantByCell[Spec.IndexOf(p)] is null;

    /// <summary>Throws rather than returning false — a caller that tries to place onto an occupied
    /// or impassable cell has a bug in its intent generation (spec §2's own reasoning: a silent
    /// no-op turns that into a unit that mysteriously does not advance).</summary>
    public void Place(string actorKey, GridPos p)
    {
        if (string.IsNullOrWhiteSpace(actorKey))
            throw new BoardStateRejection("BoardState.Place: actorKey is empty.");
        if (_positions.ContainsKey(actorKey))
            throw new BoardStateRejection($"BoardState.Place: '{actorKey}' is already placed — use Move.");
        if (!CanEnter(p))
            throw new BoardStateRejection($"BoardState.Place: '{actorKey}' cannot enter {p} (occupied or impassable).");

        _positions[actorKey] = p;
        _occupantByCell[Spec.IndexOf(p)] = actorKey;
        Entered?.Invoke(actorKey, p);
    }

    public void Move(string actorKey, GridPos to)
    {
        if (!_positions.TryGetValue(actorKey, out var from))
            throw new BoardStateRejection($"BoardState.Move: '{actorKey}' is not on the board — use Place.");
        if (!CanEnter(to))
            throw new BoardStateRejection($"BoardState.Move: '{actorKey}' cannot enter {to} (occupied or impassable).");

        _occupantByCell[Spec.IndexOf(from)] = null;
        _occupantByCell[Spec.IndexOf(to)] = actorKey;
        _positions[actorKey] = to;
        // Exited before Entered — an observer counting "who is in this cell right now" must never see
        // the actor as present in both the old and new cell at once, even transiently.
        Exited?.Invoke(actorKey, from);
        Entered?.Invoke(actorKey, to);
    }

    /// <summary>On death or withdrawal. A no-op for an actor already off the board — the caller is
    /// reacting to a state change (died, routed) it does not itself track board membership for.</summary>
    public void Remove(string actorKey)
    {
        if (!_positions.TryGetValue(actorKey, out var at)) return;
        _occupantByCell[Spec.IndexOf(at)] = null;
        _positions.Remove(actorKey);
        Exited?.Invoke(actorKey, at);
    }
}
