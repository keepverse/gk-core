using System.Collections.Concurrent;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Delve.Battle;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// D2.16 — the real threading harness: <see cref="DelveBattleSession"/> runs a live delve fight on a
/// dedicated background <see cref="Task"/>, with a bounded, cancellable dwell for the steered actor's
/// turn. These are genuine multi-threaded tests (real <c>Task.Run</c>, real
/// <see cref="Task.WaitAny(Task[], int, CancellationToken)"/> waits) — no live HTTP server needed, per
/// D2.16's own brief ("testable without a live HTTP server, just real Tasks and real cancellation").
///
/// <para>Every fixture here uses a large level gap (squad heavily overleveled) so the battle survives
/// long enough for the steered actor to be asked several times before anyone could die — the point of
/// every test is the FREEZE mechanism, not real combat balance.</para>
/// </summary>
[Trait("VerificationId", "server.delve-live-push")]
public class DelveBattleSessionTests
{
    public DelveBattleSessionTests() => DelveBattleTuningTestFixture.ConfigureRealBattleTuning();

    // Both sides carry an enormous MaxHp (far beyond anything a handful of real attacks could drain)
    // so the fight cannot end early on its own -- it always runs the full MaxRounds horizon (50,
    // this test bootstrap's own DefaultBattle) unless a test explicitly freezes it first. The point
    // of every test here is the FREEZE mechanism, never real combat balance.
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
        Wave = new[] { WaveActor("wave:0"), WaveActor("wave:1") },
    };

    /// <summary>Attacks a fixed opposing key regardless of who is asked — enough to keep a real battle
    /// progressing without needing an <see cref="IBattleView"/> (which no external caller of
    /// <c>BattleEngine.Resolve</c> can construct ahead of time, confirmed by
    /// <see cref="FusionRpg.Core.Actions.IntentRouter"/>'s own doc comment).</summary>
    sealed class FixedAttacker : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) => new(
            "act.attack",
            actorKey.StartsWith("squad", StringComparison.Ordinal) ? "wave:0" : "squad:p0:0",
            BattleEngine.BasicAttackEnvelope);
    }

    static DelveBattleSession NewSession(
        BattleSessionRegistry registry, out ConcurrentQueue<TracedDecision> persisted,
        out ConcurrentQueue<SteerLogPayload> frozen, int dwellMs = 5)
    {
        return NewSession(registry, out persisted, out frozen, out _, out _, dwellMs);
    }

    /// <summary>D5.11's live-push wave (2026-09-08) — the same fixture, additionally capturing
    /// <c>onDeclared</c>/<c>onTurnStarted</c> so a test can assert exactly when and how often each new
    /// hook fires without touching SignalR at all (<see cref="DelveBattleSessionManagerTests"/> covers
    /// the actual wire push).</summary>
    /// <para><b>Every capture below is a <see cref="ConcurrentQueue{T}"/>, not a <c>List&lt;T&gt;</c>
    /// (2026-09-17).</b> All four callbacks fire on the session's own background battle Task, while the
    /// test thread reads the same collection to decide when to stop driving. A plain <c>List&lt;T&gt;</c>
    /// written on one thread and enumerated on another is a data race in its own right — it can miss an
    /// element, or throw out of <c>Add</c>/enumeration, for reasons that have nothing to do with the
    /// behaviour under test. <c>ConcurrentQueue</c> keeps enqueue order, so the sequence assertions below
    /// still mean what they say, and <c>ToArray()</c> gives an atomic snapshot to assert against.</para>
    static DelveBattleSession NewSession(
        BattleSessionRegistry registry, out ConcurrentQueue<TracedDecision> persisted,
        out ConcurrentQueue<SteerLogPayload> frozen, out ConcurrentQueue<TracedDecision> declared,
        out ConcurrentQueue<(string ActorKey, int DwellMs)> turnsStarted, int dwellMs = 5)
    {
        var localPersisted = new ConcurrentQueue<TracedDecision>();
        var localFrozen = new ConcurrentQueue<SteerLogPayload>();
        var localDeclared = new ConcurrentQueue<TracedDecision>();
        var localTurnsStarted = new ConcurrentQueue<(string, int)>();
        var setup = Setup();
        var steeredKeys = FusionRpg.Core.Actions.IntentRouter.KeysForParty(setup, partyIndex: 0);
        var session = new DelveBattleSession(
            matchKey: "delve-1-0-0-p0", delveId: 1, partyIndex: 0, playerId: 1,
            setup, seed: 12345UL, trace: new DecisionTrace(),
            automated: new FixedAttacker(), steeredKeys, registry,
            onDecisionPersisted: t => localPersisted.Enqueue(t.Decisions[^1]),
            onFrozen: p => localFrozen.Enqueue(p),
            onDeclared: d => localDeclared.Enqueue(d),
            onTurnStarted: (actorKey, ms) => localTurnsStarted.Enqueue((actorKey, ms)),
            dwellMs: dwellMs);
        persisted = localPersisted;
        frozen = localFrozen;
        declared = localDeclared;
        turnsStarted = localTurnsStarted;
        return session;
    }

    [Fact]
    public async Task Three_consecutive_timeouts_freeze_the_session_and_cancel_the_run_with_no_report_ever_produced()
    {
        var registry = new BattleSessionRegistry();
        var session = NewSession(registry, out var persisted, out var frozenLog);

        session.Start();

        // Nobody ever declares -- every ask times out. Wait on the FREEZE LOG, never on `session.Frozen`
        // (see WaitForFreezeAsync for why the difference is load-bearing).
        var payload = await WaitForFreezeAsync(session, frozenLog);

        Assert.True(session.Frozen, "session never froze");
        Assert.Equal(0, payload.From);
        Assert.Null(payload.To);

        // The background Task must genuinely observe the cancellation -- awaiting it lets the
        // OperationCanceledException actually propagate to THIS thread's Task, proving it was not
        // silently swallowed anywhere in the call stack.
        var ex = await Record.ExceptionAsync(() => session.RunTask!);
        Assert.NotNull(ex); // Task.WaitAsync/await surfaces a TaskCanceledException for a Canceled task
        Assert.Equal(TaskStatus.Canceled, session.RunTask!.Status);
        Assert.False(session.RunTask.IsCompletedSuccessfully);

        // NO BattleReport was ever produced for this attempt -- cancel-and-discard, not
        // pause-and-resume. There is no report to inspect anywhere: RunTask never RanToCompletion.
        Assert.True(session.RunTask.IsCanceled);

        // At least 3 timeout decisions were actually persisted (onDecisionPersisted fired for each).
        // Every one of them was enqueued BEFORE the freeze callback this test already waited for
        // (`OnRecorded` declares, then asks the trigger), so this count is settled, not racing.
        Assert.True(persisted.ToArray().Count(d => d.Source == DecisionSource.Timeout) >= 3);

        Assert.Equal(BattleSessionState.Disconnected, registry.Find(session.MatchKey)!.State);
        Assert.False(registry.MayWrite(session.MatchKey)); // never blessed as a written result
    }

    [Fact]
    public async Task Freezing_mid_fight_cancels_the_background_task_and_produces_no_report()
    {
        var registry = new BattleSessionRegistry();
        // A huge dwell -- this session would never naturally time out inside this test's own window,
        // so an observed freeze can only be the EXPLICIT Freeze() call below, not the 3-timeout path.
        var session = NewSession(registry, out _, out var frozenLog, dwellMs: 60_000);

        session.Start();
        await Task.Delay(30); // let the background Task actually start blocking inside Ask()

        var froze = session.Freeze(LiveFreezeTrigger.SteerPayload(from: 0, to: 1));
        Assert.True(froze);
        Assert.False(session.Freeze(LiveFreezeTrigger.SteerPayload(0, 1))); // idempotent -- already frozen

        // No wait needed here: THIS thread called Freeze, so `_onFrozen` already ran synchronously
        // on it before the call returned.
        var payload = Assert.Single(frozenLog);
        Assert.Equal(0, payload.From);
        Assert.Equal(1, payload.To);

        await Record.ExceptionAsync(() => session.RunTask!);
        Assert.Equal(TaskStatus.Canceled, session.RunTask!.Status);
        Assert.Equal(BattleSessionState.Disconnected, registry.Find(session.MatchKey)!.State);
    }

    [Fact]
    public async Task A_declared_player_choice_resets_the_timeout_count_and_the_fight_keeps_running()
    {
        var registry = new BattleSessionRegistry();
        // Both squad actors share PartyIndex 0, so BOTH are steered -- a generous dwell means neither
        // one's ask can time out while this test is actively responding to whichever is pending.
        var session = NewSession(registry, out var persisted, out var frozenLog, dwellMs: 2000);

        session.Start();

        // Declaring for an actor that is NOT currently pending is refused -- proven directly, without
        // guessing which of the two steered actors the engine asks first.
        var deadline0 = DateTime.UtcNow.AddSeconds(5);
        while (session.PendingActorKey is null && DateTime.UtcNow < deadline0) await Task.Delay(2);
        var pending = session.PendingActorKey;
        Assert.NotNull(pending);
        var notPending = pending == "squad:p0:0" ? "squad:p0:1" : "squad:p0:0";
        Assert.False(session.Declare(notPending, "act.attack", "wave:0"));

        // Respond to a handful of real turns (whichever actor the engine asks), driven the same way
        // the byte-identical-resume test does -- proves the fight keeps running under real Player
        // decisions, never freezing.
        await DriveUntilAsync(session, () => persisted.Count(d => d.Source == DecisionSource.Player) >= 3);

        Assert.False(session.Frozen);
        Assert.Empty(frozenLog);
        Assert.Contains(persisted, d => d.Source == DecisionSource.Player);

        // Clean up -- freeze so the background Task does not keep running past this test.
        session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session.RunTask!);
    }

    /// <summary>D5.11 (2026-09-08) — <c>onTurnStarted</c> fires once per <c>Ask</c> call: every live turn,
    /// never zero times, and always with the SAME dwell this session was built with (a real, useful
    /// diagnostic — a countdown UI needs the exact window, not just "some turn started").</summary>
    [Fact]
    public async Task OnTurnStarted_fires_once_per_ask_with_the_sessions_own_dwell()
    {
        var registry = new BattleSessionRegistry();
        const int dwellMs = 2000;
        var session = NewSession(registry, out var persisted, out _, out _, out var turnsStarted, dwellMs);

        session.Start();
        await DriveUntilAsync(session, () => persisted.Count(d => d.Source == DecisionSource.Player) >= 3);

        var started = turnsStarted.ToArray();
        Assert.True(started.Length >= 3, "onTurnStarted should have fired at least once per live ask");
        Assert.All(started, t => Assert.Equal(dwellMs, t.DwellMs));
        Assert.All(started, t => Assert.True(t.ActorKey is "squad:p0:0" or "squad:p0:1" or "wave:0" or "wave:1"));

        session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session.RunTask!);
    }

    /// <summary>D5.11 (2026-09-08) — <c>onDeclared</c> fires exactly once per NEW decision (matching
    /// <c>onDecisionPersisted</c>'s own count 1:1) and carries the real <see cref="DecisionSource"/>, so
    /// a timeout is never misreported as a player choice or vice versa.</summary>
    [Fact]
    public async Task OnDeclared_fires_once_per_new_decision_with_the_real_source()
    {
        var registry = new BattleSessionRegistry();
        var session = NewSession(registry, out var persisted, out _, out var declared, out _, dwellMs: 2000);

        session.Start();
        await DriveUntilAsync(session, () => persisted.Count(d => d.Source == DecisionSource.Player) >= 3);

        // Snapshot both before comparing: the battle Task is still live at this point, so comparing the
        // queues directly could read one mid-enqueue and report a length difference that never existed.
        var persistedSeen = persisted.ToArray();
        var declaredSeen = declared.ToArray();
        Assert.Equal(persistedSeen.Length, declaredSeen.Length);
        Assert.Equal(persistedSeen, declaredSeen); // TracedDecision is a record struct -- sequence equality is real equality
        Assert.Contains(declaredSeen, d => d.Source == DecisionSource.Player);

        session.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => session.RunTask!);
    }

    /// <summary>D5.11 (2026-09-08) — a timeout-driven freeze still reports EVERY decision (including the
    /// three timeouts that caused it) through <c>onDeclared</c>, each correctly tagged
    /// <see cref="DecisionSource.Timeout"/> — the exact source `DelveLiveEventNames.Declared`'s wire push
    /// needs to tell a client "you timed out," not just "something happened."</summary>
    [Fact]
    public async Task OnDeclared_reports_timeouts_as_timeouts_through_the_freeze()
    {
        var registry = new BattleSessionRegistry();
        var session = NewSession(registry, out _, out var frozenLog, out var declared, out _);

        session.Start();
        await WaitForFreezeAsync(session, frozenLog);

        Assert.True(session.Frozen);
        var seen = declared.ToArray();
        Assert.True(seen.Count(d => d.Source == DecisionSource.Timeout) >= 3,
            $"expected at least 3 Timeout declarations through onDeclared, saw {seen.Length} total");
        Assert.DoesNotContain(seen, d => d.Source == DecisionSource.Player);

        await Record.ExceptionAsync(() => session.RunTask!);
    }

    static string OpposingTargetFor(string actorKey) =>
        actorKey.StartsWith("squad", StringComparison.Ordinal) ? "wave:0" : "squad:p0:0";

    /// <summary>Answers whichever actor is CURRENTLY asked with the exact same content every time
    /// ("act.attack" against the fixed opposing target) -- content-agnostic to WHICH actor or WHEN,
    /// which is what makes two independently-driven runs comparable without needing to agree on the
    /// engine's own internal turn order/count in advance.</summary>
    static async Task RespondToPendingAskOnceAsync(DelveBattleSession session)
    {
        if (session.PendingActorKey is { } pending)
            session.Declare(pending, "act.attack", OpposingTargetFor(pending));
        await Task.Delay(2);
    }

    static async Task<BattleReport> DriveToCompletionAsync(DelveBattleSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!session.RunTask!.IsCompleted && DateTime.UtcNow < deadline)
            await RespondToPendingAskOnceAsync(session);
        Assert.True(session.RunTask!.IsCompleted, "battle never completed within the test timeout");
        return await session.RunTask!;
    }

    /// <summary>
    /// Waits for a session to freeze by waiting on <b>what the freeze CALLBACK wrote</b> — never on
    /// <see cref="DelveBattleSession.Frozen"/>.
    ///
    /// <para><b>The defect this closes, measured 2026-09-17.</b>
    /// <c>OnDeclared_reports_timeouts_as_timeouts_through_the_freeze</c> failed a full-suite run with
    /// <c>Assert.Single() Failure: The collection was empty</c> on the freeze log — one line after
    /// <c>Assert.True(session.Frozen)</c> had PASSED. Both statements were true at the moment each ran.
    /// <c>DelveBattleSession.Freeze</c> claims the freeze and publishes <c>Frozen = true</c> under its
    /// gate, and only then (outside the gate, so a caller-supplied callback cannot deadlock it) invokes
    /// <c>_onFrozen</c>. A test spinning on <c>Frozen</c> is therefore free to wake up inside that
    /// window and assert against a log the background thread has not written yet.</para>
    ///
    /// <para><b>Why the fix is here and not in <c>Freeze</c>.</b> That order is required:
    /// <c>Frozen</c> is the gate <c>Declare</c> and <c>Ask</c> read, so publishing it later would let one
    /// more declare land on an already-frozen session — a real defect traded for a test convenience.
    /// The production contract is right; the test was waiting on the wrong signal.</para>
    ///
    /// <para>Because <c>Frozen</c> is set strictly BEFORE the callback, a non-empty log implies
    /// <c>Frozen</c> is already true — so this wait is strictly stronger than the old one, not a
    /// weakening of it, and a caller may still assert <c>Frozen</c> afterwards.</para>
    /// </summary>
    static async Task<SteerLogPayload> WaitForFreezeAsync(
        DelveBattleSession session, ConcurrentQueue<SteerLogPayload> frozenLog)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (frozenLog.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        Assert.False(frozenLog.IsEmpty,
            "session never reported a freeze through onFrozen within 5s"
            + $" (session.Frozen = {session.Frozen} at the deadline — if that reads True, the callback"
            + " itself is the thing that failed to fire, not the freeze)");
        return Assert.Single(frozenLog);
    }

    static async Task DriveUntilAsync(DelveBattleSession session, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await RespondToPendingAskOnceAsync(session);
        Assert.True(condition(), "condition never became true within the test timeout");
    }

    /// <summary>
    /// ⭐ D2.16's own named acceptance test (spec-delve-battle-profile.md's Testing-strategy section,
    /// used verbatim): "record N decisions, freeze, resume with the same remaining choices ⇒
    /// BattleReport byte-identical to an uninterrupted run of the same battle."
    ///
    /// <para>Every steered ask across all three runs (the control, the interrupted prefix, and the
    /// resumed suffix) is answered with the IDENTICAL content regardless of which actor or when it is
    /// asked (<see cref="RespondToPendingAskOnceAsync"/>) — so this does not need to predict the
    /// engine's own turn order/count, only that every ask receives a real `Player` decision, never a
    /// timeout, with the same action+target every time. That is what makes the freeze point (after N
    /// decisions, wherever the poll happens to land) reproduce the exact same battle either way.</para>
    /// </summary>
    [Fact]
    public async Task Resume_after_a_freeze_replays_the_recorded_prefix_then_finishes_byte_identical_to_an_uninterrupted_run()
    {
        const int freezeAfter = 2;

        // ---- Run A: uninterrupted control -- never frozen. ----
        var registryA = new BattleSessionRegistry();
        var sessionA = NewSession(registryA, out _, out _, dwellMs: 2000);
        sessionA.Start();
        var reportA = await DriveToCompletionAsync(sessionA);
        Assert.Equal(TaskStatus.RanToCompletion, sessionA.RunTask!.Status);

        // ---- Run B: freeze after `freezeAfter` real decisions, then resume as a BRAND NEW session
        // over the persisted trace (never a reattachment to the frozen Task) and finish. ----
        var registryB = new BattleSessionRegistry();
        var setupB = Setup();
        var steeredKeysB = FusionRpg.Core.Actions.IntentRouter.KeysForParty(setupB, 0);
        DecisionTrace? persistedTrace = null;

        var sessionB1 = new DelveBattleSession(
            "delve-1-0-0-p0", 1, 0, playerId: 1, setupB, seed: 12345UL, trace: new DecisionTrace(),
            automated: new FixedAttacker(), steeredKeysB, registryB,
            onDecisionPersisted: t => persistedTrace = t, dwellMs: 2000);

        sessionB1.Start();
        await DriveUntilAsync(sessionB1, () => sessionB1.Trace.Count >= freezeAfter);
        sessionB1.Freeze(LiveFreezeTrigger.FreezeAwayPayload(0));
        await Record.ExceptionAsync(() => sessionB1.RunTask!);
        Assert.Equal(TaskStatus.Canceled, sessionB1.RunTask!.Status);
        Assert.NotNull(persistedTrace);
        Assert.True(persistedTrace!.Count >= freezeAfter);

        registryB.Close(sessionB1.MatchKey); // a genuine resume never reuses the old bookkeeping entry
        var sessionB2 = new DelveBattleSession(
            sessionB1.MatchKey, 1, 0, playerId: 1, setupB, seed: 12345UL, trace: persistedTrace!,
            automated: new FixedAttacker(), steeredKeysB, registryB, dwellMs: 2000);

        sessionB2.Start();
        var reportB = await DriveToCompletionAsync(sessionB2);
        Assert.Equal(TaskStatus.RanToCompletion, sessionB2.RunTask!.Status);

        Assert.Equal(reportA.Outcome, reportB.Outcome);
        Assert.Equal(reportA.Rounds, reportB.Rounds);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(reportA with { EnvironmentStamp = "" }),
            System.Text.Json.JsonSerializer.Serialize(reportB with { EnvironmentStamp = "" }));
    }
}
