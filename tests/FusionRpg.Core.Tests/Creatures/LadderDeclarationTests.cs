using FusionRpg.Core.Creatures;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// tier-propagation-contract T1/T-3 (spec-tier-propagation-contract.md Testing strategy, Success
/// criteria 1–2): the ladder's count is derived and the top-rung throw is correct at any width.
/// The rung set is a CLOSED VOCABULARY the code owns — pinning its shape here is correct
/// (validation-ssot.md §1). Nothing here asserts a corpus size.
/// </summary>
public class LadderDeclarationTests
{
    [Fact]
    public void RungCount_IsDerivedFromTheEnumItself()
    {
        // Identity, not a literal: widening CreatureRarity moves both sides together.
        // (The literal width appears only in the theory below, as one case among several.)
        Assert.Equal(CreatureRarityLadder.All.Count, CreatureRarityLadder.RungCount);
        Assert.Equal(
            Enum.GetValues<CreatureRarity>().Length,
            CreatureRarityLadder.RungCount);
    }

    [Fact]
    public void OneRungAbove_ThrowsOnlyAtTheGenuineTopRung()
    {
        var top = CreatureRarityLadder.All[^1];
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreatureRarityLadder.OneRungAbove(top));
        Assert.Equal("ordinal", ex.ParamName);

        // Every other rung advances exactly one ordinal — expressed via All, never a literal width.
        foreach (var rung in CreatureRarityLadder.All.SkipLast(1))
            Assert.Equal((CreatureRarity)((int)rung + 1), CreatureRarityLadder.OneRungAbove(rung));
    }

    [Fact]
    public void RungsBetween_walks_the_window_weakest_first_and_empties_when_inverted()
    {
        // The only sanctioned ordinal walk outside All itself (wave-species-roll windows) —
        // expressed via All, never a literal width.
        var window = CreatureRarityLadder.RungsBetween(
            CreatureRarityLadder.All[2], CreatureRarityLadder.All[4]);
        Assert.Equal(new[]
        {
            CreatureRarityLadder.All[2], CreatureRarityLadder.All[3], CreatureRarityLadder.All[4],
        }, window);
        Assert.Empty(CreatureRarityLadder.RungsBetween(
            CreatureRarityLadder.All[4], CreatureRarityLadder.All[2]));
        Assert.Equal(new[] { CreatureRarityLadder.All[^1] }, CreatureRarityLadder.RungsBetween(
            CreatureRarityLadder.All[^1], CreatureRarityLadder.All[^1]));
    }
    /// <summary>
    /// The throw boundary proven at widths a test double supplies — including wider than the
    /// shipped enum. C# cannot widen <see cref="CreatureRarity"/> at runtime, so the boundary
    /// core (<c>NextOrdinal</c>, which <c>OneRungAbove</c> delegates to) is exercised over
    /// synthetic <c>(ordinal, rungCount)</c> pairs: narrower, current, and widened.
    /// </summary>
    [Theory]
    [InlineData(3, 2, -1)]   // narrow ladder: top ordinal throws
    [InlineData(3, 1, 2)]    // narrow ladder: below top advances
    [InlineData(10, 9, -1)]  // shipped width: top ordinal throws (-1 = expect throw)
    [InlineData(10, 8, 9)]   // shipped width: second-to-last advances
    [InlineData(12, 11, -1)] // widened past shipped: new top throws
    [InlineData(12, 9, 10)]  // widened past shipped: old top ordinal now advances
    [InlineData(13, 12, -1)] // widened further: top throws
    [InlineData(13, 0, 1)]   // widened further: bottom advances
    public void NextOrdinal_ThrowBoundaryHoldsAtAnyWidth(int rungCount, int ordinal, int expected)
    {
        if (expected < 0)
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => CreatureRarityLadder.NextOrdinal(ordinal, rungCount));
            Assert.Equal("ordinal", ex.ParamName);
        }
        else
        {
            Assert.Equal(expected, CreatureRarityLadder.NextOrdinal(ordinal, rungCount));
        }
    }
}
