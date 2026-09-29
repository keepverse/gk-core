using System.Diagnostics;
using System.Threading.Channels;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

public sealed class EventIngest : BackgroundService
{
    // Structural (tunables-ssot.md T2) — SQLite write-batch size, not balance.
    public const int WriterBatch = 800;

    private readonly Channel<EventEnvelope> _channel = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly RpgStore _store;
    private readonly IHubContext<RpgHub> _hub;
    private readonly CompactionWorker _compaction;
    private readonly EffectGrantSession _grants;
    private readonly UniqueActorService _uniqueActors;
    private readonly InjectorCommandInbox _inbox;
    private int _queued;
    private int _writing;
    private double _lastFlushMs;

    public EventIngest(
        RpgStore store,
        IHubContext<RpgHub> hub,
        CompactionWorker compaction,
        EffectGrantSession grants,
        UniqueActorService uniqueActors,
        InjectorCommandInbox inbox)
    {
        _store = store;
        _hub = hub;
        _compaction = compaction;
        _grants = grants;
        _uniqueActors = uniqueActors;
        _inbox = inbox;
    }

    long _droppedBatches;
    long _lastDropLogTick;

    public int Queued => Volatile.Read(ref _queued);
    public double LastFlushMs => Volatile.Read(ref _lastFlushMs);
    public long DroppedBatches => Interlocked.Read(ref _droppedBatches);

    /// <summary>
    /// Ask the live injector for the existing authoritative <c>debug.snapshot</c> event. The
    /// command is queued before the best-effort SignalR push so a reconnect cannot lose the
    /// recovery edge; the injector poll remains the reliable path.
    /// </summary>
    public async Task RequestSnapshotAsync()
    {
        var command = new CommandDto
        {
            Name = RpgConstants.SnapshotCommand,
            Id = Guid.NewGuid().ToString("N")
        };
        _inbox.Enqueue(command);
        try
        {
            await _hub.Clients.Group(RpgConstants.InjectorGroup)
                .SendAsync("Command", command)
                .ConfigureAwait(false);
        }
        catch
        {
            // The inbox is the reliable delivery path.
        }
    }

    public HealthDto Decorate(HealthDto health)
    {
        health.IngestQueued = Queued;
        health.LastFlushMs = LastFlushMs;
        health.IngestDroppedEvents = DroppedEvents;
        return health;
    }

    /// <summary>PvZ-game events only — web-mode (webrpg) events must not touch injector-facing state.</summary>
    static bool IsPvzGameEvent(EventEnvelope env) => RpgConstants.IsPvzGame(env.Game);

    public int Enqueue(EventEnvelope? env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.Kind)) return 0;
        // Guard (audit 2026-08-21): a web battle's board.start/end must never clear the LIVE
        // PvZ match's effect-grant session.
        if (IsPvzGameEvent(env))
        {
            EffectGrantSessionRecorder.NoteMatchLifecycle(_grants, env.Kind);
            // Patron aura (spec-patron-creature.md): the marker is a SESSION grant per pvzrh match —
            // recorded server-side so a mid-match injector reconnect rehydrates it with the rest
            // of the session, and board.end's lifecycle Clear ends it with the match.
            if (string.Equals(env.Kind, "board.start", StringComparison.OrdinalIgnoreCase))
            {
                var patronGrant = PatronEndpoints.TryBuildPatronSessionGrant(_store);
                if (patronGrant != null)
                    _grants.Upsert(patronGrant);
            }
        }
        Interlocked.Increment(ref _queued);
        if (!_channel.Writer.TryWrite(env))
        {
            Interlocked.Decrement(ref _queued);
            return 0;
        }
        return 1;
    }

    public int EnqueueRange(IEnumerable<EventEnvelope> events)
    {
        var n = 0;
        foreach (var e in events)
            n += Enqueue(e);
        return n;
    }

    public async Task FlushPendingAsync(CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            if (Volatile.Read(ref _queued) == 0 && Volatile.Read(ref _writing) == 0)
                return;
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(30))
                return;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        while (!stoppingToken.IsCancellationRequested)
        {
            EventEnvelope first;
            try { first = await reader.ReadAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            Volatile.Write(ref _writing, 1);
            var batch = new List<EventEnvelope>(WriterBatch) { first };
            while (batch.Count < WriterBatch && reader.TryRead(out var next))
                batch.Add(next);
            var queuedCount = batch.Count;

            var sw = Stopwatch.StartNew();
            try
            {
                var (notify, stored) = InsertIsolatingFailures(_store, batch, NoteDroppedEvent);
                batch = stored;
                var snapshot = batch.LastOrDefault(e =>
                    e.Kind == "debug.snapshot" && IsPvzGameEvent(e));
                // UniqueActor recovery watches real PvZ matches only — web-battle die/end events
                // must not recover an ActiveBound specimen mid-PvZ-match (audit 2026-08-21).
                var pvzBatch = batch.Where(IsPvzGameEvent).ToList();
                if (pvzBatch.Count > 0)
                    try { _uniqueActors.ObserveEvents(pvzBatch); } catch { /* fail-closed */ }
                Volatile.Write(ref _lastFlushMs, sw.Elapsed.TotalMilliseconds);
                await BroadcastAsync(batch).ConfigureAwait(false);
                if (snapshot != null)
                    await BroadcastLawnRecoveryAsync(snapshot).ConfigureAwait(false);
                await BroadcastActivityAsync(notify.ActivityPlayers).ConfigureAwait(false);
                await BroadcastProgressionAsync(notify.Progression).ConfigureAwait(false);
                foreach (var runId in notify.ClosedRunIds)
                    _compaction.EnqueueClosedRun(runId);
            }
            catch (Exception ex)
            {
                // A failed flush drops the whole batch — make that visible instead of silent
                // (rate-limited: disk-full/locked-DB storms would otherwise flood the console).
                Interlocked.Increment(ref _droppedBatches);
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastDropLogTick) > 30_000)
                {
                    Interlocked.Exchange(ref _lastDropLogTick, now);
                    Console.Error.WriteLine(
                        $"[ingest] dropped batch of {batch.Count} events ({_droppedBatches} total): {ex.GetType().Name}: {ex.Message}");
                }

                Volatile.Write(ref _lastFlushMs, sw.Elapsed.TotalMilliseconds);
            }
            finally
            {
                Interlocked.Add(ref _queued, -queuedCount);
                Volatile.Write(ref _writing, 0);
            }
        }
    }

    long _droppedEvents;
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    internal void NoteDroppedEvent(EventEnvelope env, Exception ex)
    {
        var total = Interlocked.Increment(ref _droppedEvents);
        Console.Error.WriteLine($"[ingest] dropped event {env.Kind} ({total} total): {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// lawn-combat-wire L-N29: <see cref="RpgStore.InsertEvents"/> is one transaction, so one event that throws used to
    /// roll back and drop every neighbour in the writer batch (live: a late <c>board.start</c> took
    /// <c>debug.level.enter</c> and <c>board.modifiers</c> with it). On failure the batch is re-inserted one event at a
    /// time; only the events that fail on their own are dropped and reported. Returns the merged notify sets and the
    /// events that were stored, in their original order.
    /// </summary>
    public static (EventInsertNotify Notify, List<EventEnvelope> Stored) InsertIsolatingFailures(
        RpgStore store, List<EventEnvelope> batch, Action<EventEnvelope, Exception> onDropped)
    {
        try
        {
            return (store.InsertEvents(batch), batch);
        }
        catch when (batch.Count > 1)
        {
            var activity = new HashSet<long>();
            var progression = new List<RpgProgressionDirty>();
            var closed = new HashSet<long>();
            var stored = new List<EventEnvelope>(batch.Count);
            foreach (var env in batch)
            {
                try
                {
                    var one = store.InsertEvents(new[] { env });
                    activity.UnionWith(one.ActivityPlayers);
                    progression.AddRange(one.Progression);
                    closed.UnionWith(one.ClosedRunIds);
                    stored.Add(env);
                }
                catch (Exception ex)
                {
                    onDropped(env, ex);
                }
            }
            return (new EventInsertNotify(activity.ToList(), progression, closed.ToList()), stored);
        }
    }

    async Task BroadcastAsync(List<EventEnvelope> batch)
    {
        var live = batch.Where(e => !RpgConstants.IsNoisyKind(e.Kind)).ToList();
        if (live.Count == 0) return;
        if (live.Count == 1)
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("Event", live[0]).ConfigureAwait(false);
        else
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("EventBatch", new EventBatch { Events = live }).ConfigureAwait(false);
    }

    async Task BroadcastLawnRecoveryAsync(EventEnvelope snapshot)
    {
        await _hub.Clients.Group(RpgConstants.WebGroup)
            .SendAsync(RpgConstants.LawnRecoveryEvent, new LawnRecoveryStatusDto
            {
                State = "ready",
                MatchKey = snapshot.MatchKey,
                SnapshotId = snapshot.Id
            })
            .ConfigureAwait(false);
    }

    async Task BroadcastActivityAsync(IReadOnlyList<long> playerIds)
    {
        if (playerIds.Count == 0) return;
        foreach (var playerId in playerIds.Distinct())
        {
            var rollup = _store.GetPvzActivityRollup(playerId);
            if (rollup is null) continue;
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PvzActivityUpdated", rollup).ConfigureAwait(false);
            await _hub.Clients.Group(RpgConstants.InjectorGroup)
                .SendAsync("PvzActivityUpdated", new { playerId, revision = rollup.Revision }).ConfigureAwait(false);
        }
    }

    // live-probe Task 25 regression test (EventIngestProgressionBroadcastTests.cs) calls this
    // directly rather than driving the whole ingest pipeline through a real event insert -- no
    // existing test exercised this method at all before that fix, and building a full
    // combat-kill-shaped event through InsertIsolatingFailures just to reach a dispatch decision
    // would test the store's XP math a second time, not this method's own fan-out logic.
    internal async Task BroadcastProgressionAsync(IReadOnlyList<RpgProgressionDirty> dirty)
    {
        if (dirty.Count == 0) return;
        foreach (var d in dirty)
        {
            await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("RpgProgressionUpdated", new
            {
                playerId = d.PlayerId,
                kind = d.Kind,
                typeId = d.TypeId,
                revision = d.Revision
            }).ConfigureAwait(false);

            // species-progression `species-layer-delivery` step 6.2, trigger 4 (SP6.5): a species
            // level-up mid-run must reach the injector's speciesLayers cache the SAME way an
            // allocation change already does -- through the ONE shared AptitudesUpdated emitter
            // (AptitudeEndpoints.BroadcastBestEffort), which the injector's existing trigger 3
            // handler already turns into a full RefreshCommanderAllocationAsync() re-fetch
            // (SP6.4 extended that fetch to also carry speciesLayers). This is additive to the
            // RpgProgressionUpdated broadcast above, never a replacement for it -- the web FE still
            // reads that one for its own progression UI.
            //
            // ai-empire-species EP4.15 (trigger T1): the same broadcast now names WHICH empire's
            // species moved (R1 credits a zombie species to Zomboss's empire). A null empire is the
            // human one -- every pre-R1 write.
            if (d.Kind == FusionRpg.Core.Progression.RpgActorKinds.Species)
                await AptitudeEndpoints.BroadcastBestEffort(_hub, new AptitudeEndpoints.AptitudesUpdatedDto(
                    d.PlayerId, "species", null, null, d.Empire)).ConfigureAwait(false);

            // live-probe Task 25 (2026-09-16, reproven live 2026-09-20): Theta (the player's OWN
            // progression level) is the input to every magnitude (k x share^gamma x P(Theta)), but
            // CheatState.ApplyPowerSnapshot is only ever called from RpgClient.RefreshPowerIndexAsync
            // -- session start, reconnect, or an explicit power.index.reload command -- and nothing
            // sent that command when the player's own level changed. A player who levels mid-session
            // got none of the new Theta until the injector process restarted. Scoped to
            // RpgActorKinds.Player specifically: Plant/Zombie/Species/UniqueActor level-ups do not
            // change the PLAYER's own Theta and must not trigger this (a species level-up already has
            // its own, correctly-scoped signal three lines up).
            //
            // ai-empire-species EP4.18 (trigger T5, R23): a PLAYER-kind row can belong to a NON-human
            // empire -- Zomboss's commander clock is exactly that (`zomboss-commander-clock` SP7.2
            // credits `EmpireRef(save, Zomboss)` a `kind='player', type_id=0` row). Its level-up moves
            // HIS commander budget, so it must reach the injector's commander cache as an
            // empire-named `AptitudesUpdated` (the same additive field T1 added), NOT as a
            // `power.index.reload` -- that command re-reads the HUMAN player's own Theta, which a
            // Zomboss level-up does not change. Which empire is human is read from the save's own
            // empire rows, never a literal empire id.
            if (d.Kind == FusionRpg.Core.Progression.RpgActorKinds.Player)
            {
                if (d.Empire is { } movedEmpire && !IsHumanEmpire(d.PlayerId, movedEmpire))
                    await AptitudeEndpoints.BroadcastBestEffort(_hub, new AptitudeEndpoints.AptitudesUpdatedDto(
                        d.PlayerId, "commander", null, null, movedEmpire)).ConfigureAwait(false);
                else
                    await new InjectorCommandSender(_hub, _inbox).SendAsync(new CommandDto
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = "power.index.reload"
                    }).ConfigureAwait(false);
            }
        }
    }

    /// <summary>`ai-empire-species` EP4.18 (T5) — whether a progression write's empire is the save's own
    /// human empire. Read from `rpg_save_empires`, never a literal id: the write that produced the dirty
    /// already resolved that save's human empire, so this cannot be the first resolution to fail.</summary>
    bool IsHumanEmpire(long saveId, string empireId) =>
        string.Equals(_store.HumanEmpireOf(saveId).Value, empireId, StringComparison.Ordinal);
}
