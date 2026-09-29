using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>Event aggregation + ordering (T13, spec §2.6).</summary>
public class ShieldEventTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    static ShieldGrant Grant(string source = "g1", long baseHp = 100, int priority = 20, long? duration = null) =>
        new() { OwnerKey = "entity:1", SourceId = source, BaseHp = baseHp, Priority = priority, DurationTicks = duration };

    static List<ShieldEventRec> Drain(ShieldRuntime rt)
    {
        var into = new List<ShieldEventRec>();
        rt.DrainEvents(into);
        return into;
    }

    [Fact]
    public void Apply_emits_granted_merge_is_silent()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(), Neutral, 0);
        rt.Apply(Grant(), Neutral, 1);   // merge — no second event
        var events = Drain(rt);
        var rec = Assert.Single(events);
        Assert.Equal(ShieldEventKinds.Granted, rec.Kind);
        Assert.Equal("g1:none", rec.ShieldId);
        Assert.Equal(100, rec.Hp);
    }

    [Fact]
    public void Absorbs_aggregate_per_shield_per_window()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(baseHp: 500), Neutral, 0);
        Drain(rt);   // clear granted

        rt.Absorb("entity:1", 40, 2, Array.Empty<ElementPayloadComponent>(), null, Neutral);
        rt.Absorb("entity:1", 25, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral);
        var events = Drain(rt);
        var rec = Assert.Single(events);
        Assert.Equal(ShieldEventKinds.Absorbed, rec.Kind);
        Assert.Equal(65, rec.Amount);
        Assert.Equal(3, rec.HitCount);
        Assert.Equal(435, rec.Hp);
    }

    [Fact]
    public void Break_orders_absorbed_before_broken_and_closes_aggregate()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(baseHp: 50), Neutral, 0);
        Drain(rt);

        rt.Absorb("entity:1", 80, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral);
        var events = Drain(rt);
        Assert.Equal(2, events.Count);
        Assert.Equal(ShieldEventKinds.Absorbed, events[0].Kind);
        Assert.Equal(50, events[0].Amount);
        Assert.Equal(ShieldEventKinds.Broken, events[1].Kind);
        Assert.Equal(0, events[1].Hp);
    }

    [Fact]
    public void Regrant_after_break_opens_fresh_aggregate()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(baseHp: 50), Neutral, 0);
        rt.Absorb("entity:1", 80, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral);
        rt.Apply(Grant(baseHp: 50), Neutral, 1);   // same source id, fresh instance
        rt.Absorb("entity:1", 10, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral);

        var events = Drain(rt);
        // granted, absorbed(50), broken, granted, absorbed(10) — two separate aggregates.
        Assert.Equal(5, events.Count);
        Assert.Equal(50, events[1].Amount);
        Assert.Equal(10, events[4].Amount);
    }

    [Fact]
    public void Expiry_and_eviction_emit_expired_not_broken()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant("timed", duration: 3), Neutral, 0);
        rt.Tick(3, 100, _ => Neutral);
        var expired = Drain(rt).Last();
        Assert.Equal(ShieldEventKinds.Expired, expired.Kind);

        rt.Apply(Grant("s1", 400), Neutral, 0);
        rt.Apply(Grant("s2", 350), Neutral, 0);
        rt.Apply(Grant("s3", 300), Neutral, 0);
        Drain(rt);
        rt.Apply(Grant("s4", 500), Neutral, 0);   // evicts weakest s3
        var events = Drain(rt);
        Assert.Equal(2, events.Count);
        Assert.Equal(ShieldEventKinds.Expired, events[0].Kind);   // eviction first
        Assert.Equal("s3:none", events[0].ShieldId);
        Assert.Equal(ShieldEventKinds.Granted, events[1].Kind);
    }

    [Fact]
    public void Death_flush_emits_nothing()
    {
        var rt = new ShieldRuntime();
        rt.Apply(Grant(), Neutral, 0);
        Drain(rt);
        rt.RemoveAll("entity:1");
        Assert.Empty(Drain(rt));
    }

    [Fact]
    public void Noisy_kind_covers_absorbed_only()
    {
        Assert.True(FusionRpg.Contracts.RpgConstants.IsNoisyKind(ShieldEventKinds.Absorbed));
        Assert.False(FusionRpg.Contracts.RpgConstants.IsNoisyKind(ShieldEventKinds.Broken));
        Assert.False(FusionRpg.Contracts.RpgConstants.IsNoisyKind(ShieldEventKinds.Granted));
        Assert.False(FusionRpg.Contracts.RpgConstants.IsNoisyKind(ShieldEventKinds.Expired));
    }
}
