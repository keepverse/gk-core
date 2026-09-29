using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Element;

public class ElementRingMatrixTests
{
    [Theory]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Fire, ElementMatchupRelation.Same)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Ice, ElementMatchupRelation.Strong)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Air, ElementMatchupRelation.Weak)]
    [InlineData(ElementTypeId.Fire, ElementTypeId.Earth, ElementMatchupRelation.Neutral)]
    [InlineData(ElementTypeId.Ice, ElementTypeId.Earth, ElementMatchupRelation.Strong)]
    [InlineData(ElementTypeId.Ice, ElementTypeId.Fire, ElementMatchupRelation.Weak)]
    [InlineData(ElementTypeId.Ice, ElementTypeId.Air, ElementMatchupRelation.Neutral)]
    [InlineData(ElementTypeId.Air, ElementTypeId.Fire, ElementMatchupRelation.Strong)]
    [InlineData(ElementTypeId.Air, ElementTypeId.Earth, ElementMatchupRelation.Weak)]
    [InlineData(ElementTypeId.Air, ElementTypeId.Ice, ElementMatchupRelation.Neutral)]
    [InlineData(ElementTypeId.Earth, ElementTypeId.Air, ElementMatchupRelation.Strong)]
    [InlineData(ElementTypeId.Earth, ElementTypeId.Ice, ElementMatchupRelation.Weak)]
    [InlineData(ElementTypeId.Earth, ElementTypeId.Fire, ElementMatchupRelation.Neutral)]
    [InlineData(ElementTypeId.Ice, ElementTypeId.Ice, ElementMatchupRelation.Same)]
    [InlineData(ElementTypeId.Air, ElementTypeId.Air, ElementMatchupRelation.Same)]
    [InlineData(ElementTypeId.Earth, ElementTypeId.Earth, ElementMatchupRelation.Same)]
    public void Ring_cycle_matrix_matches_spec(
        ElementTypeId attacker,
        ElementTypeId defender,
        ElementMatchupRelation expected) =>
        Assert.Equal(expected, ElementRingMatrix.GetRelation(attacker, defender));
}
