using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Vfx;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>
/// vfx-v3 V1: OnEnded fires at exactly three sites (expiry prune, ClearGrant, family mutex)
/// and stays silent on refresh/replace re-applies, WithdrawEntity, and Clear.
/// </summary>
public class StatusVfxEndTests
{
    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    static (CoreStatus.StatusRuntime Rt, List<CoreStatus.StatusInstance> Ended) Wired()
    {
        var rt = Runtime();
        var ended = new List<CoreStatus.StatusInstance>();
        rt.OnEnded = ended.Add;
        return (rt, ended);
    }

    static CoreStatus.StatusApplyOutcome Apply(
        CoreStatus.StatusRuntime rt, string statusId, string host, string grant,
        DateTimeOffset now, int durationMs = 1000) =>
        rt.Apply(
            new CoreStatus.StatusApplyInput(statusId, host, "P1", grant, 20, durationMs),
            new CoreStatus.FixedStatusRng(0.0),
            now);

    [Fact]
    public void Expiry_prune_fires_ended_once()
    {
        var (rt, ended) = Wired();
        var now = DateTimeOffset.UtcNow;
        Assert.True(Apply(rt, "wither", "Z1", "g1", now, durationMs: 500).Applied);
        rt.Tick(now.AddMilliseconds(100), null);
        Assert.Empty(ended);
        rt.Tick(now.AddMilliseconds(600), null);
        var e = Assert.Single(ended);
        Assert.Equal("wither", e.StatusId);
        Assert.Equal("Z1", e.HostPtr);
        Assert.Empty(rt.ForHost("Z1"));
        // no double-fire on later ticks
        rt.Tick(now.AddMilliseconds(700), null);
        Assert.Single(ended);
    }

    [Fact]
    public void Reapply_never_fires_ended()
    {
        var (rt, ended) = Wired();
        var now = DateTimeOffset.UtcNow;
        Assert.True(Apply(rt, "wither", "Z1", "g1", now).Applied);
        Assert.True(Apply(rt, "wither", "Z1", "g1", now.AddMilliseconds(100)).Applied);
        Assert.True(Apply(rt, "wither", "Z1", "g1", now.AddMilliseconds(200)).Applied);
        Assert.Empty(ended); // refresh/replace stacking must not flicker sustained visuals
    }

    [Fact]
    public void Clear_grant_fires_ended_per_matched_instance()
    {
        var (rt, ended) = Wired();
        var now = DateTimeOffset.UtcNow;
        Assert.True(Apply(rt, "wither", "Z1", "g1", now).Applied);
        Assert.True(Apply(rt, "expose", "Z2", "g1", now).Applied);
        Assert.True(Apply(rt, "rally", "Z3", "gOther", now).Applied);
        rt.ClearGrant("g1");
        Assert.Equal(2, ended.Count);
        Assert.Contains(ended, e => e.HostPtr == "Z1" && e.StatusId == "wither");
        Assert.Contains(ended, e => e.HostPtr == "Z2" && e.StatusId == "expose");
        Assert.NotEmpty(rt.ForHost("Z3"));
    }

    [Fact]
    public void Family_mutex_fires_ended_for_displaced_elemental()
    {
        var catalog = CoreStatus.StatusCatalogBootstrap.CreateDefault();
        var elementals = catalog.All()
            .Where(d => string.Equals(d.Family, "elemental", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.StatusId)
            .Take(2)
            .ToList();
        Assert.Equal(2, elementals.Count); // catalog must have >= 2 elemental statuses for the mutex to matter

        var (rt, ended) = Wired();
        var now = DateTimeOffset.UtcNow;
        Assert.True(Apply(rt, elementals[0], "Z1", "g1", now).Applied);
        Assert.True(Apply(rt, elementals[1], "Z1", "g2", now.AddMilliseconds(50)).Applied);
        var e = Assert.Single(ended);
        Assert.Equal(elementals[0], e.StatusId);
    }

    [Fact]
    public void Withdraw_entity_and_clear_stay_silent()
    {
        var (rt, ended) = Wired();
        var now = DateTimeOffset.UtcNow;
        Assert.True(Apply(rt, "wither", "Z1", "g1", now).Applied);
        Assert.True(Apply(rt, "rally", "Z2", "g2", now).Applied);
        rt.WithdrawEntity("Z1"); // host death — VFX reaps via host-gone anchor
        rt.Clear();              // match teardown — VFX ClearAll covers
        Assert.Empty(ended);
    }

    [Fact]
    public void Apply_cue_carries_duration_and_expire_cue_is_shaped()
    {
        var (rt, _) = Wired();
        var now = DateTimeOffset.UtcNow;
        Apply(rt, "wither", "Z1", "g1", now, durationMs: 4000);
        var inst = rt.ForHost("Z1").Single();
        var applyCue = StatusVfxCues.Cue(inst);
        Assert.InRange(applyCue.DurationMs, 3900, 4100);
        var expire = StatusVfxCues.ExpireCue(inst);
        Assert.Equal("status.wither.expire", expire.CueId);
        Assert.Equal("Z1", expire.TargetPtr);
        Assert.Equal("status.wither.expire", StatusVfxCues.ExpireCueId("Wither"));
    }
}
