using FusionRpg.Core.PassiveTree.GateCounters;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree;

/// <summary>
/// Task G3 -- spec-gate-counters.md §12 and §11 tests 10, 11: the composition-root exclusivity
/// contract <see cref="GateQuantityRegistry"/> and <see cref="IGateQuantitySource"/> exist to enforce.
/// Tests 13-14 (the tuning-loader divergence coupling) are ALREADY covered end to end at the loader
/// level by task G2's <c>GateCounterBoundaryGuardTests</c> and <c>PassiveTreeTuningTests</c> --
/// duplicating them here would be a second copy of a rule that already has one enforcement point.
/// </summary>
public class GateQuantityRegistryTests
{
    sealed class StubSource : IGateQuantitySource
    {
        public string Family { get; }
        readonly long _equivalents;
        public StubSource(string family, long equivalents = 0) { Family = family; _equivalents = equivalents; }
        public long AptitudePointEquivalents(GateQuantityId id, GateActorContext actor) => _equivalents;
    }

    /// <summary>§11 test 10 -- the silent double-count §12 forbids by name. The message names both
    /// producers so the collision is diagnosable from the exception alone, not just "it threw".</summary>
    [Fact]
    public void A_second_producer_for_one_family_throws_naming_both()
    {
        var registry = new GateQuantityRegistry();
        var first = new StubSource("element_mastery");
        var second = new StubSource("element_mastery");

        registry.Register(first);
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(second));

        Assert.Contains("element_mastery", ex.Message, StringComparison.Ordinal);
        Assert.Contains(first.GetType().FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(second.GetType().FullName!, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_registering_the_exact_same_instance_is_a_no_op_not_a_collision()
    {
        var registry = new GateQuantityRegistry();
        var source = new StubSource("status_applied");

        registry.Register(source);
        registry.Register(source); // same instance -- not a second producer, must not throw

        Assert.True(registry.HasProducer("status_applied"));
    }

    /// <summary>Two different families never collide with each other -- exclusivity is per-family,
    /// not global.</summary>
    [Fact]
    public void Two_different_families_register_independently()
    {
        var registry = new GateQuantityRegistry();
        registry.Register(new StubSource("status_applied"));
        registry.Register(new StubSource("element_mastery"));

        Assert.True(registry.HasProducer("status_applied"));
        Assert.True(registry.HasProducer("element_mastery"));
    }

    /// <summary>§11 test 11 -- the registry is a pure pass-through to whichever producer owns the
    /// family; it never invents, scales, or reinterprets the answer.</summary>
    [Fact]
    public void Every_registered_family_answers_in_whatever_its_producer_returns()
    {
        var registry = new GateQuantityRegistry();
        registry.Register(new StubSource("element_mastery", equivalents: 12));

        var actor = new GateActorContext(new GateOwnerKey("player", "player:1"));
        Assert.Equal(12L, registry.AptitudePointEquivalents(new GateQuantityId("element_mastery", "fire"), actor));
    }

    /// <summary>§5.2's tier-0 reason split: a family with NO producer answers 0 -- a known content gap,
    /// never an error and never inferred as "player has not started" (that distinction belongs to
    /// `tree-resolve`, task G4, which is the one place both halves of the reason are told apart).</summary>
    [Fact]
    public void A_family_with_no_producer_answers_zero_not_an_error()
    {
        var registry = new GateQuantityRegistry();
        var actor = new GateActorContext(new GateOwnerKey("player", "player:1"));

        Assert.False(registry.HasProducer("element_mastery"));
        Assert.Equal(0L, registry.AptitudePointEquivalents(new GateQuantityId("element_mastery", "fire"), actor));
    }

    [Fact]
    public void Registering_a_source_with_a_blank_family_throws()
    {
        var registry = new GateQuantityRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new StubSource("")));
    }
}
