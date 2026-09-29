using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatParseComponentsTests
{
    [Fact]
    public void Rejects_unknown_element_id()
    {
        var dtos = new List<ElementPayloadComponentDto>
        {
            new() { Element = "shadow", Weight = 1.0 }
        };
        Assert.Throws<ArgumentException>(() => OverlayCombatCalculator.ParseComponents(dtos));
    }

    [Fact]
    public void Rejects_missing_element_id()
    {
        var dtos = new List<ElementPayloadComponentDto>
        {
            new() { Element = "", Weight = 1.0 }
        };
        Assert.Throws<ArgumentException>(() => OverlayCombatCalculator.ParseComponents(dtos));
    }

    [Fact]
    public void Compute_rejects_invalid_weight_sum()
    {
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[]
            {
                new ElementPayloadComponent(ElementTypeId.Fire, 0.6),
                new ElementPayloadComponent(ElementTypeId.Air, 0.3)
            },
            Attacker = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral)
        };
        Assert.Throws<ArgumentException>(() => calc.Compute(request, new SeededCombatRng(1)));
    }
}
