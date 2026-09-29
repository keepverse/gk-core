using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>Cascade goldens (spec §2.4 worked example) + tick upkeep (§2.6).</summary>
public class ShieldRuntimeAbsorbTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    static readonly IReadOnlyList<ElementPayloadComponent> FireHit =
        new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) };

    static ShieldGrant Grant(string source, ElementTypeId? el, long baseHp, int priority) =>
        new() { OwnerKey = "entity:1", SourceId = source, Element = el, BaseHp = baseHp, Priority = priority };

    static ShieldRuntime BuildWorkedStack()
    {
        // Spec §2.4 worked cascade: S1 ice 60 (drains first) → S2 earth 100 → S3 untyped 200.
        var rt = new ShieldRuntime();
        rt.Apply(Grant("s1", ElementTypeId.Ice, 60, ShieldPolicy.PriorityAura), Neutral, 0);
        rt.Apply(Grant("s2", ElementTypeId.Earth, 100, ShieldPolicy.PrioritySkill), Neutral, 0);
        rt.Apply(Grant("s3", null, 200, ShieldPolicy.PriorityInnate), Neutral, 0);
        return rt;
    }

    [Fact]
    public void Worked_cascade_golden_240_fire()
    {
        var rt = BuildWorkedStack();
        var spent = new List<(ShieldInstance Shield, long Spent)>();
        var broken = new List<ShieldInstance>();
        var remainder = rt.Absorb("entity:1", 240, 1, FireHit, null, Neutral, spent, broken);

        // S1 ice STR: d=300, spent 60 (broken), rem 192; S2 earth NEU: spent 100 (broken),
        // rem 92; S3 untyped holds: spent 92, rem 0. Total 252 spent on a 240 hit.
        Assert.Equal(0, remainder);
        Assert.Equal(new long[] { 60, 100, 92 }, spent.Select(s => s.Spent).ToArray());
        Assert.Equal(new[] { "s1:ice", "s2:earth" }, broken.Select(b => b.ShieldId).ToArray());
        Assert.Equal(252, spent.Sum(s => s.Spent));
        var survivor = Assert.Single(rt.GetShields("entity:1"));
        Assert.Equal(108, survivor.Hp);   // 200 − 92
    }

    [Fact]
    public void No_shield_fast_path_returns_input_unchanged()
    {
        var rt = new ShieldRuntime();
        Assert.Equal(500, rt.Absorb("entity:9", 500, 1, FireHit, null, Neutral));
    }

    [Fact]
    public void Heals_and_zero_pass_through()
    {
        var rt = BuildWorkedStack();
        Assert.Equal(0, rt.Absorb("entity:1", 0, 1, FireHit, null, Neutral));
        Assert.Equal((360, 360), rt.Totals("entity:1"));   // untouched
    }

    [Fact]
    public void Per_layer_flat_pen_charges_fresh_but_capped()
    {
        // pen +50 vs three 100-HP untyped shields, hit 30: per layer d = min(30+50, 3×input).
        var composer = new DerivedComposer();
        var attacker = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldPenOmni, DerivedModifierOp.Flat, 50.0)
        });
        var rt = new ShieldRuntime();
        rt.Apply(Grant("a", null, 100, 30), Neutral, 0);
        rt.Apply(Grant("b", null, 100, 20), Neutral, 0);
        rt.Apply(Grant("c", null, 100, 10), Neutral, 0);

        var spent = new List<(ShieldInstance Shield, long Spent)>();
        var remainder = rt.Absorb("entity:1", 30, 1, Array.Empty<ElementPayloadComponent>(), attacker, Neutral, spent);
        // L1: d = min(80, 90) = 80, spent 80, rem = round(30×0/80)... 80 ≤ 100 → holds, rem 0.
        Assert.Equal(0, remainder);
        Assert.Equal(80, spent.Single().Spent);
    }

    [Fact]
    public void Toughness_reduces_shield_burn()
    {
        var composer = new DerivedComposer();
        var owner = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldToughnessOmni, DerivedModifierOp.Flat, 40.0)
        });
        var rt = new ShieldRuntime();
        rt.Apply(Grant("a", null, 100, 20), owner, 0);
        var spent = new List<(ShieldInstance Shield, long Spent)>();
        rt.Absorb("entity:1", 100, 1, Array.Empty<ElementPayloadComponent>(), null, owner, spent);
        Assert.Equal(60, spent.Single().Spent);   // d = 100 − 40
    }

    [Fact]
    public void Remainders_are_monotone_across_cascade()
    {
        var rt = BuildWorkedStack();
        // Big hit breaks everything; every intermediate remainder ≤ its input by ShieldMath
        // invariants — end-to-end: remainder ≤ original damage.
        var remainder = rt.Absorb("entity:1", 5000, 1, FireHit, null, Neutral);
        Assert.InRange(remainder, 0, 5000);
        Assert.Empty(rt.GetShields("entity:1"));
    }
}

public class ShieldRuntimeTickTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    static ActorDerivedSnapshot RegenSnap(double perSecond)
    {
        var composer = new DerivedComposer();
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldRegenOmni, DerivedModifierOp.Flat, perSecond)
        });
    }

    static ShieldGrant Grant(string source, long baseHp, int priority, long? duration = null) =>
        new() { OwnerKey = "entity:1", SourceId = source, BaseHp = baseHp, Priority = priority, DurationTicks = duration };

    [Fact]
    public void Ten_100ms_ticks_equal_one_second_of_regen_exactly()
    {
        var rt = new ShieldRuntime();
        var snap = RegenSnap(7.0);   // 7 HP/sec — doesn't divide evenly per 100 ms tick
        var s = rt.Apply(Grant("a", 100, 20), snap, 0).Instance!;
        s.Hp = 10;
        for (var t = 0; t < 10; t++)
            rt.Tick(t, 100, _ => snap);
        Assert.Equal(17, s.Hp);      // carry accumulation: exactly +7 after 1 s
    }

    [Fact]
    public void Only_front_damaged_shield_regens_no_omni_multidip()
    {
        var rt = new ShieldRuntime();
        var snap = RegenSnap(10.0);
        var aura = rt.Apply(Grant("aura", 100, ShieldPolicy.PriorityAura), snap, 0).Instance!;
        var skill = rt.Apply(Grant("skill", 100, ShieldPolicy.PrioritySkill), snap, 0).Instance!;
        var innate = rt.Apply(Grant("innate", 100, ShieldPolicy.PriorityInnate), snap, 0).Instance!;
        aura.Hp = 50;
        skill.Hp = 50;
        innate.Hp = 50;
        for (var t = 0; t < 10; t++)
            rt.Tick(t, 100, _ => snap);
        Assert.Equal(60, aura.Hp);    // front shield gains the full 10
        Assert.Equal(50, skill.Hp);   // others untouched — total regen independent of count
        Assert.Equal(50, innate.Hp);
    }

    [Fact]
    public void Regen_caps_at_max_without_spill()
    {
        var rt = new ShieldRuntime();
        var snap = RegenSnap(1000.0);
        var front = rt.Apply(Grant("front", 100, 30), snap, 0).Instance!;
        var back = rt.Apply(Grant("back", 100, 20), snap, 0).Instance!;
        front.Hp = 99;
        back.Hp = 1;
        rt.Tick(0, 100, _ => snap);
        Assert.Equal(100, front.Hp);  // capped
        Assert.Equal(1, back.Hp);     // no spill to the next shield
    }

    [Fact]
    public void Full_shields_do_not_regen_or_resolve_snapshots()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant("a", 100, 20), Neutral, 0);
        var resolves = 0;
        rt.Tick(0, 100, _ => { resolves++; return Neutral; });
        Assert.Equal(0, resolves);    // undamaged stack: no snapshot work at all
    }

    [Fact]
    public void Expiry_removes_at_tick_and_reports()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant("a", 100, 20, duration: 5), Neutral, 0);
        var expired = new List<ShieldInstance>();
        rt.Tick(4, 100, _ => Neutral, expired);
        Assert.Empty(expired);
        rt.Tick(5, 100, _ => Neutral, expired);
        Assert.Equal("a:none", Assert.Single(expired).ShieldId);
        Assert.False(rt.HasAnyInstances());
    }

    [Fact]
    public void One_hp_survivor_regens_normally()
    {
        var rt = new ShieldRuntime();
        var snap = RegenSnap(5.0);
        var s = rt.Apply(Grant("a", 100, 20), snap, 0).Instance!;
        s.Hp = 1;   // survived dispatch at 1 HP — prune only removes ≤ 0
        for (var t = 0; t < 10; t++)
            rt.Tick(t, 100, _ => snap);
        Assert.Equal(6, s.Hp);
    }

    [Fact]
    public void Broken_in_dispatch_is_pruned_and_never_revived_by_tick()
    {
        var rt = new ShieldRuntime();
        var snap = RegenSnap(100.0);
        rt.Apply(Grant("a", 50, 20), snap, 0);
        var remainder = rt.Absorb("entity:1", 200, 1, Array.Empty<ElementPayloadComponent>(), null, snap);
        Assert.True(remainder > 0);
        Assert.False(rt.HasAnyInstances());
        rt.Tick(0, 100, _ => snap);
        Assert.False(rt.HasAnyInstances());   // nothing to revive
    }
}
