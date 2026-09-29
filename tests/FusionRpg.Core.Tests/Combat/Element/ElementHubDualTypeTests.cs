using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Element;

public class ElementHubDualTypeTests
{
    readonly IElementHub _hub = ElementHub.Default;
    const double Base = 100;

    [Theory]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Ice, ElementTypeId.Earth, 25)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Air, ElementTypeId.Earth, -25)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Ice, ElementTypeId.Air, -6.25)]
    [InlineData(ElementTypeId.Ice, ElementTypeId.Fire, ElementTypeId.Air, -25)]
    public void Dual_type_product_rule(
        ElementTypeId attacker,
        ElementTypeId primary,
        ElementTypeId secondary,
        double expectedBonus)
    {
        var types = ActorElementTypes.Create(primary, secondary);
        var bonus = _hub.ResolveComponentBonus(attacker, types, Base);
        Assert.Equal(expectedBonus, bonus, 3);
    }
}
