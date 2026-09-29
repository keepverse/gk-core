using System.Reflection;
using FusionRpg.Core.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

public class GameEventRecTests
{
    [Fact]
    public void Record_struct_carries_values_only()
    {
        // Deferred records must never hold object references — an IL2CPP ref read at drain
        // time is use-after-free (event-pipeline-v2-ssot.md §4.2).
        var fields = typeof(GameEventRec).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotEmpty(fields);
        foreach (var f in fields)
        {
            Assert.True(f.FieldType.IsValueType,
                $"field {f.Name} is {f.FieldType.Name} — reference types are forbidden in GameEventRec");
        }
    }

    [Fact]
    public void HitCount_floors_at_one()
    {
        var rec = new GameEventRec(
            GameEventKind.CombatHit, frame: 10, seq: 1,
            actorPtr: new IntPtr(0xA), targetPtr: new IntPtr(0xB),
            typeId: 3, targetTypeId: 7, side: GameEventSide.Zombie,
            amount: -20, hitCount: 0, chainDepth: 0,
            sourceGrantIdx: -1, matchKeyIdx: 0, pairId: 0);
        Assert.Equal(1, rec.HitCount);
    }

    [Fact]
    public void SwingPtr_defaults_to_zero_for_melee_shaped_construction()
    {
        // lawn-hit-attribution (T6): melee's swing identity is (ActorPtr, Frame), not a stored
        // pointer — every existing call site that omits swingPtr must keep getting IntPtr.Zero.
        var rec = new GameEventRec(
            GameEventKind.CombatHit, frame: 10, seq: 1,
            actorPtr: new IntPtr(0xA), targetPtr: new IntPtr(0xB),
            typeId: 3, targetTypeId: 7, side: GameEventSide.Plant,
            amount: -20, hitCount: 1, chainDepth: 0,
            sourceGrantIdx: -1, matchKeyIdx: 0, pairId: 5);
        Assert.Equal(IntPtr.Zero, rec.SwingPtr);
    }

    [Fact]
    public void SwingPtr_carries_the_bullet_ptr_when_supplied()
    {
        // Projectile hits stamp the bullet's own pointer as the swing identity — distinct from
        // ActorPtr, which after T6 is the firing creature, not the bullet.
        var rec = new GameEventRec(
            GameEventKind.CombatHit, frame: 10, seq: 1,
            actorPtr: new IntPtr(0xA), targetPtr: new IntPtr(0xB),
            typeId: 3, targetTypeId: 7, side: GameEventSide.Zombie,
            amount: -20, hitCount: 1, chainDepth: 0,
            sourceGrantIdx: -1, matchKeyIdx: 0, pairId: 0,
            swingPtr: new IntPtr(0xC0FFEE));
        Assert.Equal(new IntPtr(0xC0FFEE), rec.SwingPtr);
        Assert.NotEqual(rec.ActorPtr, rec.SwingPtr);
    }

    [Fact]
    public void Coalescible_requires_no_chain_and_no_source_grant()
    {
        GameEventRec Make(byte depth, int grantIdx) => new(
            GameEventKind.CombatHit, 1, 1, IntPtr.Zero, new IntPtr(1), 0, 0,
            GameEventSide.Zombie, -1, 1, depth, grantIdx, -1, 0);

        Assert.True(Make(0, -1).IsCoalescible);
        Assert.False(Make(1, -1).IsCoalescible);   // chain record (SSOT §A8)
        Assert.False(Make(0, 5).IsCoalescible);    // self-proc guard rides SourceGrantId (SSOT §A7)
    }

    [Fact]
    public void Interner_round_trips_and_dedupes()
    {
        var interner = new EventStringInterner();
        var a1 = interner.Intern("match-1");
        var a2 = interner.Intern("match-1");
        var b = interner.Intern("grant.fire");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Equal("match-1", interner.Get(a1));
        Assert.Equal("grant.fire", interner.Get(b));
        Assert.Equal(2, interner.Count);
    }

    [Fact]
    public void Interner_null_and_missing_are_safe()
    {
        var interner = new EventStringInterner();
        Assert.Equal(-1, interner.Intern(null));
        Assert.Equal(-1, interner.Intern(""));
        Assert.Null(interner.Get(-1));
        Assert.Null(interner.Get(99));
    }

    [Fact]
    public void Interner_clear_resets_indices()
    {
        var interner = new EventStringInterner();
        interner.Intern("m1");
        interner.Clear();
        Assert.Equal(0, interner.Count);
        Assert.Null(interner.Get(0));
        Assert.Equal(0, interner.Intern("m2"));
    }
}
