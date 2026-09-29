using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatCalculatorTests
{
    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();

    static OverlayCombatRequest MatchupRequest(
        ElementTypeId attackerElement,
        ActorElementTypes defenderTypes,
        long baseDamage = 100)
    {
        return new OverlayCombatRequest
        {
            BaseOverlayDamage = baseDamage,
            Components = new[] { new ElementPayloadComponent(attackerElement, 1.0) },
            Attacker = new CombatActorSnapshot(
                NeutralCombat.Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -500)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(NeutralCombat, defenderTypes)
        };
    }

    [Theory]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Ice, 125)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Air, 75)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Earth, 100)]
    public void Matchup_only_end_to_end(
        ElementTypeId attacker,
        ElementTypeId defenderType,
        long expectedFinalDamage)
    {
        var calc = new OverlayCombatCalculator();
        var request = MatchupRequest(attacker, ActorElementTypes.Create(defenderType));
        var (delta, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.True(breakdown.Hit);
        Assert.Equal(-expectedFinalDamage, delta);
        Assert.Equal(expectedFinalDamage - 100, breakdown.MatchupBonus, 3);
    }

    [Fact]
    public void Hybrid_payload_weighted_matchup()
    {
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[]
            {
                new ElementPayloadComponent(ElementTypeId.Fire, 0.7),
                new ElementPayloadComponent(ElementTypeId.Air, 0.3)
            },
            Attacker = new CombatActorSnapshot(
                NeutralCombat.Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -500)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(NeutralCombat, ActorElementTypes.Create(ElementTypeId.Ice))
        };
        var (_, breakdown) = calc.Compute(request, new SeededCombatRng(1));
        Assert.Equal(17.5, breakdown.MatchupBonus, 3);
        Assert.Equal(-118, breakdown.FinalSignedDelta);
    }
}
