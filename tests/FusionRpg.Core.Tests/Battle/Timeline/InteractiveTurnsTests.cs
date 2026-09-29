using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// **B20 + B21** (spec-interactive-turns.md) — the interactive dwell and its decision trace. They are
/// tested together because the map binds them: *"an interactive battle without a persisted decision
/// trace is precisely the hole where a boot sweep silently overwrites a player's win."*
///
/// <para>The sharpest requirement here is that <b>an AFK timeout is a decision at a tick, not a
/// duration</b>. A replay that re-measured the window would branch differently on a slower machine,
/// which is why `SimulationClock` may not read a wall clock at all.</para>
/// </summary>
public class InteractiveTurnsTests
{
    static readonly ActionEnvelope Attack = ActionEnvelope.NoOp with { ActionId = "act.attack" };
    static readonly ActionEnvelope Guard = ActionEnvelope.NoOp with { ActionId = "act.guard" };

    static ActionEnvelope? EnvelopeOf(string id) => id switch
    {
        "act.attack" => Attack,
        "act.guard" => Guard,
        _ => null
    };

    /// <summary>The AI policy an auto-resolved battle would use — also the timeout default.</summary>
    sealed class AlwaysAttacks : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) =>
            new("act.attack", "enemy:0", Attack);
    }

    sealed class NeverActs : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) => ActionIntent.None;
    }

    // ---- the dwell ----

    [Fact]
    public void APlayerChoiceIsDeclaredAndRecorded()
    {
        var trace = new DecisionTrace();
        var src = new InteractiveIntentSource(
            new AlwaysAttacks(), (_, _) => new PlayerChoice("act.guard", "enemy:1"), EnvelopeOf, trace);

        var intent = src.TryDeclare("squad:0", nowTick: 1000);

        Assert.Equal("act.guard", intent.ActionId);
        Assert.Equal("enemy:1", intent.TargetKey);

        var d = Assert.Single(trace.Decisions);
        Assert.Equal(1000, d.Tick);
        Assert.Equal(DecisionSource.Player, d.Source);
    }

    /// <summary>⛔ The load-bearing one: a timeout is RECORDED, with its tick, as a decision.</summary>
    [Fact]
    public void ATimeoutIsRecordedAsADecisionAtATick()
    {
        var trace = new DecisionTrace();
        var src = new InteractiveIntentSource(
            new AlwaysAttacks(), (_, _) => PlayerChoice.None, EnvelopeOf, trace);

        var intent = src.TryDeclare("squad:0", nowTick: 2500);

        Assert.Equal("act.attack", intent.ActionId);          // the default action was taken
        var d = Assert.Single(trace.Decisions);
        Assert.Equal(DecisionSource.Timeout, d.Source);       // and it is a timeout, not a player choice
        Assert.Equal(2500, d.Tick);                           // stamped at the tick it happened
    }

    [Fact]
    public void NothingLegalRecordsNothing()
    {
        var trace = new DecisionTrace();
        var src = new InteractiveIntentSource(
            new NeverActs(), (_, _) => PlayerChoice.None, EnvelopeOf, trace);

        Assert.True(src.TryDeclare("squad:0", 100).IsNone);
        Assert.Empty(trace.Decisions);   // an absent turn is not a decision
    }

    [Fact]
    public void AChoiceNamingAnUnusableActionFallsBackAndRecordsATimeout()
    {
        var trace = new DecisionTrace();
        var src = new InteractiveIntentSource(
            new AlwaysAttacks(), (_, _) => new PlayerChoice("act.nonexistent", null), EnvelopeOf, trace);

        var intent = src.TryDeclare("squad:0", 300);

        Assert.Equal("act.attack", intent.ActionId);
        Assert.Equal(DecisionSource.Timeout, Assert.Single(trace.Decisions).Source);
    }

    // ---- replay ----

    /// <summary>
    /// ⭐ The acceptance the map names: **an AFK timeout produces an identical battle on replay.**
    /// The replay source never consults the player and never re-times anything — it reads what was
    /// decided. A replay that re-measured the window is the failure this proves absent.
    /// </summary>
    [Fact]
    public void ATimeoutReplaysIdenticallyWithoutConsultingAnyone()
    {
        var live = new DecisionTrace();
        var recorded = new InteractiveIntentSource(
            new AlwaysAttacks(), (_, _) => PlayerChoice.None, EnvelopeOf, live);
        var original = recorded.TryDeclare("squad:0", 4000);

        // Replay against a source whose fallback would REFUSE — so if replay fell through to the
        // fallback instead of reading the trace, this would come back None and fail.
        var replay = new InteractiveIntentSource(new NeverActs(), EnvelopeOf, live);
        var again = replay.TryDeclare("squad:0", nowTick: 999_999);   // a different tick, deliberately

        Assert.Equal(original.ActionId, again.ActionId);
        Assert.Equal(original.TargetKey, again.TargetKey);
    }

    [Fact]
    public void ACompletedTraceReplaysEveryDecisionInOrder()
    {
        var trace = new DecisionTrace();
        trace.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);
        trace.Record(200, "squad:1", "act.guard", null, DecisionSource.Timeout);
        trace.Record(300, "squad:0", "act.guard", "wave:1", DecisionSource.Player);

        var replay = new InteractiveIntentSource(new NeverActs(), EnvelopeOf, trace);

        Assert.Equal("act.attack", replay.TryDeclare("squad:0", 0).ActionId);
        Assert.Equal("act.guard", replay.TryDeclare("squad:1", 0).ActionId);
        Assert.Equal("act.guard", replay.TryDeclare("squad:0", 0).ActionId);
        Assert.True(replay.TryDeclare("squad:0", 0).IsNone);   // exhausted
        Assert.True(trace.ReplayExhausted);
    }

    // ---- resume: replay-the-recorded-prefix-then-go-live (D2.16) ----

    [Fact]
    public void ResumeReplaysTheRecordedPrefixByteForByte()
    {
        var recorded = new DecisionTrace();
        recorded.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);
        recorded.Record(200, "squad:0", "act.guard", null, DecisionSource.Timeout);

        // A live ask/fallback that would REFUSE if consulted during the replayed prefix -- so if
        // resume fell through to live instead of reading the trace, this would come back wrong.
        var resumed = InteractiveIntentSource.ResumeReplayThenLive(
            new NeverActs(), (_, _) => throw new InvalidOperationException("must not ask during replay"), EnvelopeOf, recorded);

        Assert.Equal("act.attack", resumed.TryDeclare("squad:0", 999_999).ActionId);
        Assert.Equal("act.guard", resumed.TryDeclare("squad:0", 999_999).ActionId);
    }

    [Fact]
    public void ResumeGoesLiveOnceThePrefixIsExhausted()
    {
        var recorded = new DecisionTrace();
        recorded.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);

        var resumed = InteractiveIntentSource.ResumeReplayThenLive(
            new AlwaysAttacks(), (_, _) => new PlayerChoice("act.guard", "enemy:9"), EnvelopeOf, recorded);

        Assert.Equal("act.attack", resumed.TryDeclare("squad:0", 1).ActionId);  // replayed
        var live = resumed.TryDeclare("squad:0", 2);                            // prefix exhausted -> live
        Assert.Equal("act.guard", live.ActionId);
        Assert.Equal("enemy:9", live.TargetKey);
    }

    /// <summary>⛔ Found while building this: `Record` appends to the SAME list `ReplayExhausted`
    /// counts against, so a naive re-check of `ReplayExhausted` on every call would see it flip back
    /// to false the instant a live decision is recorded, and try to "replay" that very decision on the
    /// actor's next turn instead of asking again. Proves the sticky latch instead.</summary>
    [Fact]
    public void OnceLiveARecordedLiveDecisionIsNeverReplayedBack()
    {
        var recorded = new DecisionTrace();
        recorded.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);

        var asked = 0;
        var resumed = InteractiveIntentSource.ResumeReplayThenLive(
            new AlwaysAttacks(),
            (_, _) => { asked++; return new PlayerChoice("act.guard", null); },
            EnvelopeOf, recorded);

        Assert.Equal("act.attack", resumed.TryDeclare("squad:0", 1).ActionId);  // replayed prefix
        Assert.Equal("act.guard", resumed.TryDeclare("squad:0", 2).ActionId);   // goes live, asks once
        Assert.Equal("act.guard", resumed.TryDeclare("squad:0", 3).ActionId);   // asks again, not replayed
        Assert.Equal(2, asked);
    }

    [Fact]
    public void ResumeWithAnAlreadyExhaustedTraceGoesLiveImmediately()
    {
        var resumed = InteractiveIntentSource.ResumeReplayThenLive(
            new AlwaysAttacks(), (_, _) => new PlayerChoice("act.guard", null), EnvelopeOf, new DecisionTrace());

        Assert.Equal("act.guard", resumed.TryDeclare("squad:0", 1).ActionId);
    }

    [Fact]
    public void ResumeStillRecordsATimeoutOnceLive()
    {
        var recorded = new DecisionTrace();
        recorded.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);

        var resumed = InteractiveIntentSource.ResumeReplayThenLive(
            new AlwaysAttacks(), (_, _) => PlayerChoice.None, EnvelopeOf, recorded);

        Assert.Equal("act.attack", resumed.TryDeclare("squad:0", 1).ActionId);   // replayed
        var timedOut = resumed.TryDeclare("squad:0", 2);                         // live, times out
        Assert.Equal("act.attack", timedOut.ActionId);                           // AlwaysAttacks fallback

        Assert.Equal(2, recorded.Decisions.Count);
        Assert.Equal(DecisionSource.Timeout, recorded.Decisions[1].Source);
    }

    [Fact]
    public void ResumeRejectsANullAsk()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InteractiveIntentSource.ResumeReplayThenLive(new AlwaysAttacks(), null!, EnvelopeOf, new DecisionTrace()));
    }

    // ---- onRecorded: the incremental-persistence seam (D2.16 §4b) ----

    [Fact]
    public void OnRecordedFiresOnceForAPlayerDecisionWithTheSameDecisionJustRecorded()
    {
        var trace = new DecisionTrace();
        var seen = new List<TracedDecision>();
        var src = new InteractiveIntentSource(
            new AlwaysAttacks(), (_, _) => new PlayerChoice("act.guard", "enemy:1"), EnvelopeOf, trace,
            onRecorded: seen.Add);

        src.TryDeclare("squad:0", nowTick: 1000);

        var recorded = Assert.Single(seen);
        Assert.Equal(trace.Decisions[0], recorded);
        Assert.Equal(DecisionSource.Player, recorded.Source);
    }

    [Fact]
    public void OnRecordedFiresForATimeoutToo()
    {
        var trace = new DecisionTrace();
        var seen = new List<TracedDecision>();
        var src = new InteractiveIntentSource(
            new AlwaysAttacks(), (_, _) => PlayerChoice.None, EnvelopeOf, trace, onRecorded: seen.Add);

        src.TryDeclare("squad:0", nowTick: 2500);

        Assert.Equal(DecisionSource.Timeout, Assert.Single(seen).Source);
    }

    [Fact]
    public void OnRecordedDoesNotFireWhenNothingLegalIsRecorded()
    {
        var trace = new DecisionTrace();
        var seen = new List<TracedDecision>();
        var src = new InteractiveIntentSource(
            new NeverActs(), (_, _) => PlayerChoice.None, EnvelopeOf, trace, onRecorded: seen.Add);

        Assert.True(src.TryDeclare("squad:0", 100).IsNone);
        Assert.Empty(seen);
    }

    [Fact]
    public void OnRecordedNeverFiresDuringReplayOnlyOnceLive()
    {
        var recorded = new DecisionTrace();
        recorded.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);

        var seen = new List<TracedDecision>();
        var resumed = InteractiveIntentSource.ResumeReplayThenLive(
            new AlwaysAttacks(), (_, _) => new PlayerChoice("act.guard", "enemy:9"), EnvelopeOf, recorded,
            onRecorded: seen.Add);

        resumed.TryDeclare("squad:0", 1);   // replayed -- must not fire
        Assert.Empty(seen);

        resumed.TryDeclare("squad:0", 2);   // prefix exhausted -> live -- must fire exactly once
        var fired = Assert.Single(seen);
        Assert.Equal("act.guard", fired.ActionId);
        Assert.Equal(DecisionSource.Player, fired.Source);
    }

    [Fact]
    public void OnRecordedIsOptionalAndOmittingItChangesNothing()
    {
        var trace = new DecisionTrace();
        var src = new InteractiveIntentSource(
            new AlwaysAttacks(), (_, _) => new PlayerChoice("act.guard", "enemy:1"), EnvelopeOf, trace);

        var intent = src.TryDeclare("squad:0", 1000);

        Assert.Equal("act.guard", intent.ActionId);
        Assert.Single(trace.Decisions);
    }

    // ---- persistence ----

    [Fact]
    public void ATraceRoundTripsThroughJson()
    {
        var trace = new DecisionTrace();
        trace.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);
        trace.Record(250, "squad:1", "act.guard", null, DecisionSource.Timeout);

        var back = DecisionTrace.FromJson(trace.ToJson());

        Assert.NotNull(back);
        Assert.Equal(2, back!.Count);
        Assert.Equal(trace.Decisions[0], back.Decisions[0]);
        Assert.Equal(trace.Decisions[1], back.Decisions[1]);
    }

    /// <summary>
    /// ⛔ **An absent trace is not an empty trace**, and conflating them is exactly the hole T10
    /// exists to close: an interactive match with no trace must be unreplayable, not "a battle in
    /// which nobody decided anything" — which the boot sweep would happily re-resolve with AI
    /// decisions, overwriting a real player result.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentTraceIsNullNotEmpty(string? json)
    {
        Assert.Null(DecisionTrace.FromJson(json));
    }

    [Fact]
    public void AnEmptyButPresentTraceIsDistinguishableFromAnAbsentOne()
    {
        var present = DecisionTrace.FromJson(new DecisionTrace().ToJson());
        Assert.NotNull(present);
        Assert.Equal(0, present!.Count);
    }

    [Fact]
    public void ADecisionAlwaysNamesAnActorAndAnAction()
    {
        var trace = new DecisionTrace();
        Assert.Throws<ArgumentException>(() => trace.Record(1, "", "act.attack", null, DecisionSource.Player));
        Assert.Throws<ArgumentException>(() => trace.Record(1, "squad:0", "", null, DecisionSource.Player));
    }

    // ---- end to end through a real battle ----

    /// <summary>
    /// ⭐ The link that makes all of the above reachable: `BattleEngine.Resolve` accepts an
    /// `IIntentSource`, so an interactive battle can actually occupy the `Ready` dwell B38 created.
    /// Passing none keeps the shipped AI policy, which is why every golden is unmoved.
    /// </summary>
    [Fact]
    public void AnInjectedIntentSourceIsUsedByARealBattle()
    {
        var declaredFor = new List<string>();
        var spy = new SpyIntentSource(declaredFor);

        BattleEngine.Resolve(Core.Tests.Battle.BattleGoldenTests.CloseSetup(), 2002, intentSource: spy);

        Assert.NotEmpty(declaredFor);   // the battle asked OUR source, not the built-in stub
    }

    [Fact]
    public void PassingNoIntentSourceIsByteIdenticalToTheShippedBattle()
    {
        var a = BattleEngine.Resolve(Core.Tests.Battle.BattleGoldenTests.CloseSetup(), 2002);
        var b = BattleEngine.Resolve(Core.Tests.Battle.BattleGoldenTests.CloseSetup(), 2002, intentSource: null);

        Assert.Equal(a.Outcome, b.Outcome);
        Assert.Equal(a.Rounds, b.Rounds);
    }

    sealed class SpyIntentSource : IIntentSource
    {
        readonly List<string> _seen;
        public SpyIntentSource(List<string> seen) => _seen = seen;

        // Declines everything: the point is to prove the battle CONSULTED this source, not to drive it.
        public ActionIntent TryDeclare(string actorKey, long nowTick)
        {
            _seen.Add(actorKey);
            return ActionIntent.None;
        }
    }
}
