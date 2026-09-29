using FusionRpg.Core.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

/// <summary>Coalescing key + exclusions — event-pipeline-v2-ssot.md §4b.2 (test group 2).</summary>
public class EventCoalescerTests
{
    static long _seq;

    static GameEventRec Rec(
        GameEventKind kind = GameEventKind.CombatHit,
        long target = 0xB,
        long actor = 0xA,
        long amount = -10,
        short hits = 1,
        byte chainDepth = 0,
        int sourceGrantIdx = -1,
        int pairId = 0,
        int typeId = 1,
        int matchKeyIdx = 0) =>
        new(kind, frame: 1, seq: ++_seq, actorPtr: new IntPtr(actor), targetPtr: new IntPtr(target),
            typeId: typeId, targetTypeId: 2, side: GameEventSide.Zombie, amount: amount,
            hitCount: hits, chainDepth: chainDepth, sourceGrantIdx: sourceGrantIdx,
            matchKeyIdx: matchKeyIdx, pairId: pairId);

    static GameEventRing RingOf(params GameEventRec[] recs)
    {
        var ring = new GameEventRing(256);
        foreach (var r in recs) ring.TryAppend(r);
        return ring;
    }

    [Fact]
    public void Same_key_merges_amount_and_hit_count()
    {
        var window = EventCoalescer.Window(RingOf(Rec(amount: -10), Rec(amount: -15), Rec(amount: -5)));
        var m = Assert.Single(window);
        Assert.Equal(-30, m.Amount);
        Assert.Equal(3, m.HitCount);
    }

    [Fact]
    public void Different_target_does_not_merge()
    {
        var window = EventCoalescer.Window(RingOf(Rec(target: 0xB), Rec(target: 0xC)));
        Assert.Equal(2, window.Count);
    }

    [Fact]
    public void Different_type_id_does_not_merge()
    {
        // plant:{tid}/zombie:{tid} owner keys match on TypeId — it is part of the key.
        var window = EventCoalescer.Window(RingOf(Rec(typeId: 1), Rec(typeId: 2)));
        Assert.Equal(2, window.Count);
    }

    [Fact]
    public void Chain_records_never_merge()
    {
        var window = EventCoalescer.Window(RingOf(Rec(chainDepth: 1), Rec(chainDepth: 1)));
        Assert.Equal(2, window.Count);
    }

    [Fact]
    public void Source_grant_records_never_merge()
    {
        // SourceGrantId carries the self-proc guard (SSOT §A7) — merging would lose one guard.
        var window = EventCoalescer.Window(RingOf(Rec(sourceGrantIdx: 3), Rec(sourceGrantIdx: 4)));
        Assert.Equal(2, window.Count);
    }

    [Fact]
    public void Status_hook_records_pass_through()
    {
        var window = EventCoalescer.Window(RingOf(
            Rec(GameEventKind.StatusHook), Rec(GameEventKind.StatusHook)));
        Assert.Equal(2, window.Count);
    }

    [Fact]
    public void Paired_taken_suppressed_when_dealt_in_window()
    {
        // Melee order (audit §A3): combat.hit (dealt) then plant.damage (taken), same PairId.
        var window = EventCoalescer.Window(RingOf(
            Rec(GameEventKind.CombatHit, pairId: 7),
            Rec(GameEventKind.PlantDamage, pairId: 7)));
        var m = Assert.Single(window);
        Assert.Equal(GameEventKind.CombatHit, m.Kind);
    }

    [Fact]
    public void Unpaired_taken_survives_and_merges_with_same_key()
    {
        // pairId 9's dealt partner is absent → not suppressed; the two surviving takens share
        // a coalescing key, so they merge (suppression is checked before merging).
        var window = EventCoalescer.Window(RingOf(
            Rec(GameEventKind.CombatHit, pairId: 7),
            Rec(GameEventKind.PlantDamage, pairId: 0),
            Rec(GameEventKind.PlantDamage, pairId: 9)));
        Assert.Equal(2, window.Count);
        Assert.Equal(GameEventKind.PlantDamage, window[1].Kind);
        Assert.Equal(2, window[1].HitCount);
    }

    [Fact]
    public void Merge_lands_at_earliest_position_preserving_order()
    {
        var window = EventCoalescer.Window(RingOf(
            Rec(target: 0xB, amount: -1),
            Rec(GameEventKind.ZombieDamage, target: 0xB, amount: -2),
            Rec(target: 0xB, amount: -4)));

        Assert.Equal(2, window.Count);
        Assert.Equal(GameEventKind.CombatHit, window[0].Kind);
        Assert.Equal(-5, window[0].Amount);          // -1 + -4 merged into position 0
        Assert.Equal(GameEventKind.ZombieDamage, window[1].Kind);
    }

    [Fact]
    public void Hit_count_accumulates_from_premerged_records()
    {
        var window = EventCoalescer.Window(RingOf(Rec(hits: 3), Rec(hits: 4)));
        Assert.Equal(7, Assert.Single(window).HitCount);
    }

    [Fact]
    public void Empty_ring_gives_empty_window()
    {
        Assert.Empty(EventCoalescer.Window(RingOf()));
    }
}
