using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// **B22 — T11 live sessions.** Lifecycle, reconnect and AFK over T6's dwell and T10's trace.
///
/// <para>The two acceptance lines are "a disconnect mid-battle resumes or abandons
/// <b>deterministically</b>" and "<b>no session path can write a battle whose trace is
/// incomplete</b>". Determinism here is structural: nothing in this class reads a clock. AFK is
/// counted in TURNS, not seconds, for the same reason a timeout is recorded as a decision at a tick —
/// a wall clock would abandon at a different point on a slower machine.</para>
/// </summary>
public class BattleSessionTests
{
    static (BattleSessionRegistry Reg, DecisionTrace Trace) New()
    {
        var reg = new BattleSessionRegistry();
        var trace = new DecisionTrace();
        reg.Open("m1", playerId: 7, trace);
        return (reg, trace);
    }

    [Fact]
    public void AnOpenedSessionIsLive()
    {
        var (reg, _) = New();
        Assert.Equal(BattleSessionState.Live, reg.Find("m1")!.State);
    }

    [Fact]
    public void OpeningTheSameMatchTwiceIsRefused()
    {
        var (reg, _) = New();
        Assert.Throws<InvalidOperationException>(() => reg.Open("m1", 7, new DecisionTrace()));
    }

    // ---- disconnect / resume ----

    /// <summary>A drop must PRESERVE the session. Discarding it would throw away a real result the
    /// player is entitled to come back to.</summary>
    [Fact]
    public void ADisconnectPreservesTheSessionAndItsTrace()
    {
        var (reg, trace) = New();
        trace.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);

        reg.Disconnect("m1");

        Assert.Equal(BattleSessionState.Disconnected, reg.Find("m1")!.State);
        Assert.Equal(1, reg.Find("m1")!.Trace.Count);

        var resumed = reg.Resume("m1", playerId: 7);
        Assert.NotNull(resumed);
        Assert.Equal(BattleSessionState.Live, resumed!.State);
        Assert.Equal(1, resumed.Trace.Count);   // decisions survived the round trip
    }

    [Fact]
    public void AnotherPlayerCannotResumeSomeoneElsesBattle()
    {
        var (reg, _) = New();
        reg.Disconnect("m1");
        Assert.Null(reg.Resume("m1", playerId: 999));
    }

    /// <summary>Abandonment is terminal — a resumed abandoned session would revive a battle that was
    /// already decided to be over.</summary>
    [Fact]
    public void AnAbandonedSessionNeverResumes()
    {
        var (reg, _) = New();
        reg.Abandon("m1", "player quit");

        Assert.Null(reg.Resume("m1", 7));
        Assert.Equal(BattleSessionState.Abandoned, reg.Find("m1")!.State);
    }

    [Fact]
    public void AbandoningTwiceKeepsTheFirstReason()
    {
        var (reg, _) = New();
        reg.Abandon("m1", "first");
        reg.Abandon("m1", "second");
        Assert.Equal("first", reg.Find("m1")!.AbandonReason);
    }

    // ---- AFK, counted in turns ----

    [Fact]
    public void ConsecutiveTimeoutsAbandonTheSession()
    {
        var (reg, _) = New();

        for (var i = 0; i < BattleSessionRegistry.MaxConsecutiveTimeouts; i++)
            reg.NoteTurn("m1", DecisionSource.Timeout);

        Assert.Equal(BattleSessionState.Abandoned, reg.Find("m1")!.State);
        Assert.Contains("AFK", reg.Find("m1")!.AbandonReason!);
    }

    /// <summary>A real decision proves someone is there, so the count starts over. Without this an
    /// occasional slow turn would eventually abandon an actively played battle.</summary>
    [Fact]
    public void APlayerDecisionResetsTheAfkCount()
    {
        var (reg, _) = New();

        reg.NoteTurn("m1", DecisionSource.Timeout);
        reg.NoteTurn("m1", DecisionSource.Timeout);
        reg.NoteTurn("m1", DecisionSource.Player);      // still here
        reg.NoteTurn("m1", DecisionSource.Timeout);
        reg.NoteTurn("m1", DecisionSource.Timeout);

        Assert.Equal(BattleSessionState.Live, reg.Find("m1")!.State);
        Assert.Equal(2, reg.Find("m1")!.ConsecutiveTimeouts);
    }

    /// <summary>Determinism: the same sequence of turns abandons at the same point, every time,
    /// because nothing here consults a clock.</summary>
    [Fact]
    public void TheSameTurnSequenceAlwaysAbandonsAtTheSamePoint()
    {
        static int TurnsUntilAbandoned()
        {
            var reg = new BattleSessionRegistry();
            reg.Open("m", 1, new DecisionTrace());
            var n = 0;
            while (reg.Find("m")!.State != BattleSessionState.Abandoned)
            {
                reg.NoteTurn("m", DecisionSource.Timeout);
                n++;
                if (n > 50) break;
            }

            return n;
        }

        Assert.Equal(TurnsUntilAbandoned(), TurnsUntilAbandoned());
    }

    // ---- the write gate ----

    /// <summary>⛔ The acceptance: no session path may write a battle whose trace is incomplete.</summary>
    [Fact]
    public void OnlyALiveCompletedSessionWithRealDecisionsMayWrite()
    {
        var (reg, trace) = New();

        Assert.False(reg.MayWrite("m1"));                       // not finished
        reg.Complete("m1");
        Assert.False(reg.MayWrite("m1"));                       // finished, but the trace is empty

        trace.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);
        Assert.True(reg.MayWrite("m1"));                        // live + finished + decided
    }

    [Fact]
    public void ADisconnectedOrAbandonedSessionMayNeverWrite()
    {
        var (reg, trace) = New();
        trace.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);
        reg.Complete("m1");
        Assert.True(reg.MayWrite("m1"));

        reg.Disconnect("m1");
        Assert.False(reg.MayWrite("m1"));                       // mid-drop: not writable

        reg.Resume("m1", 7);
        Assert.True(reg.MayWrite("m1"));                        // back, and still complete

        reg.Abandon("m1", "gave up");
        Assert.False(reg.MayWrite("m1"));                       // terminal, forever
    }

    [Fact]
    public void AnUnknownMatchMayNeverWrite()
    {
        Assert.False(new BattleSessionRegistry().MayWrite("nope"));
    }

    /// <summary>An abandoned session cannot be quietly marked complete — completion after
    /// abandonment would be a write path around the gate.</summary>
    [Fact]
    public void CompletingAnAbandonedSessionIsANoOp()
    {
        var (reg, trace) = New();
        trace.Record(100, "squad:0", "act.attack", "wave:0", DecisionSource.Player);
        reg.Abandon("m1", "quit");
        reg.Complete("m1");

        Assert.False(reg.Find("m1")!.Completed);
        Assert.False(reg.MayWrite("m1"));
    }
}
