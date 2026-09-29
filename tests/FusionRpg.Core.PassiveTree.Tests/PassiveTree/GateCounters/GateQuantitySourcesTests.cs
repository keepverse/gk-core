using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.GateCounters;

/// <summary>
/// Task G4 — spec-gate-counters.md §5.2/§5.3, todo.md G4 acceptance bullet 1:
/// <see cref="StatusAppliedSource"/> and <see cref="ElementMasterySource"/> answer in
/// aptitude-point-equivalents, via <see cref="MasteryIndex"/>, at their own rate keys -- proved end to
/// end through a real <see cref="GateQuantityRegistry"/> rather than asserted against the class in
/// isolation.
/// </summary>
public class GateQuantitySourcesTests
{
    static readonly GateCountersTuning Tuning = new(
        MasteryCurveFirstCount: 23, MasteryCurveStepCount: 23,
        ElementMasteryRatePoints: 4, StatusMasteryRatePoints: 7, // deliberately DIFFERENT rates, to prove
        FlushIntervalMs: 5000, RateDivergenceWhy: "test: proves each source reads its own rate key");

    static readonly GateOwnerKey Player1 = new("player", "player:1");

    [Fact]
    public void StatusAppliedSource_answers_the_status_family_through_MasteryIndex()
    {
        var source = new StatusAppliedSource((owner, subjectId) =>
        {
            Assert.Equal(Player1, owner);
            Assert.Equal("wither", subjectId);
            return 828; // CountToReach(9) from the spec's own §3.4 table -- index 9, tier 3 boundary.
        }, Tuning);

        Assert.Equal(StatusAppliedCounter.Quantity, source.Family);

        var actor = new GateActorContext(Player1);
        var equivalents = source.AptitudePointEquivalents(new GateQuantityId("status_applied", "wither"), actor);

        // index(828) == 9 (exact ladder boundary) -> equivalents = (9-1) * statusMasteryRatePoints(7).
        Assert.Equal((9 - 1) * 7, equivalents);
    }

    [Fact]
    public void ElementMasterySource_answers_the_element_family_through_MasteryIndex_at_its_own_rate()
    {
        var source = new ElementMasterySource((owner, subjectId) =>
        {
            Assert.Equal(Player1, owner);
            Assert.Equal("fire", subjectId);
            return 2093; // CountToReach(14) -- index 14, tier 4 boundary.
        }, Tuning);

        Assert.Equal(ElementMasteryCounter.Quantity, source.Family);

        var actor = new GateActorContext(Player1);
        var equivalents = source.AptitudePointEquivalents(new GateQuantityId("element_mastery", "fire"), actor);

        // index(2093) == 14 -> equivalents = (14-1) * elementMasteryRatePoints(4) -- the DIFFERENT rate
        // from StatusAppliedSource's own test above, proving the two sources never share a key.
        Assert.Equal((14 - 1) * 4, equivalents);
    }

    [Fact]
    public void A_family_mismatch_throws_rather_than_answering_the_wrong_quantity()
    {
        var statusSource = new StatusAppliedSource((_, _) => 0, Tuning);
        var elementSource = new ElementMasterySource((_, _) => 0, Tuning);
        var actor = new GateActorContext(Player1);

        Assert.Throws<ArgumentException>(() =>
            statusSource.AptitudePointEquivalents(new GateQuantityId("element_mastery", "fire"), actor));
        Assert.Throws<ArgumentException>(() =>
            elementSource.AptitudePointEquivalents(new GateQuantityId("status_applied", "wither"), actor));
    }

    [Fact]
    public void A_zero_count_answers_zero_equivalents_not_an_error()
    {
        var source = new StatusAppliedSource((_, _) => 0, Tuning);
        var actor = new GateActorContext(Player1);

        Assert.Equal(0L, source.AptitudePointEquivalents(new GateQuantityId("status_applied", "wither"), actor));
    }

    /// <summary>End to end through the real seam: both sources registered into one
    /// <see cref="GateQuantityRegistry"/>, resolved by family, no double-count, no scope machinery.</summary>
    [Fact]
    public void Both_sources_register_and_resolve_through_one_registry()
    {
        var registry = new GateQuantityRegistry();
        registry.Register(new StatusAppliedSource((_, _) => 828, Tuning));   // -> equivalents 56 (rate 7)
        registry.Register(new ElementMasterySource((_, _) => 2093, Tuning)); // -> equivalents 52 (rate 4)

        var actor = new GateActorContext(Player1);
        Assert.Equal(56L, registry.AptitudePointEquivalents(new GateQuantityId("status_applied", "wither"), actor));
        Assert.Equal(52L, registry.AptitudePointEquivalents(new GateQuantityId("element_mastery", "fire"), actor));
    }

    [Fact]
    public void Null_constructor_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusAppliedSource(null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => new StatusAppliedSource((_, _) => 0, null!));
        Assert.Throws<ArgumentNullException>(() => new ElementMasterySource(null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => new ElementMasterySource((_, _) => 0, null!));
    }

    /// <summary>
    /// spec-gate-counters.md §5.2's tier-0 reason split, todo.md G4 acceptance bullet 4 -- two families
    /// that both read tier 0 today for a DIFFERENT reason, distinguishable from the pieces this task and
    /// G3 already shipped: <see cref="GateQuantityRegistry.HasProducer"/> answers the "this quantity has
    /// no producer" half by itself (no source registered at all); a family WITH a source but a fresh
    /// actor's zero count is the "no aptitude allocated yet" half -- ordinary, not a content gap. Neither
    /// is inferred from the zero tier alone; `HasProducer` is the fact that tells them apart, exactly as
    /// the spec requires. (Rendering this on `TreeResolveReport.GateState`/the wire is task
    /// G6's job -- this proves the Core-level distinction the wire will read.)
    /// </summary>
    [Fact]
    public void The_two_tier_zero_reasons_are_distinguishable_via_HasProducer()
    {
        var registry = new GateQuantityRegistry();
        var actor = new GateActorContext(Player1);
        const long reqScalePoints = 5;

        // Half 1: a family with no producer at all -- "this quantity has no producer," a known content
        // gap, never inferred from the zero.
        Assert.False(registry.HasProducer("element_mastery"));
        var unproducedEquivalents = registry.AptitudePointEquivalents(new GateQuantityId("element_mastery", "fire"), actor);
        Assert.Equal(0L, unproducedEquivalents);
        Assert.Equal(0, FusionRpg.Core.PassiveTree.Resolve.TierGate.Reached(unproducedEquivalents, authoredTierCount: 10, reqScalePoints));

        // Half 2: a family WITH a real producer, but a fresh actor (zero lifetime count) -- "no
        // aptitude allocated yet," the ordinary starting state every other gate-quantity family reads.
        registry.Register(new StatusAppliedSource((_, _) => 0, Tuning));
        Assert.True(registry.HasProducer("status_applied"));
        var freshActorEquivalents = registry.AptitudePointEquivalents(new GateQuantityId("status_applied", "wither"), actor);
        Assert.Equal(0L, freshActorEquivalents);
        Assert.Equal(0, FusionRpg.Core.PassiveTree.Resolve.TierGate.Reached(freshActorEquivalents, authoredTierCount: 10, reqScalePoints));

        // Both currently resolve to tier 0 with zero equivalents -- identical on the surface. Only
        // HasProducer tells them apart, which is the whole point of the split.
        Assert.NotEqual(registry.HasProducer("element_mastery"), registry.HasProducer("status_applied"));
    }
}
