namespace FusionRpg.Core.Lawn;

/// <summary>What EntityApply.MoveToCell (injector) should do with a recorded move, once the
/// decision is made. <see cref="Col"/>/<see cref="Row"/> are only meaningful for
/// <see cref="MoveOutcome.Apply"/> and <see cref="MoveOutcome.SkipSameCell"/> — the clamped
/// destination either way, so a caller logging the skip still sees where the actor already is.</summary>
public enum MoveOutcome
{
    /// <summary>The write should happen — EntityPositionWriter is the only thing that may act on this.</summary>
    Apply,
    /// <summary>The clamped destination equals the actor's current cell. Writer not called at all
    /// (spec-lawn-reposition.md §2, "the move's own equivalent [of EntityWriteGate] is 'the actor is
    /// already in that cell', which the writer checks and skips").</summary>
    SkipSameCell,
    /// <summary>The actor is dead (HP &lt;= 0) or has not completed its first EntityApply pass yet
    /// (still mid-spawn). Drop-and-count, never an exception (spec §3).</summary>
    DropDeadOrUnspawned
}

public readonly struct MoveDecision
{
    public readonly MoveOutcome Outcome;
    public readonly int Col;
    public readonly int Row;

    MoveDecision(MoveOutcome outcome, int col, int row)
    {
        Outcome = outcome;
        Col = col;
        Row = row;
    }

    public static MoveDecision Apply(int col, int row) => new(MoveOutcome.Apply, col, row);
    public static MoveDecision SkipSameCell(int col, int row) => new(MoveOutcome.SkipSameCell, col, row);
    public static readonly MoveDecision DropDeadOrUnspawned = new(MoveOutcome.DropDeadOrUnspawned, 0, 0);
}

/// <summary>
/// Pure decision for A-M2 lawn-reposition's "move actor to cell" — no Unity types, no field
/// writes, same shape as <c>FusionRpg.Core.Stats.EntityWriteGate.ShouldWrite</c> has for combat
/// fields. <c>EntityApply.MoveToCell</c> (injector) gathers the real actor state and calls this;
/// <c>EntityPositionWriter</c> performs the Unity write only when the result is
/// <see cref="MoveOutcome.Apply"/>.
///
/// Lives in Core, not the injector, for the exact reason <c>EntityWriteGate.cs</c>'s own header
/// gives for its own rule: the injector assembly needs a real PVZ Fusion install to build and is
/// absent from ci.yml's test projects, so the part of this feature that actually needs a
/// regression test has to be reachable without one.
///
/// <b>Deltas-not-absolutes does NOT apply here</b> (spec-lawn-reposition.md §2, stated so a later
/// session does not "fix" it in) — a cell is a destination, not a magnitude, so there is no
/// <c>EntityFinal.DiffersFrom</c>-style value diff. The move's own equivalent is "is the actor
/// already in that cell", which <see cref="Decide"/> answers directly by comparing clamped
/// destination to current position.
/// </summary>
public static class MoveDecisionPolicy
{
    /// <param name="actorAlive">False for an actor the game already considers dead (HP &lt;= 0).</param>
    /// <param name="actorSpawned">False while the actor has not completed its first EntityApply
    /// pass yet — the same mid-spawn window RunPlant/RunZombie themselves gate on
    /// (<c>GameHooks.Applied</c>). Grouped with <paramref name="actorAlive"/> under one outcome:
    /// both mean "not a safe write target", and the spec does not ask the two to be told apart.</param>
    /// <param name="currentCol">Actor's current column (already-clamped board space).</param>
    /// <param name="currentRow">Actor's current row.</param>
    /// <param name="requestedCol">Unclamped destination column from the action payload.</param>
    /// <param name="requestedRow">Unclamped destination row.</param>
    /// <param name="lastCol">Board's last valid column index (0-based inclusive).</param>
    /// <param name="lastRow">Board's last valid row index (0-based inclusive).</param>
    public static MoveDecision Decide(
        bool actorAlive,
        bool actorSpawned,
        int currentCol, int currentRow,
        int requestedCol, int requestedRow,
        int lastCol, int lastRow)
    {
        if (!actorAlive || !actorSpawned) return MoveDecision.DropDeadOrUnspawned;

        var col = LawnCoordMath.ClampIndex(requestedCol, lastCol);
        var row = LawnCoordMath.ClampIndex(requestedRow, lastRow);

        return col == currentCol && row == currentRow
            ? MoveDecision.SkipSameCell(col, row)
            : MoveDecision.Apply(col, row);
    }
}
