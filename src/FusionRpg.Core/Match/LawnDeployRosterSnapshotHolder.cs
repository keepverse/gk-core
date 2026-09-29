namespace FusionRpg.Core.Match;

/// <summary>
/// Match-scoped roster snapshot — set at <c>board.start</c>, cleared on all match end paths. Mirrors
/// <see cref="Commanders.MatchCommanderSnapshotHolder"/>'s own shape exactly (same Hot/Cold problem,
/// same fix): the trigger evaluator (T2.2) reads <see cref="Current"/> during <c>InMatch</c> only,
/// never a live Cold-plane roster query.
/// </summary>
public static class LawnDeployRosterSnapshotHolder
{
    static readonly object Gate = new();
    static LawnDeployRosterSnapshot? _current;

    public static LawnDeployRosterSnapshot? Current
    {
        get { lock (Gate) return _current; }
    }

    public static void BeginMatch(LawnDeployRosterSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        lock (Gate) _current = snapshot;
    }

    public static void EndMatch()
    {
        lock (Gate) _current = null;
    }

    /// <summary>Hot-path read: the frozen match snapshot, or an empty roster outside a match / before
    /// the first cache refresh ever lands — never a live query, never a throw.</summary>
    public static LawnDeployRosterSnapshot ResolveOrEmpty() => Current ?? LawnDeployRosterSnapshot.Empty;

    /// <summary><c>debug.snapshot</c> nested <c>match.lawnDeployRoster</c> fold — mirrors
    /// <see cref="Commanders.MatchCommanderSnapshotHolder.ObserveCommanderFold"/>'s own role.</summary>
    public static Dictionary<string, object?>? ObserveRosterFold()
    {
        var cur = Current;
        if (cur == null) return null;
        return new Dictionary<string, object?>
        {
            ["eligibleCount"] = cur.Eligible.Count,
            ["eligibleInstanceIds"] = cur.Eligible.Select(e => e.InstanceId).ToArray(),
            ["snapshotRevision"] = cur.SnapshotRevision,
        };
    }
}
