namespace FusionRpg.Core.Match.Ai;

/// <summary>
/// zomboss-deploy-ai T3.4 — per-match "already fired" tracker, mirroring
/// <see cref="LawnDeployEventRunStateHolder"/>'s own lifecycle exactly (reset at board.start/board.end)
/// but deliberately simpler: Zomboss has one decision point per check, not several named cases, so a
/// single flag stands in for that holder's own `FiredCaseIds` set. A single fire per match is the
/// starting cap (matching the player-side trigger's own `maxFiresPerRun: 1` starting value) — loosening
/// this to allow a repeat deploy later in a long match is a real, reversible tuning follow-up, not
/// something this task invents against zero play data.
/// </summary>
public static class ZombossDeployRunStateHolder
{
    static readonly object Gate = new();
    static bool _firedThisMatch;

    public static bool AlreadyFired
    {
        get { lock (Gate) return _firedThisMatch; }
    }

    public static void BeginMatch() { lock (Gate) _firedThisMatch = false; }
    public static void EndMatch() { lock (Gate) _firedThisMatch = false; }
    public static void RecordFired() { lock (Gate) _firedThisMatch = true; }
}
