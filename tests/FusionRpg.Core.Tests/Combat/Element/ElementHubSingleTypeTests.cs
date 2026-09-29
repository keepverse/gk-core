using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Element;

public class ElementHubSingleTypeTests
{
    readonly IElementHub _hub = ElementHub.Default;
    const double Base = 100;

    [Theory]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Ice, 25)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Air, -25)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Earth, 0)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Fire, 0)]
    [InlineData(ElementTypeId.Ice, ElementTypeId.Earth, 25)]
    [InlineData(ElementTypeId.Air, ElementTypeId.Fire, 25)]
    [InlineData(ElementTypeId.Earth, ElementTypeId.Air, 25)]
    public void Single_type_defender_bonus(
        ElementTypeId attacker,
        ElementTypeId defender,
        double expectedBonus)
    {
        var types = ActorElementTypes.Create(defender);
        var bonus = _hub.ResolveComponentBonus(attacker, types, Base);
        Assert.Equal(expectedBonus, bonus, 3);
    }

    [Fact]
    public void Neutral_defender_returns_zero()
    {
        var bonus = _hub.ResolveComponentBonus(ElementTypeId.Fire, ActorElementTypes.Neutral, Base);
        Assert.Equal(0, bonus);
    }
}
