using FusionRpg.Core.Creatures;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// Task 2 (spec-species-rank.md §3): the rank ladder's declaration and its named helpers, checked
/// row-for-row against the rarity ladder it mirrors. The rung set is a CLOSED VOCABULARY the code
/// owns — pinning its SHAPE is correct (validation-ssot.md §1); no corpus size is asserted here.
/// </summary>
public class CreatureRankTests
{
    [Fact]
    public void The_rank_ladder_mirrors_the_rarity_ladder_row_for_row()
    {
        // Same width, same order, same ids — the owner-confirmed 1:1 mirroring (spec Assumption 1),
        // while staying two separate enums (each axis owns its closed vocabulary).
        Assert.Equal(CreatureRarityLadder.All.Select(r => r.ToId()), CreatureRankLadder.All.Select(r => r.ToId()));
        Assert.Equal(CreatureRarityLadder.RungCount, CreatureRankLadder.RungCount);
    }

    [Fact]
    public void RungCount_IsDerivedFromTheEnumItself()
    {
        // Identity, not a literal: widening CreatureRank moves both sides together.
        Assert.Equal(CreatureRankLadder.All.Count, CreatureRankLadder.RungCount);
        Assert.Equal(Enum.GetValues<CreatureRank>().Length, CreatureRankLadder.RungCount);
    }

    [Fact]
    public void AtLeast_and_AtMost_agree_with_the_rarity_ladder_over_every_pair()
    {
        // Row-for-row parity over the whole ladder: for every (rank, threshold) pair the rank helper
        // and the rarity helper answer identically at the same rung index. A literal width appears
        // nowhere — both ladders' own All supplies the pairs.
        var ranks = CreatureRankLadder.All;
        var rarities = CreatureRarityLadder.All;
        for (var i = 0; i < ranks.Count; i++)
        {
            for (var j = 0; j < ranks.Count; j++)
            {
                Assert.Equal(
                    CreatureRarityLadder.AtLeast(rarities[i], rarities[j]),
                    CreatureRankLadder.AtLeast(ranks[i], ranks[j]));
                Assert.Equal(
                    CreatureRarityLadder.AtMost(rarities[i], rarities[j]),
                    CreatureRankLadder.AtMost(ranks[i], ranks[j]));
            }
        }
    }

    [Fact]
    public void OneRungAbove_ThrowsOnlyAtTheGenuineTopRung()
    {
        var top = CreatureRankLadder.All[^1];
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreatureRankLadder.OneRungAbove(top));
        Assert.Equal("ordinal", ex.ParamName);

        // Every other rung advances exactly one ordinal — expressed via All, never a literal width.
        for (var i = 0; i < CreatureRankLadder.All.Count - 1; i++)
            Assert.Equal(CreatureRankLadder.All[i + 1], CreatureRankLadder.OneRungAbove(CreatureRankLadder.All[i]));
    }

    [Fact]
    public void RungsBelow_clamps_at_the_bottom_rung()
    {
        // Clamped, never thrown (the rarity ladder's own contract): a low-rung floor search resolves
        // to the bottom rung rather than failing.
        Assert.Equal(CreatureRankLadder.All[0], CreatureRankLadder.RungsBelow(CreatureRankLadder.All[0], 1));
        Assert.Equal(CreatureRankLadder.All[0], CreatureRankLadder.RungsBelow(CreatureRankLadder.All[3], 9));

        for (var i = 1; i < CreatureRankLadder.All.Count; i++)
            Assert.Equal(CreatureRankLadder.All[i - 1], CreatureRankLadder.RungsBelow(CreatureRankLadder.All[i], 1));
    }

    /// <summary>
    /// The throw boundary proven at widths a test double supplies — including wider than the shipped
    /// enum. C# cannot widen <see cref="CreatureRank"/> at runtime, so the boundary core
    /// (<c>NextOrdinal</c>, which <c>OneRungAbove</c> delegates to) is exercised over synthetic
    /// <c>(ordinal, rungCount)</c> pairs, exactly as the rarity ladder's own declaration test does.
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
                () => CreatureRankLadder.NextOrdinal(ordinal, rungCount));
            Assert.Equal("ordinal", ex.ParamName);
        }
        else
        {
            Assert.Equal(expected, CreatureRankLadder.NextOrdinal(ordinal, rungCount));
        }
    }

    [Fact]
    public void Rank_ids_round_trip_and_refuse_anything_outside_the_vocabulary()
    {
        foreach (var rank in CreatureRankLadder.All)
        {
            Assert.True(CreatureRankIds.TryParse(rank.ToId(), out var parsed));
            Assert.Equal(rank, parsed);
        }

        // The pipeline's unresolved sentinel and an unknown id both refuse — never a default
        // (spec Assumption 4: skip, don't fabricate).
        Assert.False(CreatureRankIds.TryParse("unresolved", out _));
        Assert.False(CreatureRankIds.TryParse("legendary", out _));
        Assert.False(CreatureRankIds.TryParse(null, out _));
    }
}
