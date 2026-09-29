using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

public class ArmouryQueryTests
{
    static ContainerRow Container(int prefixRolls, int suffixRolls) => new()
    {
        ContainerId = "item.test", Kind = ContainerKind.Item,
        PrefixRolls = prefixRolls, SuffixRolls = suffixRolls,
    };

    [Fact]
    public void Storage_grade_is_derived_from_the_container()
    {
        Assert.Equal(StorageGrade.Stock, StorageGrading.GradeOf(Container(0, 0)));
        Assert.Equal(StorageGrade.Rolled, StorageGrading.GradeOf(Container(1, 0)));
        Assert.Equal(StorageGrade.Rolled, StorageGrading.GradeOf(Container(0, 1)));
    }

    static ArmouryEntry Entry(string id, string role = "armament-primary", int rarity = 10,
        bool assigned = false, bool locked = false, bool unseen = false, bool stale = false,
        string acquired = "2026-01-01T00:00:00Z", int rollQuality = 500) =>
        new(id, "item.test", role, "plant", rarity, acquired, assigned, locked, unseen, stale, rollQuality);

    [Fact]
    public void Filter_by_role_and_rarity_range_composes()
    {
        var entries = new[]
        {
            Entry("a", role: "armament-primary", rarity: 10),
            Entry("b", role: "armament-primary", rarity: 100),
            Entry("c", role: "core-guard", rarity: 50),
        };

        var filtered = ArmouryQuery.ApplyFilter(entries,
            new ArmouryFilter(Role: "armament-primary", RarityMin: 20)).ToList();

        Assert.Single(filtered);
        Assert.Equal("b", filtered[0].InstanceId);
    }

    [Fact]
    public void Rarity_sort_orders_by_ordinal_never_the_label()
    {
        // "almanac" would sort before "chaff" alphabetically -- ordinal must win.
        var entries = new[] { Entry("chaff", rarity: 10), Entry("almanac", rarity: 100) };

        var sorted = ArmouryQuery.ApplySort(entries, ArmourySortKey.RarityOrdinal).ToList();

        Assert.Equal(new[] { "almanac", "chaff" }, sorted.Select(e => e.InstanceId));
    }

    [Fact]
    public void The_page_contract_is_keyset_and_never_offset()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry("id" + i)).ToList();

        var page1 = ArmouryQuery.ApplyPage(entries, new ArmouryPageRequest(Limit: 2));
        Assert.Equal(new[] { "id0", "id1" }, page1.Items.Select(e => e.InstanceId));
        Assert.Equal("id1", page1.NextAfterKey);

        var page2 = ArmouryQuery.ApplyPage(entries, new ArmouryPageRequest(Limit: 2, AfterKey: page1.NextAfterKey));
        Assert.Equal(new[] { "id2", "id3" }, page2.Items.Select(e => e.InstanceId));

        var page3 = ArmouryQuery.ApplyPage(entries, new ArmouryPageRequest(Limit: 2, AfterKey: page2.NextAfterKey));
        Assert.Equal(new[] { "id4" }, page3.Items.Select(e => e.InstanceId));
        // Last page: no further key. Nothing in this method ever skips by count, so an item
        // inserted between two page calls cannot be silently repeated or dropped by an offset drift.
        Assert.Null(page3.NextAfterKey);
    }

    [Fact]
    public void Page_limit_is_clamped_never_unbounded()
    {
        var entries = Enumerable.Range(0, 500).Select(i => Entry("id" + i)).ToList();

        var page = ArmouryQuery.ApplyPage(entries, new ArmouryPageRequest(Limit: 10_000));

        Assert.True(page.Items.Count <= 200);
    }
}
