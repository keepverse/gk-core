namespace FusionRpg.Data;

/// <summary>`commander-roster` EP3.4 — the closed set of seats a unique can occupy on the lawn
/// (spec-lawn-commander-seat.md). `board` is the ordinary bound actor and the value every pre-EP3.4 row
/// reads; a later task adds the commander seat. A CLOSED vocabulary: a new seat is a reviewed change
/// here, never a loose string at a call site.</summary>
public static class UniqueLawnSeats
{
    public const string Board = "board";

    /// <summary>`commander-roster` EP3.5: the lawn COMMANDER seat — a deployment child with no tile, no
    /// ptr and no kill credit, seated by the MatchStarted drain and settled by the run-end recovery like
    /// any other `ActiveBound` row.</summary>
    public const string Commander = "commander";

    public static readonly IReadOnlyList<string> All = new[] { Board, Commander };
}
