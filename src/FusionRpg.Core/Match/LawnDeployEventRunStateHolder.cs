namespace FusionRpg.Core.Match;

/// <summary>
/// Per-match holder for <see cref="LawnDeployEventRunState"/> — reset at <c>board.start</c>, mirroring
/// <see cref="LawnDeployRosterSnapshotHolder"/>'s own lifecycle exactly (same match, same edges). The
/// evaluator itself stays a pure function; this is where the CALLER (the Injector's own per-event
/// trigger check) threads the "already fired" state across repeated calls within one match.
/// </summary>
public static class LawnDeployEventRunStateHolder
{
    static readonly object Gate = new();
    static LawnDeployEventRunState _current = LawnDeployEventRunState.Fresh;

    public static LawnDeployEventRunState Current
    {
        get { lock (Gate) return _current; }
    }

    public static void BeginMatch() { lock (Gate) _current = LawnDeployEventRunState.Fresh; }

    public static void EndMatch() { lock (Gate) _current = LawnDeployEventRunState.Fresh; }

    public static void RecordFired(string caseId)
    {
        lock (Gate) _current = _current.WithFired(caseId);
    }
}
