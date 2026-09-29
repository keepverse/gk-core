namespace FusionRpg.Core.Battle.Board;

public sealed class MoveCostsRejection : Exception
{
    public MoveCostsRejection(string message) : base(message) { }
}

/// <summary>
/// Per-terrain movement cost for one pathfind (spec-siege-pathing.md §4). Negative costs throw HERE,
/// at construction, never during search — a negative edge makes Dijkstra and A* both silently wrong
/// rather than loudly broken, and this table comes from tuning, where a JSON typo could otherwise
/// corrupt every route in the game.
/// </summary>
public sealed class MoveCosts
{
    public int Open { get; }
    public int Rough { get; }

    /// <summary>Added to a diagonal step's terrain cost. Ships at 0 (base-defense decision 36):
    /// Chebyshev already means a diagonal costs the same as orthogonal, so a surcharge would
    /// desynchronize movement from every range check already shipped.</summary>
    public int DiagonalSurcharge { get; }

    /// <summary>
    /// The cheapest cost of any ENTERABLE terrain — computed, never configured, so the A* heuristic
    /// (spec §2) stays admissible no matter what a balance pass does to the tuning file. `Blocking`
    /// and `Gap` are excluded: they are never entered, so they cannot be the cheapest step.
    /// </summary>
    public int MinStepCost { get; }

    public MoveCosts(int open, int rough, int diagonalSurcharge)
    {
        if (open < 0) throw new MoveCostsRejection($"MoveCosts: open must be >= 0; got {open}");
        if (rough < 0) throw new MoveCostsRejection($"MoveCosts: rough must be >= 0; got {rough}");
        if (diagonalSurcharge < 0)
            throw new MoveCostsRejection($"MoveCosts: diagonalSurcharge must be >= 0; got {diagonalSurcharge}");

        Open = open;
        Rough = rough;
        DiagonalSurcharge = diagonalSurcharge;
        MinStepCost = Math.Min(open, rough);
    }

    public static MoveCosts FromTuning() =>
        new(SiegeTuningPolicy.MoveCostOpen, SiegeTuningPolicy.MoveCostRough, SiegeTuningPolicy.DiagonalSurcharge);

    public int CostOf(CellTerrain terrain) => terrain switch
    {
        CellTerrain.Open => Open,
        CellTerrain.Rough => Rough,
        // Blocking/Gap are never entered -- IBoardOccupancy filters them out before this is asked.
        _ => throw new MoveCostsRejection($"MoveCosts.CostOf: '{terrain}' is not an enterable terrain."),
    };
}
