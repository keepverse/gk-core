using FusionRpg.Core.Delve.Quests;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.14 (spec-delve-quests.md §Interface) — `QuestDto`: a projection test scanning for
/// engine words (the todo's own literal Verify line).</summary>
public class QuestDtoTests
{
    [Fact]
    public void Project_carries_the_five_named_fields_verbatim()
    {
        var dto = QuestDtoProjection.Project("Clear the Warren", "Something stirs below.", have: 2, need: 5, done: false);
        Assert.Equal("Clear the Warren", dto.Name);
        Assert.Equal("Something stirs below.", dto.Flavor);
        Assert.Equal(2, dto.Have);
        Assert.Equal(5, dto.Need);
        Assert.False(dto.Done);
    }

    [Fact]
    public void No_field_on_QuestDto_names_an_engine_word()
    {
        // spec, verbatim: "no Θ, rung id or PartyIndex" -- BANNED_WORDS' own vocabulary
        // (theta/rung/partyindex/delveid/sectorid) scanned against the DTO's own property names,
        // not just its rendered strings, so a field ADDED later trips this before it ever ships.
        var bannedSubstrings = new[] { "theta", "rung", "partyindex", "delveid", "sectorid", "ordinal" };
        var fieldNames = typeof(QuestDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.Equal(5, fieldNames.Count);
        foreach (var banned in bannedSubstrings)
            Assert.DoesNotContain(fieldNames, n => n.Contains(banned));
    }

    [Fact]
    public void QuestDto_carries_exactly_the_five_fields_the_spec_names_no_more()
    {
        var expected = new[] { "Name", "Flavor", "Have", "Need", "Done" };
        var actual = typeof(QuestDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));
    }
}
