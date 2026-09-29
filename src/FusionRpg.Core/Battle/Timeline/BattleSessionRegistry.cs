namespace FusionRpg.Core.Battle.Timeline;

/// <summary>What a live interactive battle session is currently doing.</summary>
public enum BattleSessionState
{
    /// <summary>A player is connected and decisions are being taken.</summary>
    Live,

    /// <summary>The connection dropped. The session is preserved and resumable — its trace is intact,
    /// it simply has nobody to ask right now.</summary>
    Disconnected,

    /// <summary>Terminal. The battle is abandoned and must never be written or re-resolved.</summary>
    Abandoned
}

/// <summary>One live interactive battle.</summary>
public sealed class BattleSession
{
    internal BattleSession(string matchKey, long playerId, DecisionTrace trace)
    {
        MatchKey = matchKey;
        PlayerId = playerId;
        Trace = trace;
    }

    public string MatchKey { get; }
    public long PlayerId { get; }
    public DecisionTrace Trace { get; }
    public BattleSessionState State { get; internal set; } = BattleSessionState.Live;
    public string? AbandonReason { get; internal set; }

    /// <summary>Consecutive turns taken by timeout rather than by a person.</summary>
    public int ConsecutiveTimeouts { get; internal set; }

    /// <summary>
    /// Whether this session's battle may be written. **False unless the battle actually finished
    /// under a live session** — a disconnected or abandoned session has a trace that describes only
    /// part of a battle, and writing it would persist a result nobody played to the end.
    /// </summary>
    public bool MayWriteResult => State == BattleSessionState.Live && Completed;

    public bool Completed { get; internal set; }
}

/// <summary>
/// **T11 — live interactive battle sessions.** Lifecycle, reconnect and AFK handling over T6's dwell
/// and T10's trace.
///
/// <para><b>Deterministic by construction, because nothing here reads a clock.</b> The acceptance is
/// that "a disconnect mid-battle resumes or abandons deterministically" — so abandonment is an
/// explicit act with a stated reason, never a timer that fires differently on a slow machine. AFK is
/// counted in TURNS (consecutive timeouts), not in seconds, for exactly the same reason
/// <see cref="DecisionSource.Timeout"/> is recorded as a decision at a tick.</para>
///
/// <para>Lives in Core rather than beside the SignalR hub so it is testable without a connection; the
/// hub is a thin caller. That is the same split `EntityWriteGate` was extracted for — CI never builds
/// transport-layer projects, so logic placed there is untested forever.</para>
/// </summary>
public sealed class BattleSessionRegistry
{
    readonly Dictionary<string, BattleSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Turns of silence before a session is abandoned. Structural, not a balance dial: it
    /// bounds an unbounded wait, and a battle nobody is answering must end rather than hold a slot
    /// forever. Counted in turns because a tick count is deterministic and a wall clock is not.</summary>
    public const int MaxConsecutiveTimeouts = 3;

    /// <summary>
    /// Open sessions, **ordered by match key**.
    ///
    /// <para>Returning <c>_sessions.Values</c> would be dictionary-enumeration order, which the kernel
    /// purity guard bans outright and rightly: this codebase has already had a live instance of order
    /// leaking from `Dictionary` internals into report bytes. A caller that iterates sessions must get
    /// the same order on every run, so the order is stated rather than inherited.</para>
    /// </summary>
    public IReadOnlyList<BattleSession> Live
    {
        get
        {
            var keys = new List<string>(_sessions.Count);
            foreach (var kv in _sessions) keys.Add(kv.Key);
            keys.Sort(StringComparer.Ordinal);

            var ordered = new List<BattleSession>(keys.Count);
            foreach (var k in keys) ordered.Add(_sessions[k]);
            return ordered;
        }
    }

    /// <summary>Opens a session, or refuses if one already exists for that match — an idempotency
    /// anchor, mirroring the match log's own per-correlation rule.</summary>
    public BattleSession Open(string matchKey, long playerId, DecisionTrace trace)
    {
        if (string.IsNullOrEmpty(matchKey)) throw new ArgumentException("A session needs a match key.", nameof(matchKey));
        if (_sessions.ContainsKey(matchKey))
            throw new InvalidOperationException($"A session for match '{matchKey}' is already open.");

        var session = new BattleSession(matchKey, playerId, trace ?? throw new ArgumentNullException(nameof(trace)));
        _sessions[matchKey] = session;
        return session;
    }

    public BattleSession? Find(string matchKey) =>
        _sessions.TryGetValue(matchKey, out var s) ? s : null;

    /// <summary>The connection dropped. The session is PRESERVED, not discarded — its trace is intact
    /// and the player may come back. Abandoning here would throw away a real result.</summary>
    public void Disconnect(string matchKey)
    {
        if (Find(matchKey) is not { } s || s.State == BattleSessionState.Abandoned) return;
        s.State = BattleSessionState.Disconnected;
    }

    /// <summary>
    /// Resume after a drop. Returns null when there is nothing to resume — including an abandoned
    /// session, which is terminal and must not come back to life.
    /// </summary>
    public BattleSession? Resume(string matchKey, long playerId)
    {
        if (Find(matchKey) is not { } s) return null;
        if (s.State == BattleSessionState.Abandoned) return null;
        if (s.PlayerId != playerId) return null;   // never hand a battle to a different player

        s.State = BattleSessionState.Live;
        return s;
    }

    /// <summary>Terminal, with a stated reason. Idempotent: abandoning twice keeps the FIRST reason,
    /// because the first is the one that explains what happened.</summary>
    public void Abandon(string matchKey, string reason)
    {
        if (Find(matchKey) is not { } s) return;
        if (s.State == BattleSessionState.Abandoned) return;
        s.State = BattleSessionState.Abandoned;
        s.AbandonReason = reason;
    }

    /// <summary>
    /// Records how a turn was decided. A player decision resets the AFK count; a timeout advances it,
    /// and enough consecutive timeouts abandon the session — in TURNS, so a replay of the same battle
    /// abandons at the same point rather than depending on how fast the machine ran.
    /// </summary>
    public void NoteTurn(string matchKey, DecisionSource source)
    {
        if (Find(matchKey) is not { } s || s.State == BattleSessionState.Abandoned) return;

        if (source == DecisionSource.Player)
        {
            s.ConsecutiveTimeouts = 0;
            return;
        }

        s.ConsecutiveTimeouts++;
        if (s.ConsecutiveTimeouts >= MaxConsecutiveTimeouts)
            Abandon(matchKey, $"AFK: {s.ConsecutiveTimeouts} consecutive turns taken by timeout");
    }

    /// <summary>The battle played to its end under this session.</summary>
    public void Complete(string matchKey)
    {
        if (Find(matchKey) is not { } s || s.State == BattleSessionState.Abandoned) return;
        s.Completed = true;
    }

    /// <summary>
    /// ⛔ The acceptance this class exists for: **no session path can write a battle whose trace is
    /// incomplete.** A session may write only if it is live, finished, and its trace actually holds
    /// the decisions — checked here rather than trusted at each call site, so there is one place to
    /// get right.
    /// </summary>
    public bool MayWrite(string matchKey) =>
        Find(matchKey) is { } s && s.MayWriteResult && s.Trace.Count > 0;

    public void Close(string matchKey) => _sessions.Remove(matchKey);
}
