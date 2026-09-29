using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Commanders;

/// <summary>
/// Match-scoped commander snapshot — set at <c>board.start</c>, cleared on all match end paths.
/// Bridge and lawn observe read <see cref="Current"/> during <c>InMatch</c> only.
/// </summary>
public static class MatchCommanderSnapshotHolder
{
    static readonly object Gate = new();
    static MatchCommanderSnapshot? _current;

    public static MatchCommanderSnapshot? Current
    {
        get { lock (Gate) return _current; }
    }

    public static void BeginMatch(MatchCommanderSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        lock (Gate) _current = snapshot;
    }

    public static void EndMatch()
    {
        lock (Gate) _current = null;
    }

    /// <summary>Hot-path allocation: frozen snapshot during a match, live cache outside.</summary>
    public static AptitudeAllocation ResolveAllocation(AptitudeAllocation live) =>
        Current?.Allocation ?? live;

    /// <summary><c>debug.snapshot</c> nested <c>match.commander</c> fold.</summary>
    public static Dictionary<string, object?>? ObserveCommanderFold()
    {
        var cur = Current;
        if (cur == null) return null;
        return new Dictionary<string, object?>
        {
            ["leadingCommanderId"] = cur.LeadingCommanderId,
            ["leadingCommanderDisplayName"] = cur.LeadingCommanderDisplayName,
            ["activeAuraId"] = cur.ActiveAuraId,
            ["activeAuraDisplayName"] = cur.ActiveAuraDisplayName,
        };
    }
}
