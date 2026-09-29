using FusionRpg.Contracts;
using FusionRpg.Data;

namespace FusionRpg.Server.Notifications;

/// <summary>notify-service spec §3 - Commit wakes the pump after an advancing commit; Boot runs it
/// for every save's active map world at startup.</summary>
public enum WorldTurnTrigger { Commit, Boot }

/// <summary>notify-service spec §3 - the one pump for the world-turn clock (v1 builds only this
/// clock; a lawn or expedition source would bring its own pump with the same shape). Runs AFTER a
/// commit returns, never inside its transaction, so it adds no work to the turn engine and cannot
/// change a state hash.</summary>
public sealed class WorldTurnNotificationPump
{
    readonly RpgStore _store;
    readonly NotificationPublisher _publisher;
    readonly IReadOnlyList<IWorldTurnNotificationSource> _sources;
    readonly Dictionary<string, object> _worldLocks = new(StringComparer.Ordinal);

    public WorldTurnNotificationPump(RpgStore store, NotificationPublisher publisher, IEnumerable<IWorldTurnNotificationSource> sources)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _sources = sources?.ToList() ?? throw new ArgumentNullException(nameof(sources));
    }

    object LockFor(string worldId)
    {
        lock (_worldLocks)
        {
            if (!_worldLocks.TryGetValue(worldId, out var l))
            {
                l = new object();
                _worldLocks[worldId] = l;
            }
            return l;
        }
    }

    public void Run(string worldId, WorldTurnTrigger trigger)
    {
        lock (LockFor(worldId))
        {
            var header = _store.GetWorldHeader(worldId);
            if (header is null) return;
            // party-dungeon delve-scope: a delve world never runs TurnEngine.Step, so it has no
            // turn report to read (decisions.md "World store — delve worlds" row).
            if (header.Kind != "map") return;

            var resolvedTurn = header.CurrentTurn - 1; // the same turn the playback panel reads
            if (resolvedTurn < 0) return; // nothing has been committed yet

            var cursor = _store.GetNotificationCursor("world-turn", worldId);
            if (cursor is null)
            {
                // A world entering the system does not back-fill its whole history into the feed.
                _store.InitNotificationCursor("world-turn", worldId, resolvedTurn);
                return;
            }

            var currentWorld = _store.LoadWorldState(worldId);
            if (currentWorld is null) return;

            for (var t = (int)cursor.Value + 1; t <= resolvedTurn; t++)
            {
                var isLatest = t == resolvedTurn;
                var log = _store.GetWorldTurnLog(worldId, t);
                // Stored report only, never a replay (spec §3 step 4): GetWorldTurnReport falls back
                // to replaying the world past the hot tail, which drops post-Step lines this pump's
                // sources may need. A trimmed turn (ReportJson null) yields Report = null instead.
                var report = log?.ReportJson is not null ? _store.GetWorldTurnReport(worldId, t) : null;

                var ctx = new WorldTurnNotificationContext(worldId, t, isLatest, header, currentWorld, report);
                var drafts = new List<AddressedDraft>();
                foreach (var source in _sources)
                {
                    try
                    {
                        drafts.AddRange(source.Collect(ctx));
                    }
                    catch (Exception ex)
                    {
                        // One broken source never silences the rest.
                        Console.Error.WriteLine($"[notify-pump] source '{source.SourceId}' failed for {worldId}@{t}: {ex.Message}");
                    }
                }

                var delivery = (trigger == WorldTurnTrigger.Commit && isLatest) ? NotifyDelivery.Live : NotifyDelivery.CatchUp;
                _publisher.PublishTurn(drafts, delivery, new FusionRpg.Data.Notifications.NotificationCursorAdvance("world-turn", worldId, t));
            }
        }
    }
}
