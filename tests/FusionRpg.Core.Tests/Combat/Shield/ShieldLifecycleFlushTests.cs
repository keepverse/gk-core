using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>
/// Review findings (2026-08-21 five-axis pass): death/lifecycle flush wiring and the
/// regen-carry leak on absorb-emptied stacks. Prove-It: these failed before the fixes.
/// </summary>
public class ShieldLifecycleFlushTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    static ActorDerivedSnapshot Regen7()
    {
        var composer = new DerivedComposer();
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldRegenOmni, DerivedModifierOp.Flat, 7.0)
        });
    }

    [Fact]
    public void Absorb_emptied_stack_does_not_leak_regen_carry_to_next_shield()
    {
        var rt = new ShieldRuntime();
        var snap = Regen7();

        // Shield damaged → one tick accrues 700 milli-HP of carry (no whole HP yet).
        var s1 = rt.Apply(new ShieldGrant { OwnerKey = "entity:a", SourceId = "s1", BaseHp = 100 }, snap, 0).Instance!;
        s1.Hp = 10;
        rt.Tick(0, 100, _ => snap);
        Assert.Equal(10, s1.Hp);

        // Break it via absorb (stack empties) — the carry must die with the stack.
        rt.Absorb("entity:a", 500, 1, Array.Empty<ElementPayloadComponent>(), null, snap);
        Assert.False(rt.HasAnyInstances());

        // Fresh shield, damaged; one tick accrues 700 milli again. If the old 700 leaked,
        // the total hits 1400 → +1 HP. Clean runtime: still exactly 10.
        var s2 = rt.Apply(new ShieldGrant { OwnerKey = "entity:a", SourceId = "s2", BaseHp = 100 }, snap, 1).Instance!;
        s2.Hp = 10;
        rt.Tick(1, 100, _ => snap);
        Assert.Equal(10, s2.Hp);
    }

    [Fact]
    public void Runtime_clear_resets_everything_including_innate_markers()
    {
        var rt = new ShieldRuntime();
        rt.QueueInnate(new ShieldGrant
            { OwnerKey = "entity:z1", SourceId = "innate:7", BaseHp = 100, IsInnate = true });
        rt.Tick(0, 100, _ => Neutral);
        Assert.True(rt.HasAnyInstances());

        rt.Clear();
        Assert.False(rt.HasAnyInstances());
        Assert.False(rt.HasPendingWork);   // pending events wiped too — board reset emits nothing

        // A new match may reuse the ptr: the innate must grant again after Clear.
        rt.QueueInnate(new ShieldGrant
            { OwnerKey = "entity:z1", SourceId = "innate:7", BaseHp = 100, IsInnate = true });
        rt.Tick(1, 100, _ => Neutral);
        Assert.True(rt.HasAnyInstances());
    }

    [Fact]
    public void EffectBag_ClearAll_flushes_shields_with_status()
    {
        var h = new FoundationHarness().WithShieldGate();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.GrantShield("Z1", baseHp: 100);
        Assert.True(h.ShieldRuntime!.HasAnyInstances());

        h.ClearAll();   // match reset — same barrier that clears grants and status
        Assert.False(h.ShieldRuntime.HasAnyInstances());
    }
}
