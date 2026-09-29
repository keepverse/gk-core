using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.GateCounters;

/// <summary>Task G3 -- spec-gate-counters.md §2.2's rules, unit-level over
/// <see cref="ElementMasteryCounter.Handle"/> directly (the pipeline-shaped halves -- zero/absorbed
/// outcomes, §11 tests 7-8 -- are proven end-to-end through the real <c>DamageApplyPipeline.Apply</c>
/// in <c>GateCounterRulesTests</c> instead, mirroring G2's own split for <c>StatusAppliedCounter</c>).</summary>
public class ElementMasteryCounterTests
{
    static readonly GateOwnerKey Player1 = new("player", "player:1");

    static ElementMasteryCounter Counter(GateCounterAccumulator acc, Func<string, GateOwnerKey?>? resolve = null) =>
        new(acc, resolve ?? (_ => Player1));

    static DamageApplyResult Applied(long amount = -50) => new(DamageApplyOutcome.Applied, amount, 0);

    static ElementMasteryCreditInput Input(
        DamageApplyResult? result = null,
        DamageOrigin origin = DamageOrigin.DirectHit,
        IReadOnlyList<ElementPayloadComponent>? components = null,
        string? attacker = "P1") =>
        new(result ?? Applied(), origin, components ?? new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) }, attacker);

    [Fact]
    public void A_direct_landed_hit_credits_the_resolved_owner_once_per_element()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input());

        var snap = acc.Snapshot();
        Assert.Equal(1L, snap[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, "fire")]);
    }

    [Fact]
    public void A_two_element_packet_credits_both_elements_once_each()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(components: new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.6),
            new ElementPayloadComponent(ElementTypeId.Ice, 0.4),
        }));

        var snap = acc.Snapshot();
        Assert.Equal(1L, snap[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, "fire")]);
        Assert.Equal(1L, snap[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, "ice")]);
        Assert.Equal(2, snap.Count); // weight is read for presence only, never as a multiplier
    }

    [Fact]
    public void A_duplicated_element_within_one_packet_still_credits_once()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(components: new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.5),
            new ElementPayloadComponent(ElementTypeId.Fire, 0.5),
        }));

        Assert.Equal(1L, acc.Snapshot()[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, "fire")]);
    }

    [Fact]
    public void A_status_pulse_origin_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(origin: DamageOrigin.StatusPulse));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void A_sink_refused_outcome_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(result: new DamageApplyResult(DamageApplyOutcome.SinkRefused, 0, 0)));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void A_fully_absorbed_outcome_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(result: new DamageApplyResult(DamageApplyOutcome.FullyAbsorbed, 0, 100)));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void An_applied_outcome_with_zero_amount_earns_nothing()
    {
        // The pipeline's own miss-telemetry parity: Outcome == Applied does not by itself mean a use.
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(result: new DamageApplyResult(DamageApplyOutcome.Applied, 0, 0)));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void An_attacker_less_hit_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(attacker: null));
        counter.Handle(Input(attacker: ""));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void A_resolver_returning_null_earns_no_credit()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc, _ => null);

        counter.Handle(Input());

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void No_components_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(components: Array.Empty<ElementPayloadComponent>()));

        Assert.Empty(acc.Snapshot());
    }

    [Theory]
    [InlineData(ElementTypeId.Fire, "fire")]
    [InlineData(ElementTypeId.Ice, "ice")]
    [InlineData(ElementTypeId.Air, "air")]
    [InlineData(ElementTypeId.Earth, "earth")]
    [InlineData(ElementTypeId.Light, "light")]
    [InlineData(ElementTypeId.Dark, "dark")]
    public void Every_one_of_the_6_concrete_elements_credits_without_throwing(ElementTypeId element, string id)
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input(components: new[] { new ElementPayloadComponent(element, 1.0) }));

        Assert.Equal(1L, acc.Snapshot()[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, id)]);
    }

    [Fact]
    public void Two_direct_hits_credit_the_same_owner_twice()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(Input());
        counter.Handle(Input());

        Assert.Equal(2L, acc.Snapshot()[new GateCounterKey(Player1, ElementMasteryCounter.Quantity, "fire")]);
    }
}
