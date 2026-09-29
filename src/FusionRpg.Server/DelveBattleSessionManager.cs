using System.Text.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Delve.Battle;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// D2.16 — the Server-layer registry of live <see cref="DelveBattleSession"/>s, and the ONLY place
/// this program derives the match key / correlation id spec-delve-battle-profile.md §4b names for a
/// delve-room fight: <c>correlation delve:{delveId}:{r}:{c}:p{partyIndex}</c>,
/// <c>matchKey delve-{delveId}-{r}-{c}-p{partyIndex}</c> (colon-free, "same reason" as an expedition's
/// own <c>exp:{id}:{n}</c> / dash form).
///
    /// <para><b>What this class does NOT do, named honestly.</b> It does not decide which room's
    /// <see cref="BattleSetup"/> a party is currently fighting, and it does not build a competent
    /// automated policy — both are genuinely separate, still-unbuilt content-wiring tasks (the spec's
    /// own §3 names a real "siege-ai-class policy" as a CONSUMED dependency, and `SiegeAi.PlayedSide` is
    /// never set in production). <see cref="StartSession"/> takes both as parameters — matching
    /// <c>DelveBattle.Run</c>'s own shape exactly — for whichever future "a party arrived at a fight
    /// room" trigger calls it; today that call's one production caller is <c>DelveBattleSession</c>
    /// (<c>DelveBattleSession.cs</c>, where the room's battle actually runs).
    /// <see cref="ResolveRoomEncounter"/> (species-gear-chain T20) is the production caller for
    /// <c>Encounter.Build</c>: room resolution produces the enemy half, and the same future trigger
    /// feeds it to <see cref="StartSession"/>.</para>
///
/// <para><b>Persisted steering.</b> The manager does not keep a second in-memory steering map. The
/// Data-owned <c>rpg_delves.steering_json</c> record is the authority; <see cref="TrySteer"/> validates
/// and persists the request before the existing live-session freeze/log side effect runs.</para>
/// </summary>
public sealed class DelveBattleSessionManager
{
    readonly RpgStore _store;
    readonly IDelveLivePush? _push;

    /// <summary>
    /// combat-ai `replay-identity` (CAI2.2, spec-replay-identity.md §2/§5): the version-addressed profile
    /// set source this manager stamps a room fight with and re-checks on BOTH of its resume entries
    /// (<see cref="StartSession"/>'s rehydrate branch and <see cref="Resume"/>) — the order-independent
    /// pair the spec's testing section names, since a rehydrate is a resume reached a different way.
    /// <para>
    /// Optional and last, so every existing construction site compiles unchanged, and null is honoured
    /// rather than defaulted: no source means no stamp is written (the legacy shape) and a stamped row
    /// is refused by name, never re-resolved on the current profile. Production wires it in
    /// <c>Program.cs</c>.
    /// </para>
    /// </summary>
    readonly ICombatAiProfileSource? _combatAiProfiles;

    readonly BattleSessionRegistry _registry = new();

    readonly object _gate = new();
    readonly Dictionary<string, DelveBattleSession> _sessions = new(StringComparer.Ordinal);
    // (delveId, partyIndex) -> the one matchKey currently live/frozen for that party, so Steer/
    // OnDisconnected can find "whatever this party (or this connection) was fighting" without the
    // caller re-deriving the room coordinates.
    readonly Dictionary<(long DelveId, int PartyIndex), string> _activeMatchKeyForParty = new();
    readonly Dictionary<string, string> _matchKeyForConnection = new(StringComparer.Ordinal);

    /// <param name="push">D5.11's live-push wave (2026-09-08) — optional so every existing caller
    /// (including every test constructed before this wave) keeps compiling with no behaviour change:
    /// a session with no push configured runs exactly as before, silently. See
    /// <see cref="DelveLiveEventNames"/> for the wire vocabulary this manager pushes.</param>
    /// <param name="combatAiProfiles">CAI2.2's version-addressed profile source — optional, last, and
    /// defaulted to nothing on purpose (see the field's own note): a delve row that cannot resolve the
    /// profile set it was stamped with is REFUSED here rather than resumed under whatever profile
    /// happens to be published now.</param>
    public DelveBattleSessionManager(
        RpgStore store, IDelveLivePush? push = null, ICombatAiProfileSource? combatAiProfiles = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _push = push;
        _combatAiProfiles = combatAiProfiles;
    }

    public static string MatchKeyFor(long delveId, int row, int col, int partyIndex) =>
        $"delve-{delveId}-{row}-{col}-p{partyIndex}";

    public static string CorrelationFor(long delveId, int row, int col, int partyIndex) =>
        $"delve:{delveId}:{row}:{col}:p{partyIndex}";

    /// <summary>The two id shapes are position-for-position identical (spec §4b: "colon-free, same
    /// reason") — every field in both is a plain non-negative integer or the literal prefixes
    /// `delve`/`p`, none of which ever contain a dash, so a genuine resume (built from a persisted
    /// matchKey alone, e.g. after a server restart with no in-memory session left) recovers the
    /// correlation id with a plain character swap rather than needing a second stored column.</summary>
    internal static string CorrelationFromMatchKey(string matchKey) => matchKey.Replace('-', ':');

    static (long DelveId, int PartyIndex) ParseMatchKey(string matchKey)
    {
        // "delve-{delveId}-{r}-{c}-p{partyIndex}"
        var parts = matchKey.Split('-');
        if (parts.Length != 5 || parts[0] != "delve" || parts[4].Length < 2 || parts[4][0] != 'p')
            throw new ArgumentException($"not a delve battle match key: '{matchKey}'", nameof(matchKey));
        return (long.Parse(parts[1]), int.Parse(parts[4][1..]));
    }

    public DelveBattleSession? Find(string matchKey)
    {
        lock (_gate) return _sessions.TryGetValue(matchKey, out var s) ? s : null;
    }

    public DelveBattleSession? FindActiveForParty(long delveId, int partyIndex)
    {
        lock (_gate)
            return _activeMatchKeyForParty.TryGetValue((delveId, partyIndex), out var key) && _sessions.TryGetValue(key, out var s)
                ? s : null;
    }

    void Register(DelveBattleSession session, string? connectionId)
    {
        lock (_gate)
        {
            _sessions[session.MatchKey] = session;
            _activeMatchKeyForParty[(session.DelveId, session.PartyIndex)] = session.MatchKey;
            if (connectionId is not null) _matchKeyForConnection[connectionId] = session.MatchKey;
        }
    }

    /// <summary>A connection is now the one steering <paramref name="matchKey"/> — recorded so
    /// <see cref="FreezeByConnection"/> (the hub's <c>OnDisconnectedAsync</c>) can freeze the right
    /// fight without the disconnect handler re-deriving room coordinates itself.</summary>
    public void TrackConnection(string connectionId, string matchKey)
    {
        lock (_gate) _matchKeyForConnection[connectionId] = matchKey;
    }

    /// <summary>
    /// species-gear-chain T20: the production caller for <see cref="Encounter.Build"/>. Resolves a
    /// real delve room's encounter anchor through the shipped selector and returns the enemy half —
    /// making `offClimateMilli`, `sameSpeciesMaxMilli` and `threatWindow.bossFloorRung` live for the
    /// first time (all three ship in tuning already; this is the first production path that reads
    /// them). The future "a party arrived at a fight room" trigger feeds the returned half to
    /// <see cref="StartSession"/> alongside the party half.
    ///
    /// <para><b>Admission.</b> The corpus is filtered by <see cref="CreatureAdmission.ForDelve"/>
    /// (delve admits `CaptureOnly`, refuses `EventOnly` — the decided rule, matching the map) via a
    /// catalog lookup per anchor. A species the catalog does not know fails closed at lookup time,
    /// never by silently dropping the anchor.</para>
    ///
    /// <para><b>Refusal.</b> <see cref="EncounterRefusal"/> propagates uncaught — a room that cannot
    /// be filled is a real, reportable condition (and, with `threat-band-fill` landed, an early
    /// signal the prerequisite did not), never a reason to fall back to a default species. Callers
    /// must not swallow it: there is no session yet (hence no matchKey to report against), so the
    /// refusal itself, naming slot and filter, IS the report.</para>
    ///
    /// <para><b>Determinism.</b> The build is deterministic per `seed` — same inputs, same enemies in
    /// the same order. `seed` must already be room-specific (the caller's own
    /// `SeededRng.DeriveStream(dungeonSeed, "room:{r}:{c}")` derivation); like
    /// <see cref="Encounter.Build"/>, this method takes uniqueness as given, never re-derives it.
    /// All content (anchors, corpus rows, tunings) arrives as parameters — this method reads no disk,
    /// matching `DomainEncounterPreflight.Build`'s and `EncounterCorpusBuilder.Build`'s own shape.</para>
    /// </summary>
    public EncounterHalf ResolveRoomEncounter(
        string encounterRef,
        int roomTheta,
        ElementTypeId? climate,
        RaidModeTuning raid,
        DifficultyRungTuning rung,
        ulong seed,
        IReadOnlyDictionary<string, EncounterAnchor> encountersById,
        IReadOnlyList<ConcreteAnchor> corpus,
        EncounterTuning tuning,
        CreatureThreatTuning threatTuning,
        AptitudeTuning? aptitudeTuning = null,
        PowerTuning? powerTuning = null)
    {
        if (encounterRef is null) throw new ArgumentNullException(nameof(encounterRef));
        if (raid is null) throw new ArgumentNullException(nameof(raid));
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (encountersById is null) throw new ArgumentNullException(nameof(encountersById));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));

        if (!encountersById.TryGetValue(encounterRef, out var anchor))
            throw new InvalidOperationException(
                $"encounterRef '{encounterRef}' names no encounter anchor — a room pointing at a missing encounter is a content error, not an empty room.");

        // Corpus anchors carry authoring-case ids ("AbyssSwordStar"); the catalog's canonical form
        // is lower-kebab (`Validate` rejects anything else), so normalize exactly the way the
        // contract defines it — the same inline `Trim().ToLowerInvariant()` shape
        // `CreatureRarityIds` already uses, not a second canonicalization rule.
        var admitted = corpus
            .Where(a => CreatureAdmission.ForDelve(CreatureSpeciesCatalog.Get(
                (a.SpeciesId ?? "").Trim().ToLowerInvariant())))
            .ToList();

        return Encounter.Build(
            anchor, roomTheta, climate, raid, rung, seed, admitted, tuning, threatTuning,
            aptitudeTuning, powerTuning);
    }

    /// <summary>
    /// Starts a fresh session for a room's fight, OR — if a row for this exact correlation already
    /// exists (a retried "start" call, or a session that outlived an in-memory eviction) — rehydrates
    /// and resumes it, exactly like <see cref="Resume"/> does. This mirrors
    /// <c>WebMatchService.RunWebMatchAsync</c>'s own "the atomic append IS the replay gate" discipline:
    /// two concurrent starts for the same room+party can never fork two independent battles.
    /// </summary>
    public DelveBattleSession? StartSession(
        long delveId, int row, int col, int partyIndex, long playerId,
        BattleSetup setup, ulong seed, IIntentSource automated,
        ActionCatalog? actionCatalog = null, IContainerEffectResolver? containerResolver = null,
        string? connectionId = null)
    {
        var matchKey = MatchKeyFor(delveId, row, col, partyIndex);
        var correlationId = CorrelationFor(delveId, row, col, partyIndex);

        var (created, entry) = _store.AppendWebMatchLog(
            playerId, correlationId, matchKey, JsonSerializer.Serialize(setup), seed,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion,
            BattleEnvironment.Stamp, _store.ComputeContentHash().ToCompact(),
            profileId: FusionRpg.Core.Battle.Timeline.BattleModeProfileCatalog.DelveId,
            // combat-ai `replay-identity` §5, stamping site 3 of 3. ⛔ A DIFFERENT VOCABULARY from the
            // `profileId` above: that is the BattleModeProfile id (the timeline mode), this is the
            // combat-ai profile SET's stamp. Same row, two columns, never overloaded.
            combatAiProfile: CombatAiProfilePin.StampFor(_combatAiProfiles));

        DecisionTrace trace;
        BattleSetup effectiveSetup;
        ulong effectiveSeed;
        var replayCount = 0;
        if (created)
        {
            trace = new DecisionTrace();
            effectiveSetup = setup;
            effectiveSeed = seed;
        }
        else
        {
            // Already logged (a retry, or a still-open session) -- the STORED row is authoritative,
            // matching RunPlannedMatchAsync's own "on replay the STORED setup wins" precedent. This
            // branch is genuinely resume-shaped (this method's own doc comment: "rehydrates and resumes
            // it, exactly like Resume does"), so it pushes the identical DelveResumed/DelveReplayConsumed
            // events Resume() does, below.
            if (entry.RunId is not null) return null;   // already finished and ingested -- nothing to (re)start
            // The pin, on this entry as well as on Resume: this branch IS a resume (its own doc comment
            // says so), so a row whose profile set cannot be resolved here is refused exactly as it
            // would be in Resume. Same return shape this method already uses for an unreplayable row,
            // with the reason logged — spec §2's "the same return, with the reason logged".
            var pin = CombatAiProfilePin.Resolve(_combatAiProfiles, entry.CombatAiProfile);
            if (pin.IsRefused)
            {
                Console.Error.WriteLine($"[delve-battle] refused to rehydrate {matchKey}: {pin.Refusal} — {pin.Detail}");
                return null;
            }
            var rehydrated = DecisionTrace.FromJson(entry.DecisionsJson);
            trace = rehydrated ?? new DecisionTrace();
            replayCount = trace.Count; // a freshly-rehydrated trace's own cursor starts at 0 -- the
                                        // whole persisted prefix still needs replaying
            var storedSetup = JsonSerializer.Deserialize<BattleSetup>(entry.SetupJson);
            if (storedSetup is null) return null;
            effectiveSetup = storedSetup;
            effectiveSeed = entry.Seed;
        }

        _registry.Close(matchKey); // clear any stale bookkeeping so Start()'s own Open never throws
        var steeredKeys = IntentRouter.KeysForParty(effectiveSetup, partyIndex);
        var session = new DelveBattleSession(
            matchKey, delveId, partyIndex, playerId, effectiveSetup, effectiveSeed, trace,
            automated, steeredKeys, _registry, actionCatalog, containerResolver,
            // T74/A33: the store-owning caller supplies the unlock pair, so a delve fight prices a held
            // action at its holder's effective rung. Same shape WebMatchService uses.
            unlockStateFor: _store.UnlockStateFor(effectiveSetup),
            unlockTuning: FusionRpg.Core.Actions.Unlock.UnlockTuningPolicy.Tuning,
            onDecisionPersisted: t => _store.WriteWebMatchDecisions(entry.Id, t.ToJson()),
            onFrozen: payload => { AppendSteerLogEntry(delveId, payload); PushFrozen(matchKey, delveId, partyIndex); },
            onDeclared: d => PushDeclared(matchKey, d),
            onTurnStarted: (actorKey, dwellMs) => PushTurnStarted(matchKey, actorKey, dwellMs));

        if (!created)
        {
            PushResumed(matchKey, replayCount);
            for (var i = 0; i < replayCount; i++) PushReplayConsumed(matchKey);
        }

        session.Start();
        Register(session, connectionId);
        return session;
    }

    /// <summary>
    /// Rebuilds a session from the persisted <c>(setup_json, seed, decisions_json)</c> row after a
    /// freeze — a BRAND NEW session, never a reattachment to a still-running background <c>Task</c>
    /// (spec §4b, verbatim). Reuses the still-open in-memory trace object when one exists (the same
    /// process never restarted) so the registry's own bookkeeping and this session's `Trace` never
    /// diverge into two objects describing the same battle; rehydrates from JSON only when no
    /// in-memory session survived (a real restart, or an evicted entry).
    /// </summary>
    public DelveBattleSession? Resume(
        string matchKey, long playerId, IIntentSource automated,
        ActionCatalog? actionCatalog = null, IContainerEffectResolver? containerResolver = null,
        string? connectionId = null)
    {
        var correlationId = CorrelationFromMatchKey(matchKey);
        var entry = _store.TryGetWebMatchLog(playerId, correlationId);
        if (entry is null || entry.RunId is not null) return null;

        // combat-ai `replay-identity` (spec §2): the pin, checked before any rehydrate. An absent or
        // incomplete trace already refuses below ("never re-resolves blind"); so does a profile set this
        // host cannot supply truthfully. Both return null — the method's own established answer for a
        // row it will not resume — and both say why in the log.
        var pin = CombatAiProfilePin.Resolve(_combatAiProfiles, entry.CombatAiProfile);
        if (pin.IsRefused)
        {
            Console.Error.WriteLine($"[delve-battle] refused to resume {matchKey}: {pin.Refusal} — {pin.Detail}");
            return null;
        }

        var existing = _registry.Find(matchKey);
        DecisionTrace trace;
        int replayCount;
        if (existing is { State: BattleSessionState.Disconnected } && existing.PlayerId == playerId)
        {
            trace = existing.Trace; // same process -- reuse, never fork a second trace object
            // Live mode only ever starts once ReplayExhausted (InteractiveIntentSource.TryDeclare), and
            // Freeze only ever fires from inside a live Ask() or an external Steer/disconnect -- never
            // mid-replay (Replay() itself observes no cancellation token) -- so a session that got as
            // far as freezing had already fully exhausted its own replay before this moment.
            replayCount = 0;
        }
        else
        {
            var rehydrated = DecisionTrace.FromJson(entry.DecisionsJson);
            if (rehydrated is null) return null; // spec §9: an absent/incomplete trace refuses, never re-resolves blind
            trace = rehydrated;
            replayCount = trace.Count; // a freshly-rehydrated trace's own cursor starts at 0 -- the
                                        // whole persisted prefix still needs replaying
        }

        var setup = JsonSerializer.Deserialize<BattleSetup>(entry.SetupJson);
        if (setup is null) return null;

        var (delveId, partyIndex) = ParseMatchKey(matchKey);
        var steeredKeys = IntentRouter.KeysForParty(setup, partyIndex);

        _registry.Close(matchKey);
        var session = new DelveBattleSession(
            matchKey, delveId, partyIndex, playerId, setup, entry.Seed, trace,
            automated, steeredKeys, _registry, actionCatalog, containerResolver,
            // T74/A33: a resumed session prices exactly like the fight it resumes, so it takes the
            // same unlock pair -- read from the stored setup it will actually resolve.
            unlockStateFor: _store.UnlockStateFor(setup),
            unlockTuning: FusionRpg.Core.Actions.Unlock.UnlockTuningPolicy.Tuning,
            onDecisionPersisted: t => _store.WriteWebMatchDecisions(entry.Id, t.ToJson()),
            onFrozen: payload => { AppendSteerLogEntry(delveId, payload); PushFrozen(matchKey, delveId, partyIndex); },
            onDeclared: d => PushDeclared(matchKey, d),
            onTurnStarted: (actorKey, dwellMs) => PushTurnStarted(matchKey, actorKey, dwellMs));

        PushResumed(matchKey, replayCount);
        for (var i = 0; i < replayCount; i++) PushReplayConsumed(matchKey);

        session.Start();
        Register(session, connectionId);
        return session;
    }

    public bool Declare(string matchKey, string actorKey, string actionId, string? targetKey) =>
        Find(matchKey)?.Declare(actorKey, actionId, targetKey) ?? false;

    /// <summary>
    /// Validate and persist the canonical steering record, then preserve the existing freeze/log
    /// behavior. A refusal performs neither the steering write nor the decision-log append.
    /// </summary>
    public (bool Ok, string Reason) TrySteer(
        long delveId, int? fromPartyIndex, int? toPartyIndex, long? playerId = null)
    {
        var persisted = _store.TrySetDelveSteering(delveId, fromPartyIndex, toPartyIndex, playerId);
        if (!persisted.Ok) return (false, persisted.Reason);

        var payload = LiveFreezeTrigger.SteerPayload(fromPartyIndex, toPartyIndex);
        var session = fromPartyIndex is { } from ? FindActiveForParty(delveId, from) : null;
        if (session is not null)
            session.Freeze(payload); // appends via onFrozen -- see below
        else
            AppendSteerLogEntry(delveId, payload);
        return (true, "");
    }

    /// <summary>Compatibility-shaped call site for the existing session tests and server code. Refusals
    /// are intentionally silent here; callers that need the named reason use <see cref="TrySteer"/>.</summary>
    public void Steer(long delveId, int? fromPartyIndex, int? toPartyIndex) =>
        TrySteer(delveId, fromPartyIndex, toPartyIndex);

    /// <summary>The hub's own <c>OnDisconnectedAsync</c> — freezes whatever this connection was
    /// steering, recording the SAME implicit "nobody is steering now" shape a triple-timeout would.
    /// This transient freeze does not erase the canonical durable selection: reconnect/restart must
    /// be able to read the same persisted party back. A connection that was never tracked (never
    /// called Steer/Declare) is a no-op.</summary>
    public void FreezeByConnection(string connectionId)
    {
        string? matchKey;
        lock (_gate)
        {
            if (!_matchKeyForConnection.TryGetValue(connectionId, out matchKey)) return;
            _matchKeyForConnection.Remove(connectionId);
        }

        if (Find(matchKey) is { } session)
            session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(session.PartyIndex));
    }

    // ---- live pushes (D5.11, 2026-09-08) -- see DelveLiveEventNames for the wire vocabulary ---------

    void PushDeclared(string matchKey, TracedDecision decision) =>
        _push?.Push(DelveLiveEventNames.Declared, new
        {
            matchKey,
            actorKey = decision.ActorKey,
            source = decision.Source == DecisionSource.Player ? "player" : "timeout"
        });

    void PushTurnStarted(string matchKey, string actorKey, int dwellMs) =>
        _push?.Push(DelveLiveEventNames.TurnStarted, new { matchKey, actorKey, dwellMs });

    void PushFrozen(string matchKey, long delveId, int partyIndex) =>
        _push?.Push(DelveLiveEventNames.Frozen, new { matchKey, delveId, partyIndex });

    void PushResumed(string matchKey, int replayCount) =>
        _push?.Push(DelveLiveEventNames.Resumed, new { matchKey, replayCount });

    void PushReplayConsumed(string matchKey) =>
        _push?.Push(DelveLiveEventNames.ReplayConsumed, new { matchKey });

    void AppendSteerLogEntry(long delveId, SteerLogPayload payload)
    {
        var delve = _store.LoadDelve(delveId);
        var seq = CountDecisions(delve?.DecisionsJson);
        // PartyIndex on the entry names the party this decision is fundamentally ABOUT -- the one
        // being switched (or frozen) away from; both the implicit-freeze and explicit-steer shapes
        // always carry a From, so this is unambiguous either way.
        _store.AppendDecision(delveId, DelveDecision.Create(seq, DelveDecisionKinds.Steer, payload.From, payload: payload));
    }

    static int CountDecisions(string? decisionsJson)
    {
        if (string.IsNullOrWhiteSpace(decisionsJson)) return 0;
        var list = JsonSerializer.Deserialize<List<JsonElement>>(decisionsJson);
        return list?.Count ?? 0;
    }
}
