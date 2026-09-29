using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>
/// Coverage locks promised by shield-todo §7 that the build pass owed: determinism replay,
/// no-shield fast-path cost (zero alloc, zero snapshot resolves), and the merge-expiry edge.
/// </summary>
public class ShieldDeterminismTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    static ActorDerivedSnapshot Loaded()
    {
        var composer = new DerivedComposer();
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldCapacityOmni, DerivedModifierOp.Flat, 25.0),
            new DerivedModifier(DerivedStatChannels.CombatShieldRegenOmni, DerivedModifierOp.Flat, 7.0),
            new DerivedModifier(DerivedStatChannels.CombatShieldToughness(ElementTypeId.Ice), DerivedModifierOp.Flat, 15.0),
            new DerivedModifier(DerivedStatChannels.CombatShieldPenOmni, DerivedModifierOp.Flat, 10.0)
        });
    }

    /// <summary>Scripted mixed workload — grants, hybrid absorbs, ticks, expiry, eviction.</summary>
    static (List<(string, ElementTypeId?, long, long, int)> Stacks, List<ShieldEventRec> Events) RunScript()
    {
        var rt = new ShieldRuntime();
        var loaded = Loaded();
        var events = new List<ShieldEventRec>();
        var hybrid = new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.7),
            new ElementPayloadComponent(ElementTypeId.Air, 0.3)
        };

        ShieldGrant G(string src, ElementTypeId? el, long hp, int prio, long? dur = null, bool refill = true) =>
            new() { OwnerKey = "entity:a", SourceId = src, Element = el, BaseHp = hp, Priority = prio, DurationTicks = dur, RefillOnMerge = refill };

        rt.Apply(G("innate", null, 200, ShieldPolicy.PriorityInnate), loaded, 0);
        rt.Apply(G("skill", ElementTypeId.Ice, 120, ShieldPolicy.PrioritySkill, dur: 40), loaded, 0);
        rt.Apply(G("aura", ElementTypeId.Dark, 60, ShieldPolicy.PriorityAura, refill: false), loaded, 0);
        rt.QueueInnate(G("innate:late", null, 90, ShieldPolicy.PriorityInnate));

        for (long t = 1; t <= 50; t++)
        {
            rt.Tick(t, 100, _ => loaded, null);
            if (t % 7 == 0)
                rt.Absorb("entity:a", 33 + t, t % 3 + 1, hybrid, loaded, loaded);
            if (t == 20)
                rt.Apply(G("skill2", ElementTypeId.Light, 500, ShieldPolicy.PrioritySkill), loaded, t);   // eviction pressure
            if (t == 30)
                rt.Apply(G("aura", ElementTypeId.Dark, 60, ShieldPolicy.PriorityAura, refill: false), loaded, t);   // idempotent re-assert
            rt.DrainEvents(events);
        }

        var stacks = rt.GetShields("entity:a")
            .Select(s => (s.ShieldId, s.Element, s.Hp, s.MaxHp, s.Priority))
            .ToList();
        return (stacks, events);
    }

    [Fact]
    public void Identical_scripts_replay_to_identical_state_and_events()
    {
        var run1 = RunScript();
        var run2 = RunScript();

        Assert.Equal(run1.Stacks, run2.Stacks);
        Assert.Equal(run1.Events.Count, run2.Events.Count);
        for (var i = 0; i < run1.Events.Count; i++)
        {
            var a = run1.Events[i];
            var b = run2.Events[i];
            Assert.Equal((a.Kind, a.OwnerKey, a.ShieldId, a.Element, a.Amount, a.HitCount, a.Hp, a.MaxHp),
                (b.Kind, b.OwnerKey, b.ShieldId, b.Element, b.Amount, b.HitCount, b.Hp, b.MaxHp));
        }
    }

    [Fact]
    public void No_shield_absorb_fast_path_allocates_nothing()
    {
        var rt = new ShieldRuntime();
        var components = Array.Empty<ElementPayloadComponent>();
        // Warm-up (JIT and any lazy statics).
        rt.Absorb("entity:cold", 100, 1, components, null, Neutral);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            rt.Absorb("entity:cold", 100, 1, components, null, Neutral);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Gate_fast_path_never_resolves_snapshots()
    {
        var runtime = new ShieldRuntime();
        CombatActorResolve throwingResolve = (_, _) =>
            throw new InvalidOperationException("fast path must not resolve actors");
        var gate = new ShieldGate(runtime, throwingResolve);
        var packet = new DamagePacket();

        // No instances at all → HasAnyInstances short-circuit.
        Assert.Equal(-50, gate.AbsorbFinalized(-50, "z9", packet, 1));

        // Instances exist for ANOTHER owner → per-owner miss short-circuit.
        runtime.Apply(new ShieldGrant { OwnerKey = "entity:other", SourceId = "s", BaseHp = 10 }, Neutral, 0);
        Assert.Equal(-50, gate.AbsorbFinalized(-50, "z9", packet, 1));

        // Heals never resolve either, even with shields on the target's key.
        runtime.Apply(new ShieldGrant { OwnerKey = "entity:z9", SourceId = "s", BaseHp = 10 }, Neutral, 0);
        Assert.Equal(50, gate.AbsorbFinalized(50, "z9", packet, 1));
    }

    [Fact]
    public void Merge_refresh_without_duration_clears_expiry()
    {
        // Locked edge: merge always refreshes expiry FROM THE NEW GRANT — a no-duration
        // re-grant makes the shield permanent (recast upgrades a timed shield).
        var rt = new ShieldRuntime();
        ShieldGrant G(long? dur) => new()
            { OwnerKey = "entity:a", SourceId = "s", BaseHp = 100, DurationTicks = dur };
        rt.Apply(G(10), Neutral, 0);
        var merged = rt.Apply(G(null), Neutral, 5);
        Assert.Equal(ShieldApplyOutcome.Merged, merged.Outcome);
        Assert.Null(merged.Instance!.ExpiresAtTick);
        rt.Tick(100, 100, _ => Neutral);
        Assert.True(rt.HasAnyInstances());
    }
}

[Collection("PerfProbe")]
public class ShieldPerfProbeTests
{
    [Fact]
    public void Engaged_absorb_records_the_shield_absorb_section()
    {
        PerfProbe.ResetAll();
        var runtime = new ShieldRuntime();
        var neutral = ActorDerivedSnapshot.StubNeutral();
        runtime.Apply(new ShieldGrant { OwnerKey = "entity:p1", SourceId = "s", BaseHp = 100 }, neutral, 0);
        var gate = new ShieldGate(runtime, (_, _) =>
            new CombatActorSnapshot(neutral, ActorElementTypes.Neutral));

        var remainder = gate.AbsorbFinalized(-40, "p1", new DamagePacket(), 1);
        Assert.Equal(0, remainder);

        var window = PerfProbe.SnapshotAndReset();
        var sections = Assert.IsType<Dictionary<string, object>>(window["sections"]);
        Assert.True(sections.TryGetValue("shield.absorb", out var s), "shield.absorb section missing");
        Assert.True((long)((Dictionary<string, object>)s!)["count"] >= 1);
    }
}
