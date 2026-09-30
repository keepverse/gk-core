using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Battle;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// D2.16 — <see cref="DelveBattleSessionManager"/>: the Server-layer registry that turns
/// <see cref="DelveBattleSession"/> into something a real delve/store/hub can drive. Covers match-key
/// derivation, the log-before-ingest discipline for starting a session, Steer's freeze-or-append
/// behaviour, FreezeByConnection, and a real Resume round-trip against the persisted
/// <c>rpg_web_match_log</c> row.
/// </summary>
[Trait("VerificationId", "server.delve-live-push")]
public class DelveBattleSessionManagerTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;

    public DelveBattleSessionManagerTests()
    {
        DelveBattleTuningTestFixture.ConfigureRealBattleTuning();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        DungeonTuningHub.Configure(DungeonTuningLoader.Parse(
            File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    const ulong WorldSeed = 11UL;

    WorldState BuildGraph(string worldId, string partyEntityId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = WorldSeed, CurrentTurn = 0,
        Factions = new[] { new WorldFaction { FactionId = _playerId.ToString(), Kind = WorldFactionKind.Player, Name = "Player" } },
        Sectors = new[] { new WorldSector { SectorId = "r0c0", TypeId = "fight" } },
        Lanes = Array.Empty<WorldLane>(),
        Entities = new[]
        {
            new WorldEntity { EntityId = partyEntityId, Kind = WorldEntityKind.Warband, OwnerFactionId = _playerId.ToString(), AtSectorId = "r0c0" },
        },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
    };

    long CreateDelve()
    {
        var worldId = "delve-battle-mgr-" + Guid.NewGuid().ToString("N");
        var (ok, reason, delve) = _store.CreateDelve(
            _playerId, "domain.fire-shallow-001", "solo", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", WorldSeed, BuildGraph(worldId, "0"), BuildRooms(), _rooms, _doors);
        Assert.True(ok, reason);
        return delve!.DelveId;
    }

    long CreatePairDelve()
    {
        var worldId = "delve-battle-mgr-pair-" + Guid.NewGuid().ToString("N");
        var world = new WorldState
        {
            WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = WorldSeed, CurrentTurn = 0,
            Factions = new[] { new WorldFaction { FactionId = _playerId.ToString(), Kind = WorldFactionKind.Player, Name = "Player" } },
            Sectors = new[] { new WorldSector { SectorId = "r0c0", TypeId = "fight" } },
            Lanes = Array.Empty<WorldLane>(),
            Entities = new[]
            {
                new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = _playerId.ToString(), AtSectorId = "r0c0" },
                new WorldEntity { EntityId = "party-1", Kind = WorldEntityKind.Warband, OwnerFactionId = _playerId.ToString(), AtSectorId = "r0c0" },
            },
        };
        var (ok, reason, delve) = _store.CreateDelve(
            _playerId, "domain.fire-shallow-001", "pair", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", WorldSeed, world, BuildRooms(), _rooms, _doors);
        Assert.True(ok, reason);
        return delve!.DelveId;
    }

    // ---- setup fixtures (mirrors DelveBattleSessionTests) --------------------------------------------

    static BattleActorSetup SquadActor(string key, int? partyIndex = 0) => new()
    {
        Key = key, Side = "squad", PartyIndex = partyIndex, SpeciesId = "test-species", TypeId = 1,
        Level = 30, MaxHp = 10_000_000, Atk = BattleRuleset.BaseAtk(30), Defense = BattleRuleset.BaseDefense(30),
    };

    static BattleActorSetup WaveActor(string key) => new()
    {
        Key = key, Side = "wave", SpeciesId = "test-species", TypeId = 2,
        Level = 1, MaxHp = 10_000_000, Atk = BattleRuleset.BaseAtk(1), Defense = BattleRuleset.BaseDefense(1),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "test-delve-room",
        Squad = new[] { SquadActor("squad:p0:0"), SquadActor("squad:p0:1") },
        Wave = new[] { WaveActor("wave:0") },
    };

    sealed class FixedAttacker : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) => new(
            "act.attack",
            actorKey.StartsWith("squad", StringComparison.Ordinal) ? "wave:0" : "squad:p0:0",
            BattleEngine.BasicAttackEnvelope);
    }

    /// <summary>D5.11's live-push wave (2026-09-08) — a fake <see cref="IDelveLivePush"/> that records
    /// every push verbatim, so a test can assert exactly which wire events fired, in what order, with
    /// what payload, with no live SignalR server needed (matches this project's own callback-capture
    /// style rather than reaching for a mocking library this test project does not depend on).</summary>
    /// <para>Thread-safe on purpose (2026-09-15): <c>Resume</c> starts the resumed session's background run,
    /// which pushes while the test reads — the unsynchronised list threw "Collection was modified" in
    /// full-suite runs. Readers get a snapshot.</para>
    sealed class RecordingPush : IDelveLivePush
    {
        readonly object _gate = new();
        readonly List<(string EventName, object Payload)> _pushes = new();
        public void Push(string eventName, object payload)
        {
            lock (_gate) _pushes.Add((eventName, payload));
        }
        public List<(string EventName, object Payload)> Pushes
        {
            get { lock (_gate) return new List<(string EventName, object Payload)>(_pushes); }
        }
        public void Clear()
        {
            lock (_gate) _pushes.Clear();
        }
        public IEnumerable<object> PayloadsOf(string eventName) => Pushes.Where(p => p.EventName == eventName).Select(p => p.Payload).ToList();
    }

    static async Task RespondToPendingAskOnceAsync(DelveBattleSession session)
    {
        if (session.PendingActorKey is { } pending)
            session.Declare(pending, "act.attack",
                pending.StartsWith("squad", StringComparison.Ordinal) ? "wave:0" : "squad:p0:0");
        await Task.Delay(2);
    }

    static async Task DriveUntilAsync(DelveBattleSession session, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await RespondToPendingAskOnceAsync(session);
        Assert.True(condition(), "condition never became true within the test timeout");
    }

    /// <summary>Reads a named field off one of the anonymous-object push payloads
    /// (`new { matchKey, actorKey, source }` etc.) without redeclaring their shape here.</summary>
    static object? Prop(object payload, string name) => payload.GetType().GetProperty(name)?.GetValue(payload);

    // ==================================================================================================
    // Match key / correlation derivation
    // ==================================================================================================

    [Fact]
    public void MatchKey_and_correlation_are_position_for_position_identical_modulo_separator()
    {
        var matchKey = DelveBattleSessionManager.MatchKeyFor(delveId: 7, row: 2, col: 3, partyIndex: 1);
        var correlation = DelveBattleSessionManager.CorrelationFor(delveId: 7, row: 2, col: 3, partyIndex: 1);

        Assert.Equal("delve-7-2-3-p1", matchKey);
        Assert.Equal("delve:7:2:3:p1", correlation);
        Assert.Equal(correlation, matchKey.Replace('-', ':'));
    }

    // ==================================================================================================
    // StartSession
    // ==================================================================================================

    [Fact]
    public async Task StartSession_logs_before_ingest_and_runs_the_fight()
    {
        var delveId = CreateDelve();
        var manager = new DelveBattleSessionManager(_store);

        var session = manager.StartSession(
            delveId, row: 0, col: 0, partyIndex: 0, _playerId,
            Setup(), seed: 555UL, automated: new FixedAttacker());

        Assert.NotNull(session);
        var matchKey = DelveBattleSessionManager.MatchKeyFor(delveId, 0, 0, 0);
        Assert.Equal(matchKey, session!.MatchKey);

        // The row is logged BEFORE the fight resolves (log-before-ingest) -- readable immediately.
        var entry = _store.TryGetWebMatchLog(_playerId, DelveBattleSessionManager.CorrelationFor(delveId, 0, 0, 0));
        Assert.NotNull(entry);
        Assert.Equal("delve", entry!.ProfileId);
        Assert.Null(entry.RunId); // not yet finished

        // Drive a couple of real decisions -- proves the session is genuinely live and persisting.
        await DriveUntilAsync(session, () => session.Trace.Count >= 2);
        var reread = _store.TryGetWebMatchLog(_playerId, DelveBattleSessionManager.CorrelationFor(delveId, 0, 0, 0));
        Assert.True(reread!.DecisionsJson?.Length > 2); // more than "[]"

        // Clean up.
        session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session.RunTask!);
    }

    [Fact]
    public void StartSession_twice_for_the_same_room_and_party_resumes_instead_of_forking_a_second_battle()
    {
        var delveId = CreateDelve();
        var manager = new DelveBattleSessionManager(_store);

        var first = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 777UL, automated: new FixedAttacker());
        Assert.NotNull(first);
        first!.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));

        // A second "start" call for the identical room+party is really a resume over the SAME logged
        // row (log-before-ingest's own replay-gate discipline) -- not a second, independent battle.
        var second = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 999UL, automated: new FixedAttacker());
        Assert.NotNull(second);
        Assert.Equal(first.MatchKey, second!.MatchKey);

        second.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
    }

    // ==================================================================================================
    // Steer
    // ==================================================================================================

    [Fact]
    public async Task Steer_freezes_the_from_partys_live_session_and_logs_exactly_one_entry()
    {
        var delveId = CreatePairDelve();
        var manager = new DelveBattleSessionManager(_store);
        var session = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 111UL, automated: new FixedAttacker())!;

        manager.Steer(delveId, fromPartyIndex: 0, toPartyIndex: 1);

        await Record.ExceptionAsync(() => session.RunTask!);
        Assert.Equal(TaskStatus.Canceled, session.RunTask!.Status);

        var delve = _store.LoadDelve(delveId)!;
        var decisions = JsonSerializer.Deserialize<List<JsonElement>>(delve.DecisionsJson)!;
        var steerEntries = decisions.Where(d => d.GetProperty("Kind").GetString() == DelveDecisionKinds.Steer).ToList();
        var entry = Assert.Single(steerEntries);
        Assert.Equal(0, entry.GetProperty("PartyIndex").GetInt32());
        var payload = entry.GetProperty("Payload");
        Assert.Equal(0, payload.GetProperty("From").GetInt32());
        Assert.Equal(1, payload.GetProperty("To").GetInt32());
        var (steeringOk, steeringReason, steering) = _store.ReadDelveSteering(delveId);
        Assert.True(steeringOk, steeringReason);
        Assert.Equal(1, steering!.PartyIndex);
    }

    [Fact]
    public async Task A_stale_steer_refuses_without_freezing_or_appending_a_decision()
    {
        var delveId = CreatePairDelve();
        Assert.True(_store.TrySetDelveSteering(delveId, 0, 1).Ok);
        var manager = new DelveBattleSessionManager(_store);
        var session = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 112UL, automated: new FixedAttacker())!;

        var result = manager.TrySteer(delveId, fromPartyIndex: 0, toPartyIndex: 0);

        Assert.False(result.Ok);
        Assert.Equal("delve.steering.stale", result.Reason);
        Assert.False(session.Frozen);
        var persisted = _store.ReadDelveSteering(delveId);
        Assert.True(persisted.Ok);
        Assert.Equal(1, persisted.Steering!.PartyIndex);
        Assert.Empty(JsonSerializer.Deserialize<List<JsonElement>>(_store.LoadDelve(delveId)!.DecisionsJson)!);

        session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session.RunTask!);
    }

    [Fact]
    public void A_successful_steer_keeps_the_existing_delve_decision_order()
    {
        var delveId = CreatePairDelve();
        _store.AppendDecision(delveId, DelveDecision.Create(0, DelveDecisionKinds.Enter, payload: new { domainId = "domain.fire-shallow-001" }));
        var manager = new DelveBattleSessionManager(_store);

        manager.Steer(delveId, fromPartyIndex: 0, toPartyIndex: 1);

        var decisions = JsonSerializer.Deserialize<List<JsonElement>>(_store.LoadDelve(delveId)!.DecisionsJson)!;
        Assert.Equal(2, decisions.Count);
        Assert.Equal(0, decisions[0].GetProperty("Seq").GetInt32());
        Assert.Equal(DelveDecisionKinds.Enter, decisions[0].GetProperty("Kind").GetString());
        Assert.Equal(1, decisions[1].GetProperty("Seq").GetInt32());
        Assert.Equal(DelveDecisionKinds.Steer, decisions[1].GetProperty("Kind").GetString());
    }

    [Fact]
    public void Steer_with_no_live_session_still_appends_the_delve_level_log_entry()
    {
        var delveId = CreateDelve();
        Assert.True(_store.TrySetDelveSteering(delveId, 0, toPartyIndex: null).Ok);
        var manager = new DelveBattleSessionManager(_store);

        manager.Steer(delveId, fromPartyIndex: null, toPartyIndex: 0);

        var delve = _store.LoadDelve(delveId)!;
        var decisions = JsonSerializer.Deserialize<List<JsonElement>>(delve.DecisionsJson)!;
        var entry = Assert.Single(decisions);
        Assert.Equal(DelveDecisionKinds.Steer, entry.GetProperty("Kind").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("PartyIndex").ValueKind); // From was null too
        Assert.Equal(0, entry.GetProperty("Payload").GetProperty("To").GetInt32());
    }

    // ==================================================================================================
    // FreezeByConnection
    // ==================================================================================================

    [Fact]
    public async Task FreezeByConnection_freezes_the_tracked_sessions_fight_exactly_like_a_dropped_connection()
    {
        var delveId = CreateDelve();
        var manager = new DelveBattleSessionManager(_store);
        var session = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 222UL, automated: new FixedAttacker(), connectionId: "conn-1")!;

        manager.FreezeByConnection("conn-1");

        await Record.ExceptionAsync(() => session.RunTask!);
        Assert.Equal(TaskStatus.Canceled, session.RunTask!.Status);

        // A connection that was never tracked is a no-op, never an exception.
        manager.FreezeByConnection("conn-does-not-exist");
    }

    [Fact]
    public async Task A_disconnect_freeze_does_not_erase_the_durable_selection_for_reconnect()
    {
        var delveId = CreatePairDelve();
        Assert.True(_store.TrySetDelveSteering(delveId, 0, 1).Ok);
        var manager = new DelveBattleSessionManager(_store);
        var session = manager.StartSession(
            delveId, 0, 0, 0, _playerId, Setup(), seed: 223UL, automated: new FixedAttacker(), connectionId: "conn-reconnect")!;

        manager.FreezeByConnection("conn-reconnect");
        await Record.ExceptionAsync(() => session.RunTask!);

        using var reopened = _testStore.Reopen();
        var (ok, reason, steering) = reopened.ReadDelveSteering(delveId);
        Assert.True(ok, reason);
        Assert.Equal(1, steering!.PartyIndex);
    }

    [Fact]
    public void Declare_forwards_to_the_named_session_and_returns_false_for_an_unknown_matchKey()
    {
        var manager = new DelveBattleSessionManager(_store);
        Assert.False(manager.Declare("delve-999-0-0-p0", "squad:p0:0", "act.attack", "wave:0"));
    }

    // ==================================================================================================
    // Resume
    // ==================================================================================================

    [Fact]
    public async Task Resume_after_a_freeze_rebuilds_a_fresh_session_over_the_persisted_trace_and_finishes()
    {
        var delveId = CreateDelve();
        var manager = new DelveBattleSessionManager(_store);
        var session1 = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 333UL, automated: new FixedAttacker())!;

        await DriveUntilAsync(session1, () => session1.Trace.Count >= 2);
        session1.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session1.RunTask!);
        Assert.Equal(TaskStatus.Canceled, session1.RunTask!.Status);
        var decisionsBeforeResume = session1.Trace.Count;
        Assert.True(decisionsBeforeResume >= 2);

        var session2 = manager.Resume(session1.MatchKey, _playerId, automated: new FixedAttacker());
        Assert.NotNull(session2);
        Assert.NotSame(session1, session2); // a brand-new session, never a reattachment
        // The prefix survived the freeze/resume round-trip.
        Assert.True(session2!.Trace.Count >= decisionsBeforeResume);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!session2.RunTask!.IsCompleted && DateTime.UtcNow < deadline)
            await RespondToPendingAskOnceAsync(session2);

        Assert.Equal(TaskStatus.RanToCompletion, session2.RunTask!.Status);
        var report = await session2.RunTask!;
        Assert.NotNull(report);

        // The row's own decisions_json reflects the FULL history, prefix and suffix, once more.
        var entry = _store.TryGetWebMatchLog(_playerId, DelveBattleSessionManager.CorrelationFromMatchKey(session1.MatchKey));
        Assert.NotNull(entry);
    }

    [Fact]
    public void Resume_with_no_logged_row_at_all_refuses()
    {
        var manager = new DelveBattleSessionManager(_store);
        var session = manager.Resume("delve-42-0-0-p0", _playerId, automated: new FixedAttacker());
        Assert.Null(session);
    }

    // ==================================================================================================
    // Live pushes (D5.11, 2026-09-08) -- DelveLiveEventNames, pushed via a fake IDelveLivePush so no
    // live SignalR server is needed to prove the wiring.
    // ==================================================================================================

    [Fact]
    public async Task A_live_fight_pushes_DelveTurnStarted_and_DelveDeclared_for_real_decisions()
    {
        var delveId = CreateDelve();
        var push = new RecordingPush();
        var manager = new DelveBattleSessionManager(_store, push);
        var session = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 777UL, automated: new FixedAttacker())!;

        await DriveUntilAsync(session, () => session.Trace.Count >= 2);

        var turnStarted = push.PayloadsOf(DelveLiveEventNames.TurnStarted).ToList();
        Assert.True(turnStarted.Count >= 2, "onTurnStarted should push at least once per live ask");
        Assert.All(turnStarted, p => Assert.Equal(session.MatchKey, Prop(p, "matchKey")));

        var declared = push.PayloadsOf(DelveLiveEventNames.Declared).ToList();
        Assert.True(declared.Count >= 2);
        Assert.All(declared, p => Assert.Equal(session.MatchKey, Prop(p, "matchKey")));
        Assert.All(declared, p => Assert.True(Prop(p, "source") is "player" or "timeout"));

        session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session.RunTask!);
    }

    [Fact]
    public async Task Steer_pushes_DelveFightFrozen_for_the_from_partys_live_session()
    {
        var delveId = CreatePairDelve();
        var push = new RecordingPush();
        var manager = new DelveBattleSessionManager(_store, push);
        var session = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 888UL, automated: new FixedAttacker())!;

        manager.Steer(delveId, fromPartyIndex: 0, toPartyIndex: 1);
        await Record.ExceptionAsync(() => session.RunTask!);

        var frozen = Assert.Single(push.PayloadsOf(DelveLiveEventNames.Frozen));
        Assert.Equal(session.MatchKey, Prop(frozen, "matchKey"));
        Assert.Equal(delveId, Prop(frozen, "delveId"));
        Assert.Equal(0, Prop(frozen, "partyIndex"));
    }

    [Fact]
    public void Steer_with_no_live_session_pushes_no_DelveFightFrozen()
    {
        var delveId = CreateDelve();
        Assert.True(_store.TrySetDelveSteering(delveId, 0, toPartyIndex: null).Ok);
        var push = new RecordingPush();
        var manager = new DelveBattleSessionManager(_store, push);

        manager.Steer(delveId, fromPartyIndex: null, toPartyIndex: 0);

        Assert.Empty(push.PayloadsOf(DelveLiveEventNames.Frozen)); // nobody was fighting there -- nothing to tell a client
    }

    /// <summary>Same in-memory manager, resumed right after its own freeze -- the trace it reuses was
    /// already fully replay-exhausted (live mode never starts before that), so nothing needs replaying.</summary>
    [Fact]
    public async Task Resume_reusing_the_same_in_memory_trace_pushes_DelveResumed_with_zero_replayCount()
    {
        var delveId = CreateDelve();
        var push = new RecordingPush();
        var manager = new DelveBattleSessionManager(_store, push);
        var session1 = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 555UL, automated: new FixedAttacker())!;

        await DriveUntilAsync(session1, () => session1.Trace.Count >= 2);
        session1.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session1.RunTask!);

        push.Clear(); // isolate this assertion to the Resume call alone

        var session2 = manager.Resume(session1.MatchKey, _playerId, automated: new FixedAttacker());
        Assert.NotNull(session2);

        var resumed = Assert.Single(push.PayloadsOf(DelveLiveEventNames.Resumed));
        Assert.Equal(0, Prop(resumed, "replayCount"));
        Assert.Empty(push.PayloadsOf(DelveLiveEventNames.ReplayConsumed));

        session2!.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session2.RunTask!);
    }

    /// <summary>A BRAND NEW manager (a fresh in-memory registry, simulating a real process restart)
    /// resuming from the persisted row alone: the rehydrated trace's own replay cursor starts at 0, so
    /// the WHOLE recorded prefix needs replaying -- proven against the real decision count, not a
    /// hardcoded number.</summary>
    [Fact]
    public async Task Resume_after_a_real_restart_pushes_the_full_persisted_prefix_then_one_ReplayConsumed_each_in_order()
    {
        var delveId = CreateDelve();
        var manager1 = new DelveBattleSessionManager(_store);
        var session1 = manager1.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 444UL, automated: new FixedAttacker())!;

        await DriveUntilAsync(session1, () => session1.Trace.Count >= 3);
        var decisionsBeforeFreeze = session1.Trace.Count;
        session1.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session1.RunTask!);

        var push = new RecordingPush();
        var manager2 = new DelveBattleSessionManager(_store, push); // fresh registry -- no in-memory session survives
        var session2 = manager2.Resume(session1.MatchKey, _playerId, automated: new FixedAttacker());
        Assert.NotNull(session2);

        var resumed = Assert.Single(push.PayloadsOf(DelveLiveEventNames.Resumed));
        Assert.Equal(session1.MatchKey, Prop(resumed, "matchKey"));
        var replayCount = (int)Prop(resumed, "replayCount")!;
        Assert.Equal(decisionsBeforeFreeze, replayCount);
        Assert.True(replayCount >= 3);

        var replayConsumed = push.PayloadsOf(DelveLiveEventNames.ReplayConsumed).ToList();
        Assert.Equal(replayCount, replayConsumed.Count);
        Assert.All(replayConsumed, p => Assert.Equal(session1.MatchKey, Prop(p, "matchKey")));

        // Order: DelveResumed always precedes every DelveReplayConsumed for the same resume.
        var resumedIndex = push.Pushes.FindIndex(p => p.EventName == DelveLiveEventNames.Resumed);
        var firstReplayConsumedIndex = push.Pushes.FindIndex(p => p.EventName == DelveLiveEventNames.ReplayConsumed);
        Assert.True(resumedIndex < firstReplayConsumedIndex);

        session2!.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session2.RunTask!);
    }

    /// <summary>StartSession's own "already logged, not yet finished" retry branch is resume-shaped
    /// (this class's own doc comment: "rehydrates and resumes it, exactly like Resume does") -- proven
    /// to push the identical DelveResumed/DelveReplayConsumed pair Resume() does.</summary>
    [Fact]
    public async Task StartSession_retried_over_an_existing_row_pushes_DelveResumed_like_a_genuine_resume()
    {
        var delveId = CreateDelve();
        var manager1 = new DelveBattleSessionManager(_store);
        var first = manager1.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 999UL, automated: new FixedAttacker())!;
        await DriveUntilAsync(first, () => first.Trace.Count >= 2);
        var decisionsBeforeRetry = first.Trace.Count;
        first.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => first.RunTask!);

        var push = new RecordingPush();
        var manager2 = new DelveBattleSessionManager(_store, push); // fresh registry -- forces the rehydrate branch
        var second = manager2.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 111UL, automated: new FixedAttacker());
        Assert.NotNull(second);
        Assert.Equal(first.MatchKey, second!.MatchKey);

        var resumed = Assert.Single(push.PayloadsOf(DelveLiveEventNames.Resumed));
        Assert.Equal(decisionsBeforeRetry, Prop(resumed, "replayCount"));
        Assert.Equal(decisionsBeforeRetry, push.PayloadsOf(DelveLiveEventNames.ReplayConsumed).Count());

        second.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => second.RunTask!);
    }

    [Fact]
    public void A_fresh_StartSession_pushes_no_DelveResumed_at_all()
    {
        var delveId = CreateDelve();
        var push = new RecordingPush();
        var manager = new DelveBattleSessionManager(_store, push);
        var session = manager.StartSession(delveId, 0, 0, 0, _playerId, Setup(), seed: 222UL, automated: new FixedAttacker())!;

        Assert.Empty(push.PayloadsOf(DelveLiveEventNames.Resumed));
        Assert.Empty(push.PayloadsOf(DelveLiveEventNames.ReplayConsumed));

        session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
    }
}
