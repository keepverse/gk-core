using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.6 (spec-event-deck.md §5): the out-of-fight `resource.delta` executor. Fixture shape
/// mirrors `RestResolverTests.cs`'s own `Snapshot` helper (`ActorDerivedSnapshot.FromValues` over
/// `DerivedStatChannels.ResourceIds`, zero regen for predictable arithmetic).</summary>
public class DelveResourceDeltaTests
{
    static ActorDerivedSnapshot Snapshot(long max = 1000, long regenPerTick = 0) =>
        ActorDerivedSnapshot.FromValues(DerivedStatChannels.ResourceIds.SelectMany(id => new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax(id), max),
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceRegen(id), regenPerTick),
        }));

    static IReadOnlyDictionary<string, long> PoolsAt(long value) =>
        DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => value);

    // ---- argument validation ----

    [Fact]
    public void Apply_null_arguments_throw()
    {
        var pools = PoolsAt(500);
        var deltas = new[] { new DelveResourceDelta.ResourceDelta("hp", 10) };
        Assert.Throws<ArgumentNullException>(() => DelveResourceDelta.Apply(null!, deltas, Snapshot(), 0));
        Assert.Throws<ArgumentNullException>(() => DelveResourceDelta.Apply(pools, null!, Snapshot(), 0));
        Assert.Throws<ArgumentNullException>(() => DelveResourceDelta.Apply(pools, deltas, null!, 0));
    }

    [Fact]
    public void Apply_an_unknown_channel_throws()
    {
        var pools = PoolsAt(500);
        var deltas = new[] { new DelveResourceDelta.ResourceDelta("mana", 10) };
        Assert.Throws<ArgumentException>(() => DelveResourceDelta.Apply(pools, deltas, Snapshot(), 0));
    }

    // ---- one delta per resource -- the verify line's own headline ----

    [Theory]
    [InlineData("hp")]
    [InlineData("stamina")]
    [InlineData("hunger")]
    [InlineData("spirit")]
    [InlineData("qi")]
    [InlineData("poise")]
    public void A_positive_delta_heals_the_named_resource_and_leaves_every_other_pool_untouched(string channel)
    {
        var pools = PoolsAt(500);
        var deltas = new[] { new DelveResourceDelta.ResourceDelta(channel, 200) };
        var result = DelveResourceDelta.Apply(pools, deltas, Snapshot(max: 1000), atTick: 0);

        Assert.Equal(700, result[channel]);
        foreach (var id in DerivedStatChannels.ResourceIds.Where(id => id != channel))
            Assert.Equal(500, result[id]);
    }

    // ---- clamping through ActorResourcePools.Add's own [0, max] rail ----

    [Fact]
    public void A_negative_delta_drains_and_clamps_at_zero_never_goes_negative()
    {
        var pools = PoolsAt(50);
        var deltas = new[] { new DelveResourceDelta.ResourceDelta("spirit", -500) };
        var result = DelveResourceDelta.Apply(pools, deltas, Snapshot(max: 1000), atTick: 0);
        Assert.Equal(0, result["spirit"]);
    }

    [Fact]
    public void A_positive_delta_past_max_clamps_at_max_never_overshoots()
    {
        var pools = PoolsAt(900);
        var deltas = new[] { new DelveResourceDelta.ResourceDelta("stamina", 500) };
        var result = DelveResourceDelta.Apply(pools, deltas, Snapshot(max: 1000), atTick: 0);
        Assert.Equal(1000, result["stamina"]);
    }

    [Fact]
    public void A_zero_amount_delta_is_a_no_op()
    {
        var pools = PoolsAt(333);
        var deltas = new[] { new DelveResourceDelta.ResourceDelta("qi", 0) };
        var result = DelveResourceDelta.Apply(pools, deltas, Snapshot(max: 1000), atTick: 0);
        Assert.Equal(333, result["qi"]);
    }

    // ---- multiple deltas ----

    [Fact]
    public void Multiple_deltas_to_different_resources_all_apply_independently()
    {
        var pools = PoolsAt(500);
        var deltas = new[]
        {
            new DelveResourceDelta.ResourceDelta("hp", 100),
            new DelveResourceDelta.ResourceDelta("hunger", -300),
            new DelveResourceDelta.ResourceDelta("poise", 50),
        };
        var result = DelveResourceDelta.Apply(pools, deltas, Snapshot(max: 1000), atTick: 0);

        Assert.Equal(600, result["hp"]);
        Assert.Equal(200, result["hunger"]);
        Assert.Equal(550, result["poise"]);
        Assert.Equal(500, result["stamina"]); // untouched
        Assert.Equal(500, result["spirit"]);
        Assert.Equal(500, result["qi"]);
    }

    [Fact]
    public void Multiple_deltas_to_the_same_resource_accumulate_in_order()
    {
        var pools = PoolsAt(500);
        var deltas = new[]
        {
            new DelveResourceDelta.ResourceDelta("hp", 200),
            new DelveResourceDelta.ResourceDelta("hp", -50),
        };
        var result = DelveResourceDelta.Apply(pools, deltas, Snapshot(max: 1000), atTick: 0);
        Assert.Equal(650, result["hp"]);
    }

    [Fact]
    public void An_empty_delta_list_returns_the_pools_unchanged()
    {
        var pools = PoolsAt(500);
        var result = DelveResourceDelta.Apply(pools, Array.Empty<DelveResourceDelta.ResourceDelta>(), Snapshot(max: 1000), atTick: 0);
        foreach (var id in DerivedStatChannels.ResourceIds)
            Assert.Equal(500, result[id]);
    }
}
