using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

/// <summary>Merged-record proc math — event-pipeline-v2-spec.md decision #2 (test group 3).</summary>
public class ProcMergeMathTests
{
    static EffectGrant GrantWithOverlay(Dictionary<string, object?> overlay) =>
        EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "g-proc",
            EffectId = "fx.test",
            OwnerKey = "match",
            Overlay = overlay
        });

    [Fact]
    public void Counter_advances_by_hit_count_and_bursts_on_crossing()
    {
        var rt = new StatusRuntime(StatusCatalogBootstrap.CreateDefault(), (_, _) => ActorDerivedSnapshot.AttackerLess());

        // 3 + 2 hits cross an every-5 threshold on the second call — one burst.
        Assert.False(rt.RecordCounterHit("g1", "t1", everyHits: 5, resetOnBurst: true, hits: 3));
        Assert.True(rt.RecordCounterHit("g1", "t1", everyHits: 5, resetOnBurst: true, hits: 2));
        // Reset happened — next 4 hits do not burst.
        Assert.False(rt.RecordCounterHit("g1", "t1", everyHits: 5, resetOnBurst: true, hits: 4));
    }

    [Fact]
    public void Counter_single_call_over_threshold_bursts_once()
    {
        var rt = new StatusRuntime(StatusCatalogBootstrap.CreateDefault(), (_, _) => ActorDerivedSnapshot.AttackerLess());
        // 12 merged hits over an every-5 threshold: one burst per call (spec decision #2).
        Assert.True(rt.RecordCounterHit("g1", "t1", everyHits: 5, resetOnBurst: true, hits: 12));
    }

    [Fact]
    public void Counter_burst_keeps_residual_hits()
    {
        // I3 (review): merged records must not eat progress — 7 hits over an every-3 counter
        // bursts once (spec: one burst per crossing) and keeps residual 1, so 2 more hits
        // cross again.
        var rt = new StatusRuntime(StatusCatalogBootstrap.CreateDefault(), (_, _) => ActorDerivedSnapshot.AttackerLess());
        Assert.True(rt.RecordCounterHit("g1", "t1", everyHits: 3, resetOnBurst: true, hits: 7));
        Assert.True(rt.RecordCounterHit("g1", "t1", everyHits: 3, resetOnBurst: true, hits: 2));
        Assert.False(rt.RecordCounterHit("g1", "t1", everyHits: 3, resetOnBurst: true, hits: 2));
    }

    [Fact]
    public void Counter_hits_one_matches_legacy_behavior()
    {
        var rt = new StatusRuntime(StatusCatalogBootstrap.CreateDefault(), (_, _) => ActorDerivedSnapshot.AttackerLess());
        for (var i = 0; i < 4; i++)
            Assert.False(rt.RecordCounterHit("g1", "t1", everyHits: 5, resetOnBurst: true));
        Assert.True(rt.RecordCounterHit("g1", "t1", everyHits: 5, resetOnBurst: true));
    }

    [Fact]
    public void Chance_hit_count_one_is_unchanged()
    {
        var policy = new EffectProcPolicy(new SystemEffectClock(), new SeededEffectRandom(7));
        var grant = GrantWithOverlay(new Dictionary<string, object?> { ["chance"] = 1.0, ["icd_ms"] = 0 });
        Assert.True(policy.TryPass(grant, EffectTriggers.OnDamageDealt, out _));
    }

    [Fact]
    public void Chance_merged_hits_follow_closed_form_distribution()
    {
        // P(≥1 proc of n=5 at p=0.2) = 1−0.8^5 = 0.67232. Seeded rng over 10k trials.
        var policy = new EffectProcPolicy(new SystemEffectClock(), new SeededEffectRandom(42));
        var grant = GrantWithOverlay(new Dictionary<string, object?> { ["chance"] = 0.2, ["icd_ms"] = 0 });

        var passes = 0;
        const int trials = 10_000;
        for (var i = 0; i < trials; i++)
        {
            if (policy.TryPass(grant, EffectTriggers.OnDamageDealt, out _, hitCount: 5))
                passes++;
        }

        var rate = passes / (double)trials;
        Assert.InRange(rate, 0.6523, 0.6923); // 0.67232 ± 0.02
    }

    [Fact]
    public void Max_stacks_consumes_per_hit_clamped()
    {
        var policy = new EffectProcPolicy(new SystemEffectClock(), new SeededEffectRandom(1));
        var grant = GrantWithOverlay(new Dictionary<string, object?> { ["max_stacks"] = 3, ["icd_ms"] = 0 });

        // One merged record of 5 hits consumes up to the cap; the next record is blocked.
        Assert.True(policy.TryPass(grant, EffectTriggers.OnDamageDealt, out _, hitCount: 5));
        Assert.False(policy.TryPass(grant, EffectTriggers.OnDamageDealt, out var reason, hitCount: 1));
        Assert.Equal("max_stacks", reason);
    }

    [Fact]
    public void Max_stacks_hit_count_one_matches_legacy()
    {
        var policy = new EffectProcPolicy(new SystemEffectClock(), new SeededEffectRandom(1));
        var grant = GrantWithOverlay(new Dictionary<string, object?> { ["max_stacks"] = 2, ["icd_ms"] = 0 });
        Assert.True(policy.TryPass(grant, EffectTriggers.OnDamageDealt, out _));
        Assert.True(policy.TryPass(grant, EffectTriggers.OnDamageDealt, out _));
        Assert.False(policy.TryPass(grant, EffectTriggers.OnDamageDealt, out var reason));
        Assert.Equal("max_stacks", reason);
    }
}
