using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using Xunit;

namespace FusionRpg.Core.Tests.World.Turn;

/// <summary>
/// combat-ai `siege-loadout-wiring` B (CAI3.3, spec-siege-loadout-wiring.md §2): the composite resolver a
/// district assault composes so real authored containers resolve alongside the four fixed construction
/// ones. Its contract is ORDER and the empty answer — not either source's contents.
/// </summary>
public class SiegeLoadoutWiringTests
{
    /// <summary>A resolver that answers for the ids it was given and counts how often it was asked, so
    /// the short-circuit and the order are proven rather than assumed.</summary>
    sealed class StubResolver : IContainerEffectResolver
    {
        readonly Dictionary<string, IReadOnlyList<string>> _answers;
        public int Calls;

        public StubResolver(params (string Container, string[] Ids)[] answers)
        {
            _answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (container, ids) in answers) _answers[container] = ids;
        }

        public IReadOnlyList<string> EffectIdsFor(string containerId)
        {
            Calls++;
            return _answers.TryGetValue(containerId, out var ids) ? ids : Array.Empty<string>();
        }
    }

    [Fact]
    public void The_first_non_empty_answer_wins_and_later_resolvers_are_never_asked()
    {
        var store = new StubResolver(("container.authored", new[] { "atom.a", "atom.b" }));
        var construction = new StubResolver(("container.authored", new[] { "atom.construction" }));

        var composite = new CompositeContainerEffectResolver(store, construction);

        Assert.Equal(new[] { "atom.a", "atom.b" }, composite.EffectIdsFor("container.authored"));
        Assert.Equal(1, store.Calls);
        Assert.Equal(0, construction.Calls); // short-circuit: a second source cannot contradict the first
    }

    /// <summary>The reason the composite exists: the two sources know DIFFERENT containers, so an empty
    /// answer from one must fall through to the next — the construction ids are four fixed entries
    /// (`ConstructionActions.cs:70-77`) the store bundle knows nothing about.</summary>
    [Fact]
    public void An_empty_answer_falls_through_to_the_next_resolver()
    {
        var store = new StubResolver();                                   // authored containers only
        var construction = new StubResolver(("container.rampart", new[] { "atom.place" }));

        var composite = new CompositeContainerEffectResolver(store, construction);

        Assert.Equal(new[] { "atom.place" }, composite.EffectIdsFor("container.rampart"));
        Assert.Equal(1, store.Calls);
        Assert.Equal(1, construction.Calls);
    }

    /// <summary>Empty, never null: `BindContainers` turns empty into its own loud rejection, and null
    /// here would move that failure to a null reference downstream.</summary>
    [Fact]
    public void Nothing_answering_returns_empty_rather_than_null()
    {
        var composite = new CompositeContainerEffectResolver(new StubResolver(), new StubResolver());

        var ids = composite.EffectIdsFor("container.unknown");

        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    /// <summary>An empty composite is legal and answers empty for everything — the honest "nothing can
    /// resolve here" state, which the bind loop rejects loudly.</summary>
    [Fact]
    public void An_empty_composite_answers_empty()
    {
        var composite = new CompositeContainerEffectResolver();

        Assert.Empty(composite.EffectIdsFor("container.anything"));
    }

    /// <summary>Determinism is the constructor's order, so swapping the sources swaps the winner — the
    /// property that makes "siege composes [store, construction]" a statement about behaviour.</summary>
    [Fact]
    public void The_order_is_the_constructor_order()
    {
        var first = new StubResolver(("c", new[] { "from.first" }));
        var second = new StubResolver(("c", new[] { "from.second" }));

        Assert.Equal(new[] { "from.first" }, new CompositeContainerEffectResolver(first, second).EffectIdsFor("c"));
        Assert.Equal(new[] { "from.second" }, new CompositeContainerEffectResolver(second, first).EffectIdsFor("c"));
    }

    /// <summary>The spec's own words: "allocation-free per call (no LINQ, no closure)". Measured in bytes
    /// on the current thread with the warm-then-collect harness this repo settled on, never in
    /// milliseconds.</summary>
    [Fact]
    public void A_warm_composite_allocates_zero_bytes_per_call()
    {
        var store = new StubResolver(("c", new[] { "atom.a" }));
        var construction = new StubResolver();
        var composite = new CompositeContainerEffectResolver(store, construction);

        for (var pass = 0; pass < 3; pass++) composite.EffectIdsFor("c");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        composite.EffectIdsFor("c");
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(bytes == 0, $"a warm composite allocated {bytes} bytes per call; budget is 0");
    }

    [Fact]
    public void A_null_inner_array_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeContainerEffectResolver(null!));
    }
}
