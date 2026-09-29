using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>Innate queue-then-barrier (T12, spec §2.6) — capacity reads after the first tick.</summary>
public class ShieldInnateTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    static ShieldGrant Innate(string owner = "entity:z1", int typeId = 7, long baseHp = 200) => new()
    {
        OwnerKey = owner,
        SourceId = "innate:" + typeId,
        BaseHp = baseHp,
        Priority = ShieldPolicy.PriorityInnate,
        RefillOnMerge = false,
        IsInnate = true
    };

    [Fact]
    public void Queued_innate_applies_on_first_tick_not_immediately()
    {
        var rt = new ShieldRuntime();
        rt.QueueInnate(Innate());
        Assert.False(rt.HasAnyInstances());
        Assert.True(rt.HasPendingWork);

        rt.Tick(0, 100, _ => Neutral);
        var shield = Assert.Single(rt.GetShields("entity:z1"));
        Assert.Equal(200, shield.Hp);
        Assert.True(shield.IsInnate);
        Assert.Null(shield.ExpiresAtTick);
    }

    [Fact]
    public void Capacity_contributor_landing_before_first_tick_is_included()
    {
        // The barrier's whole point: a progression row that loads between registration and
        // the first tick must be in the capacity read — live and replay then agree.
        var rt = new ShieldRuntime();
        rt.QueueInnate(Innate(baseHp: 200));

        var composer = new DerivedComposer();
        var buffed = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacityOmni, DerivedModifierOp.Flat, 100.0)
        });
        rt.Tick(0, 100, _ => buffed);

        Assert.Equal(300, Assert.Single(rt.GetShields("entity:z1")).MaxHp);
    }

    [Fact]
    public void Resync_requeue_is_ignored_and_broken_innate_does_not_reform()
    {
        var rt = new ShieldRuntime();
        rt.QueueInnate(Innate());
        rt.Tick(0, 100, _ => Neutral);

        // Break it, then simulate the 1024-frame registry resync re-firing Add.
        rt.Absorb("entity:z1", 5000, 1,
            Array.Empty<FusionRpg.Core.Combat.Element.ElementPayloadComponent>(), null, Neutral);
        Assert.False(rt.HasAnyInstances());

        rt.DrainEvents(new List<ShieldEventRec>());   // clear observability backlog
        rt.QueueInnate(Innate());
        Assert.False(rt.HasPendingWork);   // granted-once marker blocks the re-queue
        rt.Tick(1, 100, _ => Neutral);
        Assert.False(rt.HasAnyInstances());
    }

    [Fact]
    public void Double_queue_before_first_tick_applies_once()
    {
        var rt = new ShieldRuntime();
        rt.QueueInnate(Innate());
        rt.QueueInnate(Innate());
        rt.Tick(0, 100, _ => Neutral);
        Assert.Single(rt.GetShields("entity:z1"));
    }

    [Fact]
    public void Death_flush_clears_marker_so_ptr_reuse_gets_fresh_innate()
    {
        var rt = new ShieldRuntime();
        rt.QueueInnate(Innate());
        rt.Tick(0, 100, _ => Neutral);
        rt.RemoveAll("entity:z1");         // actor died; ptr may be reused by a new zombie

        rt.QueueInnate(Innate());          // genuinely new actor at the same ptr
        rt.Tick(1, 100, _ => Neutral);
        Assert.Equal(200, Assert.Single(rt.GetShields("entity:z1")).Hp);
    }

    [Fact]
    public void Death_before_first_tick_drops_pending_grant()
    {
        var rt = new ShieldRuntime();
        rt.QueueInnate(Innate());
        rt.RemoveAll("entity:z1");
        Assert.False(rt.HasPendingWork);
        rt.Tick(0, 100, _ => Neutral);
        Assert.False(rt.HasAnyInstances());
    }

    [Fact]
    public void Nonpositive_innate_after_capacity_debuff_rejected_at_apply()
    {
        var rt = new ShieldRuntime();
        rt.QueueInnate(Innate(baseHp: 50));
        var composer = new DerivedComposer();
        var debuffed = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacityOmni, DerivedModifierOp.Flat, -80.0)
        });
        rt.Tick(0, 100, _ => debuffed);
        Assert.False(rt.HasAnyInstances());
    }

    [Fact]
    public void Innate_catalog_validates_rows()
    {
        ShieldInnateCatalog.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShieldInnateCatalog.Register("zombie", 7, new ShieldInnateDef(0, null, ShieldPolicy.PriorityInnate)));
        ShieldInnateCatalog.Register("zombie", 7, new ShieldInnateDef(150, ElementTypeId.Dark, ShieldPolicy.PriorityInnate));
        Assert.True(ShieldInnateCatalog.TryGet("ZOMBIE", 7, out var def));
        Assert.Equal(150, def.BaseHp);
        Assert.Equal(ShieldPolicy.PriorityInnate, def.Priority);
        ShieldInnateCatalog.Clear();
        Assert.True(ShieldInnateCatalog.IsEmpty);
    }
}
