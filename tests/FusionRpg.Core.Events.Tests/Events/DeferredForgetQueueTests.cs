using FusionRpg.Contracts;
using FusionRpg.Core.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

/// <summary>
/// lawn-hit-entry T9c / lawn-combat-wire L-N16 (GATE 2 "DeferForget has no fixture"): a death that
/// fires inside an active drain gets <c>-1</c> from <see cref="EventDrain.FlushForPtr"/> and defers its
/// grant-withdraw. The deferred forget must run only after every record for that ptr has drained —
/// including one recorded after the defer — and never leak into the next match.
/// </summary>
public class DeferredForgetQueueTests
{
    static GameEventRec Hit(EventDrain d, long target, long attacker = 0xA) =>
        new(GameEventKind.CombatHit, frame: 1, seq: d.NextSeq(), actorPtr: new IntPtr(attacker),
            targetPtr: new IntPtr(target), typeId: 1, targetTypeId: 2, side: GameEventSide.Zombie,
            amount: -10, hitCount: 1, chainDepth: 0, sourceGrantIdx: -1,
            matchKeyIdx: d.InternMatchKey("m1"), pairId: 0);

    [Fact]
    public void A_death_inside_a_drain_forgets_only_after_that_ptrs_records_have_drained()
    {
        var log = new List<string>();
        var forgets = new DeferredForgetQueue();
        EventDrain drain = null!;
        var died = false;
        drain = new EventDrain(dto =>
        {
            log.Add("process:" + dto.TargetPtr);
            if (died || dto.TargetPtr != "B") return;
            died = true;
            // The die hook, nested inside this drain pass.
            var spent = drain.FlushForPtr(new IntPtr(0xB));
            Assert.True(spent < 0);
            forgets.Defer(new IntPtr(0xB), () => log.Add("forget:B"));
        })
        { SessionMode = true };

        drain.Record(Hit(drain, 0xB, attacker: 0xA1));
        drain.Record(Hit(drain, 0xB, attacker: 0xA2));
        drain.Drain(long.MaxValue);

        Assert.DoesNotContain("forget:B", log);           // nothing withdrew during the nested pass
        drain.Record(Hit(drain, 0xB, attacker: 0xA3));     // arrives after the defer, before the tick

        forgets.RunDue(ptr => drain.FlushForPtr(ptr));     // EventDrainHost.Tick

        Assert.Equal("forget:B", log[^1]);
        Assert.Equal(3, log.Count(l => l == "process:B"));
        Assert.Equal(0, drain.PendingCount);
        Assert.Equal(0, forgets.Count);
    }

    [Fact]
    public void Due_forgets_run_in_queue_order_each_after_its_own_flush()
    {
        var log = new List<string>();
        var q = new DeferredForgetQueue();
        q.Defer(new IntPtr(0x1), () => log.Add("forget:1"));
        q.Defer(new IntPtr(0x2), () => log.Add("forget:2"));

        q.RunDue(ptr => log.Add("flush:" + ptr.ToString("X")));

        Assert.Equal(new[] { "flush:1", "forget:1", "flush:2", "forget:2" }, log);
    }

    [Fact]
    public void A_forget_deferred_while_running_waits_for_the_next_tick()
    {
        var log = new List<string>();
        var q = new DeferredForgetQueue();
        q.Defer(new IntPtr(0x1), () =>
        {
            log.Add("forget:1");
            q.Defer(new IntPtr(0x2), () => log.Add("forget:2"));
        });

        q.RunDue(_ => { });
        Assert.Equal(new[] { "forget:1" }, log);
        Assert.Equal(1, q.Count);

        q.RunDue(_ => { });
        Assert.Equal(new[] { "forget:1", "forget:2" }, log);
    }

    [Fact]
    public void Match_end_clear_drops_every_pending_forget()
    {
        var ran = false;
        var q = new DeferredForgetQueue();
        q.Defer(new IntPtr(0x1), () => ran = true);

        q.Clear();
        q.RunDue(_ => { });

        Assert.False(ran);
        Assert.Equal(0, q.Count);
    }
}
