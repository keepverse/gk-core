using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Element;

public class ElementHubHybridTests
{
    readonly IElementHub _hub = ElementHub.Default;
    const double Base = 100;

    [Fact]
    public void Fire_air_hybrid_vs_ice()
    {
        var components = new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.7),
            new ElementPayloadComponent(ElementTypeId.Air, 0.3)
        };
        var defender = ActorElementTypes.Create(ElementTypeId.Ice);
        var bonus = _hub.ResolvePayloadBonus(components, defender, Base);
        Assert.Equal(17.5, bonus, 3);
    }

    [Fact]
    public void Fire_air_hybrid_vs_air()
    {
        var components = new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.5),
            new ElementPayloadComponent(ElementTypeId.Air, 0.5)
        };
        var defender = ActorElementTypes.Create(ElementTypeId.Air);
        var bonus = _hub.ResolvePayloadBonus(components, defender, Base);
        Assert.Equal(-12.5, bonus, 3);
    }

    [Fact]
    public void Single_fire_vs_neutral_defender()
    {
        var components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) };
        var bonus = _hub.ResolvePayloadBonus(components, ActorElementTypes.Neutral, Base);
        Assert.Equal(0, bonus);
    }
}
