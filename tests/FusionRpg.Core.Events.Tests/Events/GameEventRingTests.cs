using FusionRpg.Core.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

public class GameEventRingTests
{
    static GameEventRec Rec(long seq, IntPtr target, GameEventKind kind = GameEventKind.CombatHit, long amount = -10) =>
        new(kind, frame: 1, seq: seq, actorPtr: new IntPtr(0xA), targetPtr: target,
            typeId: 1, targetTypeId: 2, side: GameEventSide.Zombie, amount: amount,
            hitCount: 1, chainDepth: 0, sourceGrantIdx: -1, matchKeyIdx: 0, pairId: 0);

    [Fact]
    public void Fifo_order_preserved()
    {
        var ring = new GameEventRing(16);
        for (var i = 1; i <= 5; i++)
            Assert.True(ring.TryAppend(Rec(i, new IntPtr(i))));

        for (var i = 1; i <= 5; i++)
        {
            Assert.True(ring.TryPop(out var rec));
            Assert.Equal(i, rec.Seq);
        }
        Assert.False(ring.TryPop(out _));
    }

    [Fact]
    public void Append_during_drain_lands_behind_cursor()
    {
        var ring = new GameEventRing(16);
        ring.TryAppend(Rec(1, new IntPtr(1)));
        ring.TryAppend(Rec(2, new IntPtr(2)));

        Assert.True(ring.TryPop(out var first));
        Assert.Equal(1, first.Seq);
        // Simulates a record generated while draining (chain synthetic).
        ring.TryAppend(Rec(3, new IntPtr(3)));

        Assert.True(ring.TryPop(out var second));
        Assert.Equal(2, second.Seq);
        Assert.True(ring.TryPop(out var third));
        Assert.Equal(3, third.Seq);
    }

    [Fact]
    public void Overflow_drops_incoming_with_counter()
    {
        var ring = new GameEventRing(16);
        for (var i = 0; i < 16; i++)
            Assert.True(ring.TryAppend(Rec(i, new IntPtr(1))));

        Assert.False(ring.TryAppend(Rec(99, new IntPtr(1))));
        Assert.Equal(1, ring.Dropped);
        Assert.Equal(16, ring.Count);

        // FIFO intact — oldest record survives, incoming was the drop.
        Assert.True(ring.TryPop(out var oldest));
        Assert.Equal(0, oldest.Seq);
    }

    [Fact]
    public void Wraparound_keeps_order()
    {
        var ring = new GameEventRing(16);
        // Fill, drain half, refill past the physical end.
        for (var i = 0; i < 16; i++) ring.TryAppend(Rec(i, new IntPtr(1)));
        for (var i = 0; i < 8; i++) ring.TryPop(out _);
        for (var i = 16; i < 24; i++) Assert.True(ring.TryAppend(Rec(i, new IntPtr(1))));

        var expected = 8L;
        while (ring.TryPop(out var rec))
            Assert.Equal(expected++, rec.Seq);
        Assert.Equal(24L, expected);
    }

    [Fact]
    public void NextSeq_is_monotonic()
    {
        var ring = new GameEventRing();
        var a = ring.NextSeq();
        var b = ring.NextSeq();
        Assert.True(b > a);
    }
}
