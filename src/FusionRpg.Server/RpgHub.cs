using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

public sealed class RpgHub : Hub
{
    private readonly RpgStore _store;
    private readonly EventIngest _ingest;
    private readonly EffectGrantSession _grants;
    private readonly InjectorCommandInbox _inbox;
    private readonly DelveBattleSessionManager _delveBattles;
    private readonly IHubContext<RpgHub> _hubContext;
    private readonly PlayerConnectionRegistry _playerConnections;

    public RpgHub(RpgStore store, EventIngest ingest, EffectGrantSession grants, InjectorCommandInbox inbox,
        DelveBattleSessionManager delveBattles, IHubContext<RpgHub> hubContext, PlayerConnectionRegistry playerConnections)
    {
        _store = store;
        _ingest = ingest;
        _grants = grants;
        _inbox = inbox;
        _delveBattles = delveBattles;
        _hubContext = hubContext;
        _playerConnections = playerConnections;
    }

    public async Task Join(string role)
    {
        var group = role == RpgConstants.InjectorGroup ? RpgConstants.InjectorGroup : RpgConstants.WebGroup;
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        if (group == RpgConstants.InjectorGroup)
        {
            _store.Heartbeat(RpgConstants.SourceInjector);
            return;
        }

        // A web join is a recovery edge, not merely a group membership change. Ask the
        // injector for the same authoritative debug.snapshot event it already emits; the web
        // folds that event and the status message only describes the edge state.
        await Clients.Caller.SendAsync(RpgConstants.LawnRecoveryEvent, new LawnRecoveryStatusDto
        {
            State = "loading"
        });
        if (!_store.LiveInjector)
        {
            await Clients.Caller.SendAsync(RpgConstants.LawnRecoveryEvent, new LawnRecoveryStatusDto
            {
                State = "error",
                Message = "No live game is available to provide an authoritative lawn snapshot."
            });
            return;
        }

        await _ingest.RequestSnapshotAsync();
    }

    /// <summary>player-routing spec §1 - joins the save this connection is showing (T1-T3). Refuses
    /// (returns false, joins nothing) an id that is not a LIVE save: no `players` row, or one whose
    /// `archived_utc` is set (spec §Testing "Archived row", NS7.1). The predicate is
    /// <see cref="RpgStore.IsLiveSave"/> - the same filter <see cref="RpgStore.ListPlayers"/> and
    /// `SetCurrentPlayer` use, so an archived row (the legacy Zomboss row the migration archived,
    /// save-identity D2/SE4.29) is never joinable in one place while being hidden in another. Never
    /// reads `current_player_id`. Moves the connection out of its previous player group first, if
    /// any, so one connection is in at most one player group at a time.</summary>
    public async Task<bool> JoinPlayer(long playerId)
    {
        if (!_store.IsLiveSave(playerId)) return false;

        var previous = _playerConnections.Set(Context.ConnectionId, playerId);
        if (previous is { } prevId && prevId != playerId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RpgConstants.PlayerGroup(prevId));
        await Groups.AddToGroupAsync(Context.ConnectionId, RpgConstants.PlayerGroup(playerId));
        return true;
    }

    public async Task Hello(HelloDto hello)
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        // The injector is the truth for its own debug session; this in-memory mirror resets on a server restart.
        DebugSessionState.Active = hello.DebugSessionActive;
        DebugSessionState.ScenarioId = hello.DebugSessionActive ? hello.DebugScenarioId ?? "" : "";
        _ingest.Enqueue(new EventEnvelope
        {
            T = ServerClock.UtcNowDateTime.ToString("o"),
            Game = hello.Game,
            Kind = "injector.hello",
            Payload = hello
        });
        await PushGrantSnapshotAsync();
        await PushPatronAsync();
    }

    /// <summary>A fresh inject/reconnect always receives the current patron designation
    /// (spec-patron-creature.md) — same rehydrate discipline as the grant snapshot above.</summary>
    async Task PushPatronAsync()
    {
        var cmd = PatronEndpoints.TryBuildPatronCommand(_store);
        if (cmd == null) return;
        _inbox.Enqueue(cmd);
        try
        {
            await Clients.Group(RpgConstants.InjectorGroup).SendAsync("Command", cmd);
        }
        catch
        {
            /* inbox poll */
        }
    }

    /// <summary>
    /// W0-E: push session Effect grants so the injector bag survives reconnect / re-inject, plus
    /// E19's compiled atom output on the same command.
    ///
    /// <para><b>The atom half resolves per player</b>, via <c>GetCurrentPlayerId</c> — the same
    /// current-player the patron push already uses for this exact shape, and the constitution's rule
    /// that the server stamps <c>player_id</c> while the injector never sends it
    /// ([pvz-middle-layer.md](../../docs/architecture/pvz-middle-layer.md) §Constitution 6). The
    /// session grant snapshot beside it stays session-scoped, because it always was.</para>
    /// </summary>
    async Task PushGrantSnapshotAsync()
    {
        var cmd = BuildApplyCommand();
        if (cmd == null) return;
        _inbox.Enqueue(cmd);
        try
        {
            await Clients.Group(RpgConstants.InjectorGroup).SendAsync("Command", cmd);
        }
        catch
        {
            /* inbox poll */
        }
    }

    /// <summary>
    /// The session grants and the compiled atom push travel on one command, because they are the same
    /// rehydrate: a reconnect must not leave the injector holding half of its effects.
    /// </summary>
    CommandDto? BuildApplyCommand()
    {
        var grants = _grants.Snapshot();

        AtomPushDto? atoms = null;
        try
        {
            var playerId = _store.GetCurrentPlayerId();

            // item-ideal.md, equip-runtime (module 5): the live lawn push, previously Player-scope
            // only. Every ActiveBound specimen (UniqueActorPhases.ActiveBound — deployed AND bound,
            // not merely rostered) contributes its own equipped-item atoms at
            // OwnerKind.UniqueActor(instanceId), the same scope ResolveBindings already resolves in
            // Data.Tests. GrantedDerivedAtomReader (the injector's read side) is already
            // scope-generic, so no Injector change is needed for this half — verified in P1.5.
            // T6.1 (2026-09-06): the union itself now lives in AtomPushService.OwnersForSave, so
            // the mid-session re-push UniqueActorService triggers on bind/unbind builds the identical
            // list rather than a second, hand-rolled copy of this same loop.
            var owners = AtomPushService.OwnersForSave(_store, playerId);

            // No seed at Hello: the lawn match key is born in the injector's board.start capture,
            // so the server has none here. The receiver derives the seed itself from that key with
            // MatchSeed.For — the same pure function the server uses when replaying, which is what
            // D5 actually needs. The wire field stays, so a stored seed can override it later.
            atoms = new AtomPushService(_store).Build(
                owners,
                new BindContext(RuntimeId.Lawn),
                matchSeed: 0);
        }
        catch (Exception ex)
        {
            // A failed atom push must never cost the injector its Foundation grants — the two halves
            // share a command, not a fate.
            Console.Error.WriteLine("[atom-push] build failed: " + ex.Message);
        }

        var nothingToSend = grants.Count == 0
            && (atoms is null || (atoms.Grants.Count == 0 && atoms.RunnerBindings.Count == 0 && atoms.Defs.Count == 0));
        if (nothingToSend) return null;

        // T6.2 (2026-09-06): assembled by AtomPushService.BuildApplyPayload, not inline here. This
        // dictionary was hand-rolled in two places and BOTH dropped `atoms.Grants` — the compiled
        // (passive) half of the push — while this very method already read `atoms.Grants.Count` in
        // its nothingToSend test above. See that method for why the compiled grants merge into the
        // SAME `grants` array as the session snapshot rather than a key of their own.
        var payload = AtomPushService.BuildApplyPayload(atoms, grants);

        return new CommandDto
        {
            Name = EffectGrantRehydrate.ApplyCommandName,
            Payload = payload,
            Id = Guid.NewGuid().ToString("N"),
        };
    }

    public Task Event(EventEnvelope envelope)
    {
        _ingest.Enqueue(envelope);
        return Task.CompletedTask;
    }

    public Task Events(List<EventEnvelope> batch)
    {
        _ingest.EnqueueRange(batch);
        return Task.CompletedTask;
    }

    public Task Metrics(List<MetricItem> items)
    {
        foreach (var m in items)
            _store.UpsertMetric(m.Name, m.Value);
        return Task.CompletedTask;
    }

    public async Task Heartbeat(HelloDto hello)
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        await Clients.Group(RpgConstants.WebGroup).SendAsync("Health", _ingest.Decorate(_store.ToHealth(SimFlags.Enabled)));
    }

    // ================================================================================================
    // D2.16 (spec-delve-battle-profile.md §3-4, §Structure) — steer / declare / resume over SignalR.
    // Freeze has no dedicated hub method: §9's own table names only two freeze CAUSES a player ever
    // sees -- three consecutive timeouts and a dropped connection -- neither is something a client
    // deliberately invokes ("stage-left"/"panel-closed" are both explicit client-observable no-ops
    // per session.ts's own doc comment), so freeze is always a SIDE EFFECT here: of Steer (moving
    // control away from a fight), or of OnDisconnectedAsync below, or of three recorded timeouts
    // inside DelveBattleSession's own onRecorded hook. Adding a bare "Freeze" verb with nothing
    // legitimate to call it for would be inventing wire surface the spec's own vocabulary does not ask
    // for.
    // ================================================================================================

    /// <summary>Steering moves from one party to another (or to none, on leaving every fight).
    /// The Data-owned record is validated and written before the existing freeze/log side effect;
    /// a refusal does not notify or append a decision. A transient disconnect deliberately leaves
    /// that durable selection intact so reconnect/restart can read the same party back.</summary>
    public async Task Steer(long delveId, int? fromPartyIndex, int? toPartyIndex)
    {
        long? playerId = _playerConnections.TryGet(Context.ConnectionId, out var boundPlayerId)
            ? boundPlayerId
            : null;
        var result = _delveBattles.TrySteer(delveId, fromPartyIndex, toPartyIndex, playerId);
        if (!result.Ok) return;

        // A party just gained control of an ALREADY-LIVE fight (e.g. two browser tabs, or steering
        // back to a party mid-fight) -- this connection is now the one to freeze on disconnect.
        if (toPartyIndex is { } to && _delveBattles.FindActiveForParty(delveId, to) is { } session)
            _delveBattles.TrackConnection(Context.ConnectionId, session.MatchKey);

        await NotifyDelveUpdatedAsync(delveId);
    }

    /// <summary>A player's declared choice for the CURRENT dwell. Returns <c>false</c> rather than
    /// throwing for a stale/misdirected declare (wrong actor, no session, already frozen) -- see
    /// <see cref="DelveBattleSession.Declare"/>'s own doc comment for exactly which cases that is.
    /// </summary>
    public Task<bool> Declare(string matchKey, string actorKey, string actionId, string? targetKey)
    {
        var ok = _delveBattles.Declare(matchKey, actorKey, actionId, targetKey);
        if (ok) _delveBattles.TrackConnection(Context.ConnectionId, matchKey);
        return Task.FromResult(ok);
    }

    /// <summary>
    /// Reconnect after a freeze — replays the recorded prefix, then goes live (spec §4b).
    ///
    /// <para><b>Not wired to a real automated policy in production today, named exactly like
    /// `DelveEndpoints.BuildDelveStartLive`'s own unreachable-today delegates.</b>
    /// spec-delve-battle-profile.md §3 names a real "siege-ai-class policy" as a CONSUMED dependency
    /// of this whole module, not something it builds; `grep`-confirmed (this session, fresh) that
    /// `SiegeAi.PlayedSide` is never set in production either, and the spec's own Boundaries section
    /// says "Never: `StubIntentSource` as the raid policy" — there is nothing correct to pass as
    /// <c>automated</c> here yet. <see cref="DelveBattleSessionManager.Resume"/> itself is real,
    /// tested and directly callable with a supplied automated policy
    /// (<c>DelveBattleSessionManagerTests</c>) — only this HTTP-reachable wiring is blocked, on that
    /// still-missing upstream policy, not on anything this task could build.</para>
    /// </summary>
    public Task Resume(string matchKey)
    {
        throw new NotImplementedException(
            "RpgHub.Resume needs a real siege-ai-class automated IIntentSource for the raid's " +
            "un-steered actors and every wave enemy -- none exists in production yet " +
            "(spec-delve-battle-profile.md §3's own consumed dependency; SiegeAi.PlayedSide is never " +
            "set in production either). StubIntentSource is explicitly barred from standing in for it. " +
            "DelveBattleSessionManager.Resume itself is real and already tested against a supplied " +
            "automated policy.");
    }

    /// <summary>A dropped connection freezes whatever it was steering, recording the same transient
    /// freeze/log shape as three consecutive timeouts (§9: "or the connection drops"). The canonical
    /// durable steering record is deliberately not erased, so reconnect/restart can read the same
    /// persisted party back. A connection that never called <see cref="Steer"/>/<see cref="Declare"/>
    /// (every non-delve connection, today) is a no-op.</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _delveBattles.FreezeByConnection(Context.ConnectionId);
        _playerConnections.Remove(Context.ConnectionId); // player-routing T4
        await base.OnDisconnectedAsync(exception);
    }

    async Task NotifyDelveUpdatedAsync(long delveId)
    {
        var revision = _store.LoadDelve(delveId)?.Revision ?? 0;
        await DelveEndpoints.NotifyDelveUpdatedAsync(_hubContext, delveId, revision);
    }
}
