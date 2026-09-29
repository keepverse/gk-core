using System.Collections.Concurrent;

namespace FusionRpg.Server;

/// <summary>player-routing spec §1 - "connectionId -> playerId (runtime only)". One connection is in
/// at most one player group; this is the map `RpgHub.JoinPlayer` reads to find the PREVIOUS group so
/// it can leave it before joining the new one, and `OnDisconnectedAsync` uses to forget a dropped
/// connection. Structural runtime state, never persisted (a server restart drops every connection,
/// and every client re-joins through the reconnect trigger, T2). Singleton, in-memory, thread-safe
/// (SignalR hub methods and disconnect callbacks can run on different threads for different
/// connections).</summary>
public sealed class PlayerConnectionRegistry
{
    readonly ConcurrentDictionary<string, long> _byConnection = new();

    /// <summary>Records connectionId -&gt; playerId and returns the connection's PREVIOUS playerId,
    /// if any, so the caller can leave that group. Returns null the first time a connection joins.</summary>
    public long? Set(string connectionId, long playerId)
    {
        var previous = _byConnection.TryGetValue(connectionId, out var existing) ? existing : (long?)null;
        _byConnection[connectionId] = playerId;
        return previous;
    }

    public bool TryGet(string connectionId, out long playerId) => _byConnection.TryGetValue(connectionId, out playerId);

    /// <summary>T4 (disconnect) - keeps the registry from growing without bound.</summary>
    public void Remove(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    /// <summary>Tests only.</summary>
    public int Count => _byConnection.Count;
}
