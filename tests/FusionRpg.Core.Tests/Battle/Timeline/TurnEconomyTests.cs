using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>B10 / T3b — the turn economy as a pluggable strategy.</summary>
public class TurnEconomyTests
{
    [Fact]
    public void OneActionPerTurn_grants_exactly_one_action_then_refuses_until_reset()
    {
        ITurnEconomy economy = new OneActionPerTurnEconomy();
        Assert.Equal(TurnEconomyScope.PerActor, economy.Scope);

        Assert.True(economy.TryAcquire("z1", cost: 1, nowTick: 0));
        Assert.False(economy.TryAcquire("z1", cost: 1, nowTick: 1));

        economy.ResetForNewTurn("z1", nowTick: 100);
        Assert.True(economy.TryAcquire("z1", cost: 1, nowTick: 100));
    }

    [Fact]
    public void OneActionPerTurn_tracks_actors_independently()
    {
        ITurnEconomy economy = new OneActionPerTurnEconomy();
        Assert.True(economy.TryAcquire("a", 1, 0));
        Assert.True(economy.TryAcquire("b", 1, 0)); // b is unaffected by a's spend
        Assert.False(economy.TryAcquire("a", 1, 0));
    }

    [Fact]
    public void ActionPoints_spends_down_and_refuses_when_insufficient()
    {
        ITurnEconomy economy = new ActionPointsEconomy(maxPoints: 3);
        Assert.Equal(TurnEconomyScope.PerActor, economy.Scope);

        Assert.True(economy.TryAcquire("z1", cost: 2, nowTick: 0));  // 3 -> 1
        Assert.False(economy.TryAcquire("z1", cost: 2, nowTick: 0)); // only 1 left, needs 2
        Assert.True(economy.TryAcquire("z1", cost: 1, nowTick: 0));  // 1 -> 0

        economy.ResetForNewTurn("z1", nowTick: 10);
        Assert.True(economy.TryAcquire("z1", cost: 3, nowTick: 10)); // refilled to max
    }

    [Fact]
    public void ActionPoints_rejects_a_non_positive_max()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActionPointsEconomy(maxPoints: 0));
    }

    [Fact]
    public void PressTurn_writes_cleanly_against_the_interface_weakness_refunds_and_miss_costs_double()
    {
        // "It is the implementation that would have broken the original interface, so it is the
        // proof the interface is right" — this test is that proof: side-scoped budget, resolution
        // outcome mutating it, both expressed through ITurnEconomy with no side-channel.
        ITurnEconomy economy = new PressTurnEconomy(startingIcons: 3);
        Assert.Equal(TurnEconomyScope.PerSide, economy.Scope);

        const string side = "side:left";
        Assert.True(economy.TryAcquire(side, cost: 1, nowTick: 0)); // 3 -> 2

        economy.OnActionResolved(side, ActionResolutionOutcome.HitWeakness); // refund: 2 -> 3
        Assert.True(economy.TryAcquire(side, cost: 1, nowTick: 1));           // 3 -> 2
        Assert.True(economy.TryAcquire(side, cost: 1, nowTick: 2));           // 2 -> 1
        Assert.True(economy.TryAcquire(side, cost: 1, nowTick: 3));           // 1 -> 0
        Assert.False(economy.TryAcquire(side, cost: 1, nowTick: 4));          // exhausted

        economy.ResetForNewTurn(side, nowTick: 100);                          // -> 3
        Assert.True(economy.TryAcquire(side, cost: 1, nowTick: 100));         // 3 -> 2
        economy.OnActionResolved(side, ActionResolutionOutcome.Missed);       // costs double: 2 -> 1
        Assert.True(economy.TryAcquire(side, cost: 1, nowTick: 101));         // 1 -> 0
        Assert.False(economy.TryAcquire(side, cost: 1, nowTick: 102));        // a miss really did cost two icons total
    }

    [Fact]
    public void PressTurn_never_goes_negative_on_a_miss_at_zero_icons()
    {
        ITurnEconomy economy = new PressTurnEconomy(startingIcons: 1);
        const string side = "side:left";
        Assert.True(economy.TryAcquire(side, 1, 0)); // 1 -> 0
        economy.OnActionResolved(side, ActionResolutionOutcome.Missed); // would go to -1 without the floor
        Assert.False(economy.TryAcquire(side, 1, 1)); // still 0, not negative
    }

    [Fact]
    public void PressTurn_rejects_a_non_positive_starting_pool()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressTurnEconomy(startingIcons: 0));
    }

    [Fact]
    public void Readiness_never_references_a_turn_economy_type()
    {
        // Architecture-level assertion of the purity boundary stated in TurnEconomy.cs's own doc
        // comment: readiness must never read a budget. Checked structurally — every public and
        // private member of ReadinessDriver and TurnReadiness is scanned for any ITurnEconomy-typed
        // parameter, field, or return value.
        var readinessTypes = new[] { typeof(ReadinessDriver), typeof(TurnReadiness) };
        foreach (var type in readinessTypes)
        {
            foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                Assert.False(typeof(ITurnEconomy).IsAssignableFrom(method.ReturnType), $"{type.Name}.{method.Name} returns an ITurnEconomy");
                foreach (var p in method.GetParameters())
                    Assert.False(typeof(ITurnEconomy).IsAssignableFrom(p.ParameterType), $"{type.Name}.{method.Name}({p.Name}) takes an ITurnEconomy");
            }
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                Assert.False(typeof(ITurnEconomy).IsAssignableFrom(field.FieldType), $"{type.Name}.{field.Name} is an ITurnEconomy");
        }
    }
}
