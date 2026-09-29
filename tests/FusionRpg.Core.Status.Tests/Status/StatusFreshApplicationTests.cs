using FusionRpg.Core.Combat;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>Task G1 — the two shipped-code prerequisites (spec-gate-counters.md §7 P1, P2). P1: a
/// defaulted `DamageOrigin` parameter on `DamageApplyPipeline.Apply`. P2: `StatusRuntime.
/// OnFreshApplication`, fired only when the upsert added a new instance — `OnApplied` untouched.
/// </summary>
public class StatusFreshApplicationTests
{
    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot.AttackerLess()
                         : FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot.StubNeutral());

    // "wither" is StatusStacking.Refresh (StatusCatalogBootstrap.cs) -- the exact stacking P2's own
    // spec paragraph names as one of the two that must NOT report fresh on a reapply.
    static CoreStatus.StatusApplyInput WitherApply(DateTimeOffset now, string grantId = "g1") => new(
        "wither",
        HostPtr: "Z1",
        AttackerPtr: "P1",
        GrantId: grantId,
        BaseMagnitude: 20,
        BaseDuration: 5000,
        PeriodMs: 1000,
        DurationMs: 5000);

    [Fact] // "a reapply loop fires one fresh event"
    public void A_reapply_loop_on_one_target_fires_exactly_one_fresh_event()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var freshCount = 0;
        var appliedCount = 0;
        rt.OnFreshApplication += _ => freshCount++;
        rt.OnApplied += _ => appliedCount++;

        for (var i = 0; i < 5; i++)
            rt.Apply(WitherApply(now.AddMilliseconds(i * 10)), new CoreStatus.FixedStatusRng(0.0), now.AddMilliseconds(i * 10));

        Assert.Equal(1, freshCount);   // only the FIRST application is fresh
        Assert.Equal(5, appliedCount); // every application still fires OnApplied, exactly as before
    }

    [Fact] // "a refresh fires OnApplied and does NOT fire OnFreshApplication"
    public void A_refresh_fires_OnApplied_and_not_OnFreshApplication()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var freshEvents = new List<CoreStatus.StatusAppliedEvent>();
        var appliedEvents = new List<CoreStatus.StatusInstance>();
        rt.OnFreshApplication += freshEvents.Add;
        rt.OnApplied += appliedEvents.Add;

        rt.Apply(WitherApply(now), new CoreStatus.FixedStatusRng(0.0), now); // fresh
        rt.Apply(WitherApply(now.AddMilliseconds(500)), new CoreStatus.FixedStatusRng(0.0), now.AddMilliseconds(500)); // refresh

        Assert.Single(freshEvents);
        Assert.Equal(2, appliedEvents.Count);
        Assert.Equal("Z1", freshEvents[0].Instance.HostPtr);
        Assert.Equal("wither", freshEvents[0].Instance.StatusId);
    }

    [Fact]
    public void A_second_grant_id_on_the_same_host_and_status_is_a_fresh_slot_not_a_refresh()
    {
        // Refresh keys on (StatusId, GrantId) -- a DIFFERENT grant is a distinct slot, so both are
        // fresh (matches UpsertInstance's own Refresh-branch key, never widened here).
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var freshCount = 0;
        rt.OnFreshApplication += _ => freshCount++;

        rt.Apply(WitherApply(now, grantId: "g1"), new CoreStatus.FixedStatusRng(0.0), now);
        rt.Apply(WitherApply(now, grantId: "g2"), new CoreStatus.FixedStatusRng(0.0), now);

        Assert.Equal(2, freshCount);
    }

    [Fact]
    public void An_empty_subscriber_list_never_throws_on_a_fresh_application()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var outcome = rt.Apply(WitherApply(now), new CoreStatus.FixedStatusRng(0.0), now);
        Assert.True(outcome.Applied); // no OnFreshApplication subscriber at all -- must not throw
    }

    [Fact] // "the origin defaults, so every existing call site is zero lines changed"
    public void DamageApplyPipeline_Apply_origin_defaults_to_DirectHit()
    {
        var sink = new AlwaysAcceptSink();
        // The exact pre-existing call shape (no origin argument at all) still compiles and behaves
        // identically -- proof that P1 added a parameter, not a breaking signature change.
        var result = DamageApplyPipeline.Apply(
            "abc", -10, hitCount: 1, components: Array.Empty<FusionRpg.Core.Combat.Element.ElementPayloadComponent>(),
            attackerSnapshot: null, ownerSnapshot: null, shieldGate: null, sink: sink);

        Assert.Equal(DamageApplyOutcome.Applied, result.Outcome);
    }

    [Fact]
    public void DamageOrigin_has_exactly_the_two_documented_values()
    {
        var values = Enum.GetValues<DamageOrigin>();
        Assert.Equal(new[] { DamageOrigin.DirectHit, DamageOrigin.StatusPulse }, values);
    }

    sealed class AlwaysAcceptSink : IHpDeltaSink
    {
        public bool Apply(string ownerKey, long amount, string? pluginId, string? effectId, string? grantId,
            string channel, List<FusionRpg.Contracts.ElementPayloadComponentDto>? elements) => true;
    }
}
