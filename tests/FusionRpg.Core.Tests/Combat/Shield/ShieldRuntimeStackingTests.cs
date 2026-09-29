using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>Stacking / merge / admission policy — shield-system-spec.md §2.5.</summary>
public class ShieldRuntimeStackingTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    // priority: not `= ShieldPolicy.PrioritySkill` — PrioritySkill is config-loaded now
    // (tunables-ssot.md T1), and a default parameter value must be a compile-time constant.
    static ShieldGrant Grant(string owner = "entity:1", string source = "g1", ElementTypeId? el = null,
        long baseHp = 100, int? priority = null, long? duration = null,
        bool refill = true, bool innate = false) =>
        new()
        {
            OwnerKey = owner, SourceId = source, Element = el, BaseHp = baseHp,
            Priority = priority ?? ShieldPolicy.PrioritySkill,
            DurationTicks = duration, RefillOnMerge = refill, IsInnate = innate
        };

    [Fact]
    public void Apply_creates_full_shield()
    {
        var rt = new ShieldRuntime();
        var r = rt.Apply(Grant(), Neutral, nowTick: 0);
        Assert.Equal(ShieldApplyOutcome.Applied, r.Outcome);
        Assert.Equal(100, r.Instance!.Hp);
        Assert.Equal((100, 100), rt.Totals("entity:1"));
        Assert.True(rt.HasAnyInstances());
    }

    [Fact]
    public void Capacity_channel_raises_max_at_grant_time()
    {
        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacityOmni, DerivedModifierOp.Flat, 40.0),
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacity(ElementTypeId.Fire), DerivedModifierOp.Flat, 10.0)
        });
        var rt = new ShieldRuntime();
        var r = rt.Apply(Grant(el: ElementTypeId.Fire, baseHp: 100), snap, 0);
        Assert.Equal(150, r.Instance!.MaxHp);   // 100 + omni 40 + fire 10
    }

    [Fact]
    public void Merge_same_source_refills_when_refillOnMerge()
    {
        var rt = new ShieldRuntime();
        var first = rt.Apply(Grant(), Neutral, 0).Instance!;
        first.Hp = 30;
        var r = rt.Apply(Grant(), Neutral, 5);
        Assert.Equal(ShieldApplyOutcome.Merged, r.Outcome);
        Assert.Equal(100, r.Instance!.Hp);      // recast = paid refill
        Assert.Same(first, r.Instance);
    }

    [Fact]
    public void Aura_merge_clamps_never_heals()
    {
        var rt = new ShieldRuntime();
        var first = rt.Apply(Grant(source: "aura1", priority: ShieldPolicy.PriorityAura, refill: false), Neutral, 0).Instance!;
        first.Hp = 30;
        var r = rt.Apply(Grant(source: "aura1", priority: ShieldPolicy.PriorityAura, refill: false), Neutral, 5);
        Assert.Equal(ShieldApplyOutcome.Merged, r.Outcome);
        Assert.Equal(30, r.Instance!.Hp);       // idempotent re-assert: no free heal
    }

    [Fact]
    public void Merge_capacity_downgrade_clamps_current_hp()
    {
        var composer = new DerivedComposer();
        var buffed = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacityOmni, DerivedModifierOp.Flat, 400.0)
        });
        var rt = new ShieldRuntime();
        rt.Apply(Grant(source: "a", baseHp: 100, refill: false), buffed, 0);           // max 500, hp 500
        var r = rt.Apply(Grant(source: "a", baseHp: 100, refill: false), Neutral, 1);  // recompute max 100
        Assert.Equal(100, r.Instance!.MaxHp);
        Assert.Equal(100, r.Instance.Hp);       // min(500, 100) — clamped, not healed
    }

    [Fact]
    public void Merge_refresh_updates_expiry()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(duration: 10), Neutral, 0);
        var r = rt.Apply(Grant(duration: 10), Neutral, 7);
        Assert.Equal(17, r.Instance!.ExpiresAtTick);
    }

    [Fact]
    public void Merge_to_nonpositive_max_removes_instance()
    {
        var composer = new DerivedComposer();
        var debuffed = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacityOmni, DerivedModifierOp.Flat, -150.0)
        });
        var rt = new ShieldRuntime();
        rt.Apply(Grant(baseHp: 100), Neutral, 0);
        var r = rt.Apply(Grant(baseHp: 100), debuffed, 1);
        Assert.Equal(ShieldApplyOutcome.MergedRemoved, r.Outcome);
        Assert.Empty(rt.GetShields("entity:1"));
        Assert.False(rt.HasAnyInstances());
    }

    [Fact]
    public void Nonpositive_max_grant_rejected()
    {
        var composer = new DerivedComposer();
        var debuffed = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacityOmni, DerivedModifierOp.Flat, -100.0)
        });
        var rt = new ShieldRuntime();
        var r = rt.Apply(Grant(baseHp: 80), debuffed, 0);
        Assert.Equal(ShieldApplyOutcome.Rejected, r.Outcome);
        Assert.False(rt.HasAnyInstances());
    }

    [Fact]
    public void Cap_admission_rejects_weaker_trickle()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(source: "s1", baseHp: 400), Neutral, 0);
        rt.Apply(Grant(source: "s2", baseHp: 350), Neutral, 0);
        rt.Apply(Grant(source: "s3", baseHp: 300), Neutral, 0);
        // 20-HP aura trickle must NOT evict the 300-current shield.
        var r = rt.Apply(Grant(source: "aura", baseHp: 20, priority: ShieldPolicy.PriorityAura), Neutral, 0);
        Assert.Equal(ShieldApplyOutcome.DroppedWeaker, r.Outcome);
        Assert.Equal(3, rt.GetShields("entity:1").Count);
        Assert.Equal((1050, 1050), rt.Totals("entity:1"));
    }

    [Fact]
    public void Cap_admission_evicts_weakest_when_stronger()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(source: "s1", baseHp: 400), Neutral, 0);
        rt.Apply(Grant(source: "s2", baseHp: 350), Neutral, 0);
        var weak = rt.Apply(Grant(source: "s3", baseHp: 300), Neutral, 0).Instance!;
        weak.Hp = 50;   // damaged — its value is its CURRENT pool
        var r = rt.Apply(Grant(source: "s4", baseHp: 120), Neutral, 0);
        Assert.Equal(ShieldApplyOutcome.Applied, r.Outcome);
        Assert.Same(weak, r.Evicted);
        Assert.Equal(3, rt.GetShields("entity:1").Count);
    }

    [Fact]
    public void Same_source_different_element_takes_two_slots()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(source: "swap", el: ElementTypeId.Fire), Neutral, 0);
        var r = rt.Apply(Grant(source: "swap", el: ElementTypeId.Ice), Neutral, 0);
        Assert.Equal(ShieldApplyOutcome.Applied, r.Outcome);
        Assert.Equal(2, rt.GetShields("entity:1").Count);
    }

    [Fact]
    public void Drain_order_is_outer_to_core_aura_first()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(source: "innate", priority: ShieldPolicy.PriorityInnate, innate: true), Neutral, 0);
        rt.Apply(Grant(source: "aura", priority: ShieldPolicy.PriorityAura), Neutral, 0);
        rt.Apply(Grant(source: "skill", priority: ShieldPolicy.PrioritySkill), Neutral, 0);
        // Higher priority number drains first (owner decision 9): aura 30 → skill 20 → innate 10.
        var stack = rt.GetShields("entity:1");
        Assert.Equal(new[] { "aura:none", "skill:none", "innate:none" },
            stack.Select(s => s.ShieldId).ToArray());
    }

    [Fact]
    public void Drain_order_ties_break_by_created_seq()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(source: "a"), Neutral, 0);
        rt.Apply(Grant(source: "b"), Neutral, 0);
        var stack = rt.GetShields("entity:1");
        Assert.Equal(new[] { "a:none", "b:none" }, stack.Select(s => s.ShieldId).ToArray());
    }

    [Fact]
    public void RemoveAll_clears_owner()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(), Neutral, 0);
        rt.Apply(Grant(owner: "entity:2", source: "z"), Neutral, 0);
        rt.RemoveAll("entity:1");
        Assert.Empty(rt.GetShields("entity:1"));
        Assert.Single(rt.GetShields("entity:2"));
        Assert.True(rt.HasAnyInstances());
    }
}
