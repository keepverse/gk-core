using FusionRpg.Core.Commanders;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

/// <summary>aura-skill T9c: each commander's own resource pool, distinct from any battle actor's.
/// Wraps the already-shipped `ActorResourcePools` (spec-action-costs.md's six pools) rather than a
/// second implementation — this type only owns WHICH instance belongs to which `CommanderRef`, kept
/// alive for the session instead of re-created per match.</summary>
public class CommanderResourcePoolsTests
{
    static ActorDerivedSnapshot BaselineDerived() => ActorDerivedSnapshot.FromValues(new[]
    {
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("hp"), 100),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("stamina"), 50),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("hunger"), 100),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("spirit"), 20),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("qi"), 30),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("poise"), 10),
    });

    [Fact]
    public void Each_commander_gets_its_own_pool_starting_at_max()
    {
        var pools = new CommanderResourcePools();
        var dave = pools.GetOrCreate(ShippedCommanders.Dave, BaselineDerived(), atTick: 0);

        Assert.Equal(100, dave.Resolve("hp", 0, BaselineDerived()));
        Assert.Equal(50, dave.Resolve("stamina", 0, BaselineDerived()));
        Assert.Equal(10, dave.Resolve("poise", 0, BaselineDerived()));
    }

    [Fact]
    public void An_empty_default_pool_never_throws_when_read()
    {
        var pools = new CommanderResourcePools();
        var zomboss = pools.GetOrCreate(ShippedCommanders.Zomboss, ActorDerivedSnapshot.Empty, atTick: 0);

        var exception = Record.Exception(() =>
        {
            foreach (var id in DerivedStatChannels.ResourceIds)
                zomboss.Resolve(id, 0, ActorDerivedSnapshot.Empty);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Pool_state_survives_across_calls_within_the_session_not_recreated_per_match()
    {
        var pools = new CommanderResourcePools();
        var derived = BaselineDerived();
        var dave = pools.GetOrCreate(ShippedCommanders.Dave, derived, atTick: 0);

        // Match 1: spend 30 stamina (simulating an aura's upkeep cost across a battle).
        Assert.True(dave.TrySpend("stamina", 30, nowTick: 100, derived));

        // "Match 2 starts": the caller asks for Dave's pool again, same session.
        var daveAgain = pools.GetOrCreate(ShippedCommanders.Dave, derived, atTick: 200);

        Assert.Same(dave, daveAgain); // not a fresh instance
        Assert.Equal(20, daveAgain.Resolve("stamina", 200, derived)); // 50 - 30 spent, still gone
    }

    [Fact]
    public void Dave_and_Zombosss_pools_are_fully_independent()
    {
        var pools = new CommanderResourcePools();
        var derived = BaselineDerived();
        var dave = pools.GetOrCreate(ShippedCommanders.Dave, derived, atTick: 0);
        var zomboss = pools.GetOrCreate(ShippedCommanders.Zomboss, derived, atTick: 0);

        Assert.True(dave.TrySpend("hp", 50, nowTick: 0, derived));

        Assert.Equal(50, dave.Resolve("hp", 0, derived));
        Assert.Equal(100, zomboss.Resolve("hp", 0, derived)); // untouched by Dave's spend
    }

    [Fact]
    public void TryGet_reports_false_for_a_commander_never_created()
    {
        var pools = new CommanderResourcePools();
        Assert.False(pools.TryGet(ShippedCommanders.Zomboss, out _));
    }
}
