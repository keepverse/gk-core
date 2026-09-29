using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudShieldStacksTests
{
    [Fact]
    public void Build_shield_stacks_two_elements()
    {
        var stacks = ActorHudShieldStacks.AggregateByElement(new List<ShieldInstance>
        {
            new() { Element = ElementTypeId.Fire, Hp = 30, MaxHp = 40 },
            new() { Element = ElementTypeId.Ice, Hp = 20, MaxHp = 30 },
        });

        Assert.Equal(2, stacks.Count);
    }

    [Fact]
    public void AggregateByElement_two_elements_two_stacks()
    {
        var shields = new List<ShieldInstance>
        {
            new()
            {
                Element = ElementTypeId.Fire,
                Hp = 50,
                MaxHp = 80,
            },
            new()
            {
                Element = ElementTypeId.Ice,
                Hp = 20,
                MaxHp = 30,
            },
        };

        var stacks = ActorHudShieldStacks.AggregateByElement(shields);

        Assert.Equal(2, stacks.Count);
        Assert.Equal("fire", stacks[0].Element);
        Assert.Equal(50, stacks[0].Hp);
        Assert.Equal("ice", stacks[1].Element);
    }

    [Fact]
    public void AggregateByElement_sums_same_element()
    {
        var shields = new List<ShieldInstance>
        {
            new() { Element = ElementTypeId.Fire, Hp = 30, MaxHp = 40 },
            new() { Element = ElementTypeId.Fire, Hp = 20, MaxHp = 40 },
        };

        var stacks = ActorHudShieldStacks.AggregateByElement(shields);

        Assert.Single(stacks);
        Assert.Equal(50, stacks[0].Hp);
        Assert.Equal(80, stacks[0].Max);
    }

    [Fact]
    public void AggregateByElement_prunes_zero_hp_and_max()
    {
        var shields = new List<ShieldInstance>
        {
            new() { Element = ElementTypeId.Fire, Hp = 0, MaxHp = 0 },
        };

        Assert.Empty(ActorHudShieldStacks.AggregateByElement(shields));
    }

    [Fact]
    public void Totals_sums_all_instances()
    {
        var shields = new List<ShieldInstance>
        {
            new() { Hp = 10, MaxHp = 20 },
            new() { Hp = 5, MaxHp = 15 },
        };

        var (hp, max) = ActorHudShieldStacks.Totals(shields);

        Assert.Equal(15, hp);
        Assert.Equal(35, max);
    }
}
