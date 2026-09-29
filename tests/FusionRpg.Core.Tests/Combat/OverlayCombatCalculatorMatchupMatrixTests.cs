using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>All 16 single-type attacker×defender cells → MatchupBonus + FinalSignedDelta.</summary>
public class OverlayCombatCalculatorMatchupMatrixTests
{
    const long Base = 100;

    static OverlayCombatRequest Request(ElementTypeId attacker, ElementTypeId defender) =>
        new()
        {
            BaseOverlayDamage = Base,
            Components = new[] { new ElementPayloadComponent(attacker, 1.0) },
            Attacker = CombatActorSnapshot.AttackerLess(),
            Defender = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral(),
                ActorElementTypes.Create(defender)),
            ForceHit = true,
            ForceCrit = false
        };

    public static IEnumerable<object[]> AllCells()
    {
        var elements = new[]
        {
            ElementTypeId.Fire,
            ElementTypeId.Ice,
            ElementTypeId.Earth,
            ElementTypeId.Air
        };
        foreach (var atk in elements)
        foreach (var def in elements)
        {
            var relation = ElementRingMatrix.GetRelation(atk, def);
            var bonus = ElementRingMatrix.RelationShare(relation) * Base;
            var finalDamage = Base + bonus;
            yield return new object[] { atk, def, bonus, -(long)Math.Round(finalDamage) };
        }
    }

    [Theory]
    [MemberData(nameof(AllCells))]
    public void Matchup_matrix_cell(
        ElementTypeId attacker,
        ElementTypeId defender,
        double expectedBonus,
        long expectedFinalSignedDelta)
    {
        var calc = new OverlayCombatCalculator();
        var (_, breakdown) = calc.Compute(Request(attacker, defender), new SeededCombatRng(1));
        Assert.True(breakdown.Hit);
        Assert.False(breakdown.Crit);
        Assert.Equal(expectedBonus, breakdown.MatchupBonus, 3);
        Assert.Equal(expectedFinalSignedDelta, breakdown.FinalSignedDelta);
    }
}
