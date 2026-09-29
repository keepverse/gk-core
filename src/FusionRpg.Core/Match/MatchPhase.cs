namespace FusionRpg.Core.Match;

/// <summary>Live overlay match phase (match-runtime §5). W1-A skeleton.</summary>
public enum MatchPhase
{
    Idle = 0,
    Starting = 1,
    InMatch = 2,
    Paused = 3,
    Ending = 4
}
