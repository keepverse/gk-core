using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects;
using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.Stats.Derived;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree;

/// <summary>
/// Tasks G2 (§11 tests 5, 6) and G3 (§11 tests 7, 8), each driven end-to-end through the REAL pipeline
/// rather than a hand-built instance, so the proof covers the actual wiring: <c>OnFreshApplication</c>
/// firing only on a genuinely fresh, landed status apply, and <c>DamageApplyPipeline.Apply</c>'s own
/// zero-delta/fully-absorbed outcomes reaching (or not reaching) <see cref="ElementMasteryCounter"/>.
/// </summary>
public class GateCounterRulesTests
{
    static readonly GateOwnerKey Player1 = new("player", "player:1");

    static CoreStatus.StatusRuntime Runtime(out StatusAppliedCounter counter, out GateCounterAccumulator acc)
    {
        var rt = new CoreStatus.StatusRuntime(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());
        acc = new GateCounterAccumulator();
        counter = new StatusAppliedCounter(acc, _ => Player1);
        rt.OnFreshApplication += counter.Handle;
        return rt;
    }

    static CoreStatus.StatusApplyInput WitherApply(string host = "Z1", string grantId = "g1") => new(
        "wither",
        HostPtr: host,
        AttackerPtr: "P1",
        GrantId: grantId,
        BaseMagnitude: 20,
        BaseDuration: 5000,
        PeriodMs: 1000,
        DurationMs: 5000);

    [Fact]
    public void A_resisted_application_earns_nothing()
    {
        // §11 test 5. rng.NextUnit() >= pFinal resists on the ApplyRoll phase for any pFinal <= 1 --
        // ResistanceEvaluator.Evaluate:230.
        var rt = Runtime(out _, out var acc);
        var now = DateTimeOffset.UtcNow;

        var outcome = rt.Apply(WitherApply(), new CoreStatus.FixedStatusRng(1.0), now);

        Assert.False(outcome.Applied);
        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void A_refresh_earns_nothing_and_a_fresh_host_earns_one()
    {
        // §11 test 6 -- "the cheapest farm in the game." wither is StatusStacking.Refresh
        // (StatusCatalogBootstrap.cs) and StatusIcdMs defaults to 0, so a reapply loop on one host
        // with the same GrantId is exactly what a real farm attempt looks like.
        var rt = Runtime(out _, out var acc);
        var now = DateTimeOffset.UtcNow;

        var first = rt.Apply(WitherApply(host: "Z1"), new CoreStatus.FixedStatusRng(0.0), now);
        Assert.True(first.Applied);
        Assert.Equal(1L, acc.Snapshot()[new GateCounterKey(Player1, StatusAppliedCounter.Quantity, "wither")]);

        // Same host, same GrantId, same StatusId -- UpsertInstance's Refresh path matches the existing
        // instance and reports NOT fresh; OnFreshApplication must not fire a second time.
        var refresh = rt.Apply(WitherApply(host: "Z1"), new CoreStatus.FixedStatusRng(0.0), now.AddMilliseconds(100));
        Assert.True(refresh.Applied);
        Assert.Equal(1L, acc.Snapshot()[new GateCounterKey(Player1, StatusAppliedCounter.Quantity, "wither")]);

        // A different host is a genuinely fresh instance and earns its own credit.
        var freshHost = rt.Apply(WitherApply(host: "Z2", grantId: "g2"), new CoreStatus.FixedStatusRng(0.0), now);
        Assert.True(freshHost.Applied);
        Assert.Equal(2L, acc.Snapshot()[new GateCounterKey(Player1, StatusAppliedCounter.Quantity, "wither")]);
    }

    [Fact]
    public void A_status_icd_block_never_reaches_OnFreshApplication_either()
    {
        // Belt-and-suspenders alongside the resist test: the ICD short-circuit
        // (StatusRuntime.cs IsStatusIcdBlocked) returns even earlier than the resist contest, and
        // must not credit either.
        var rt = Runtime(out _, out var acc);
        var now = DateTimeOffset.UtcNow;
        var input = WitherApply() with { StatusIcdMs = 5000 };

        rt.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);
        var blocked = rt.Apply(input with { GrantId = "g2" }, new CoreStatus.FixedStatusRng(0.0), now.AddMilliseconds(500));

        Assert.False(blocked.Applied);
        Assert.Equal(1L, acc.Snapshot()[new GateCounterKey(Player1, StatusAppliedCounter.Quantity, "wither")]);
    }

    sealed class RecordingSink : IHpDeltaSink
    {
        public bool Apply(string ownerKey, long amount, string? pluginId, string? effectId, string? grantId,
            string channel, List<ElementPayloadComponentDto>? elements) => true;
    }

    static ElementMasteryCounter ElementCounter(GateCounterAccumulator acc) =>
        new(acc, _ => Player1);

    /// <summary>§11 test 7. Two distinct earns-nothing paths through the REAL
    /// <c>DamageApplyPipeline.Apply</c>: a zero un-absorbed delta, which the pipeline deliberately lets
    /// reach the sink for miss-telemetry parity (<c>DamageApplyPipeline.cs:99-104</c>) but which is not
    /// a "use" by §2.2b's own reading; and a hit a shield eats entirely
    /// (<c>DamageApplyOutcome.FullyAbsorbed</c>, <c>:95-97</c>), where the element reached nothing.</summary>
    [Fact]
    public void A_zero_damage_hit_and_a_fully_absorbed_hit_earn_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = ElementCounter(acc);
        var components = new ElementPayloadComponent[] { new(ElementTypeId.Fire, 1.0) };

        var zero = DamageApplyPipeline.Apply(
            "z1", 0, 1, components, attackerSnapshot: null, ownerSnapshot: ActorDerivedSnapshot.StubNeutral(),
            shieldGate: null, sink: new RecordingSink());
        Assert.Equal(DamageApplyOutcome.Applied, zero.Outcome);
        Assert.Equal(0, zero.AppliedAmount);
        counter.Handle(new ElementMasteryCreditInput(zero, DamageOrigin.DirectHit, components, "P1"));
        Assert.Empty(acc.Snapshot());

        var runtime = new ShieldRuntime();
        runtime.Apply(new ShieldGrant { OwnerKey = "entity:z1", SourceId = "s", BaseHp = 500 },
            ActorDerivedSnapshot.StubNeutral(), 0);
        var gate = new ShieldGate(runtime, (_, _) =>
            new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral));

        var absorbed = DamageApplyPipeline.Apply(
            "z1", -100, 1, components, attackerSnapshot: null, ownerSnapshot: ActorDerivedSnapshot.StubNeutral(),
            shieldGate: gate, sink: new RecordingSink());
        Assert.Equal(DamageApplyOutcome.FullyAbsorbed, absorbed.Outcome);
        counter.Handle(new ElementMasteryCreditInput(absorbed, DamageOrigin.DirectHit, components, "P1"));
        Assert.Empty(acc.Snapshot());
    }

    /// <summary>§11 test 8, through the real pipeline: a hybrid two-element hit that actually lands
    /// (non-zero, unabsorbed) credits both elements once each.</summary>
    [Fact]
    public void A_two_element_packet_credits_both_elements_once()
    {
        var acc = new GateCounterAccumulator();
        var counter = ElementCounter(acc);
        var components = new ElementPayloadComponent[]
        {
            new(ElementTypeId.Fire, 0.6),
            new(ElementTypeId.Ice, 0.4),
        };

        var result = DamageApplyPipeline.Apply(
            "z1", -50, 1, components, attackerSnapshot: null, ownerSnapshot: ActorDerivedSnapshot.StubNeutral(),
            shieldGate: null, sink: new RecordingSink());
        Assert.Equal(DamageApplyOutcome.Applied, result.Outcome);
        Assert.NotEqual(0, result.AppliedAmount);

        counter.Handle(new ElementMasteryCreditInput(result, DamageOrigin.DirectHit, components, "P1"));

        var snap = acc.Snapshot();
        Assert.Equal(1L, snap[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, "fire")]);
        Assert.Equal(1L, snap[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, "ice")]);
    }

    /// <summary>The DoT-pulse half of §2.2d, through the real pipeline: a landed, non-zero hit that
    /// carries <see cref="DamageOrigin.StatusPulse"/> (a `wither` tick, e.g.) still earns nothing --
    /// crediting it would double-pay for the one application <c>status_applied</c> already credited.</summary>
    [Fact]
    public void A_wither_pulse_hit_through_the_real_pipeline_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = ElementCounter(acc);
        var components = new ElementPayloadComponent[] { new(ElementTypeId.Dark, 1.0) };

        var result = DamageApplyPipeline.Apply(
            "z1", -20, 1, components, attackerSnapshot: null, ownerSnapshot: ActorDerivedSnapshot.StubNeutral(),
            shieldGate: null, sink: new RecordingSink());
        Assert.Equal(DamageApplyOutcome.Applied, result.Outcome);

        counter.Handle(new ElementMasteryCreditInput(result, DamageOrigin.StatusPulse, components, "P1"));

        Assert.Empty(acc.Snapshot());
    }
}
