using FusionRpg.Core.Stats.Derived;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

public class StatusRuntimeTests
{
    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    static CoreStatus.StatusApplyInput WitherApply(DateTimeOffset now) => new(
        "wither",
        HostPtr: "Z1",
        AttackerPtr: "P1",
        GrantId: "g1",
        BaseMagnitude: 20,
        BaseDuration: 5000,
        PeriodMs: 1000,
        DurationMs: 5000);

    [Fact]
    public void Apply_tick_expire_lifecycle()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var outcome = rt.Apply(WitherApply(now), new CoreStatus.FixedStatusRng(0.0), now);
        Assert.True(outcome.Applied);
        Assert.NotNull(outcome.Instance);

        var sink = new RecordingPulseSink();
        Assert.Equal(0, rt.Tick(now, sink));
        Assert.Equal(1, rt.Tick(now.AddMilliseconds(1000), sink));
        Assert.Equal(1, sink.Count);

        rt.Tick(now.AddMilliseconds(6000), sink);
        Assert.Empty(rt.ForHost("Z1"));
    }

    [Fact]
    public void Elemental_family_replace()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        rt.Apply(WitherApply(now) with { StatusId = "freeze", GrantId = "g-freeze" }, new CoreStatus.FixedStatusRng(0.0), now);
        rt.Apply(WitherApply(now) with { StatusId = "cold", GrantId = "g-cold" }, new CoreStatus.FixedStatusRng(0.0), now);

        var ids = rt.ForHost("Z1").Select(i => i.StatusId).ToList();
        Assert.DoesNotContain("freeze", ids, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("cold", ids, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_icd_separate_from_period()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var input = WitherApply(now) with { StatusIcdMs = 5000 };
        rt.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);
        var second = rt.Apply(input with { GrantId = "g2" }, new CoreStatus.FixedStatusRng(0.0), now.AddMilliseconds(500));
        Assert.False(second.Applied);
        Assert.Equal(CoreStatus.StatusResistReason.StatusIcd, second.ResistReason);
        Assert.Empty(rt.ResistedEvents);
    }

    [Fact]
    public void WithdrawEntity_does_not_fire_OnEnded()
    {
        var rt = Runtime();
        var ended = 0;
        rt.OnEnded = _ => ended++;
        var now = DateTimeOffset.UtcNow;
        rt.Apply(WitherApply(now), new CoreStatus.FixedStatusRng(0.0), now);
        Assert.NotEmpty(rt.ForHost("Z1"));

        var taken = rt.TakeHostInstances("Z1");
        Assert.Single(taken);
        Assert.Equal(0, ended);
        Assert.Empty(rt.ForHost("Z1"));
    }

    [Fact]
    public void Withdraw_entity_clears_instances()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        rt.Apply(WitherApply(now), new CoreStatus.FixedStatusRng(0.0), now);
        rt.WithdrawEntity("Z1");
        Assert.Empty(rt.ForHost("Z1"));
    }

    sealed class RecordingPulseSink : CoreStatus.IStatusPulseSink
    {
        public int Count { get; private set; }

        public void PulseHp(CoreStatus.StatusInstance instance, double amount) => Count++;
    }
}
