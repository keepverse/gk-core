using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Events;

public class SupplyOverrideTagSeedFileTests
{
    static string SuppliesDir() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "supplies");

    [Fact]
    public void LoadAllOverrideTags_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => SupplyOverrideTagSeedFile.LoadAllOverrideTags(null!));
    }

    [Fact]
    public void LoadAllOverrideTags_a_missing_directory_returns_empty_never_throws()
    {
        Assert.Empty(SupplyOverrideTagSeedFile.LoadAllOverrideTags(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist")));
    }

    /// <summary>Real, measured 2026-09-07: all 31 real shipped supply-extension anchors carry an empty
    /// `overrideTags` array — none has ever authored one yet. A real, honest finding, not a test bug —
    /// this test becomes load-bearing the day a real override tag ships.</summary>
    [Fact]
    public void LoadAllOverrideTags_over_the_real_31_shipped_supplies_is_currently_empty()
    {
        var tags = SupplyOverrideTagSeedFile.LoadAllOverrideTags(SuppliesDir());
        Assert.Empty(tags);
    }

    // ---- LoadOverrideTagsByConsumableRef (2026-09-08, D3.3/D3.5's own remaining gap) ----------------

    [Fact]
    public void LoadOverrideTagsByConsumableRef_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => SupplyOverrideTagSeedFile.LoadOverrideTagsByConsumableRef(null!));
    }

    [Fact]
    public void LoadOverrideTagsByConsumableRef_a_missing_directory_returns_empty_never_throws()
    {
        Assert.Empty(SupplyOverrideTagSeedFile.LoadOverrideTagsByConsumableRef(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist")));
    }

    /// <summary>Real, cross-checked against the real corpus: exactly 31 entries (one per real shipped
    /// supply-extension anchor), each keyed by its OWN `consumableRef` (proven equal to the file's own
    /// name minus `.json`, confirmed by reading a real file directly — `consumable.k1-001.json`'s own
    /// `consumableRef` field is the literal string `"consumable.k1-001"`), every value an empty tag set
    /// (matching `LoadAllOverrideTags`'s own already-established finding above).</summary>
    [Fact]
    public void LoadOverrideTagsByConsumableRef_over_the_real_shipped_supplies_keys_every_ref_to_an_empty_set()
    {
        var byRef = SupplyOverrideTagSeedFile.LoadOverrideTagsByConsumableRef(SuppliesDir());
        // The supply count is a growing content population, never pinned (population-pin SE3.3,
        // 2026-09-19); the real contract is the membership checks below.
        Assert.NotEmpty(byRef);
        Assert.Contains("consumable.k1-001", byRef.Keys);
        Assert.All(byRef.Values, tags => Assert.Empty(tags));
    }

    // ---- HoldsOverrideStockBridge.HoldsOverrideStock (2026-09-08) ------------------------------------

    static PackCell Cell(int row, int col, string refId) =>
        new(row, col, new PackItem("Consumable", refId, InstanceId: null, Qty: 1, W: 1, H: 1, GrantIndex: 0, PackItemOrigin.CarryIn));

    static IReadOnlyDictionary<string, IReadOnlySet<string>> TagsByRef(params (string refId, string[] tags)[] entries)
    {
        var d = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (refId, tags) in entries) d[refId] = new HashSet<string>(tags, StringComparer.Ordinal);
        return d;
    }

    [Fact]
    public void HoldsOverrideStock_null_arguments_throw()
    {
        var tagsByRef = TagsByRef();
        Assert.Throws<ArgumentNullException>(() => HoldsOverrideStockBridge.HoldsOverrideStock(null!, "herbs", tagsByRef));
        Assert.Throws<ArgumentException>(() => HoldsOverrideStockBridge.HoldsOverrideStock(Array.Empty<PackCell>(), "", tagsByRef));
        Assert.Throws<ArgumentException>(() => HoldsOverrideStockBridge.HoldsOverrideStock(Array.Empty<PackCell>(), null!, tagsByRef));
        Assert.Throws<ArgumentNullException>(() => HoldsOverrideStockBridge.HoldsOverrideStock(Array.Empty<PackCell>(), "herbs", null!));
    }

    [Fact]
    public void HoldsOverrideStock_red_an_empty_pack_never_holds_any_tag()
    {
        Assert.False(HoldsOverrideStockBridge.HoldsOverrideStock(Array.Empty<PackCell>(), "herbs", TagsByRef()));
    }

    [Fact]
    public void HoldsOverrideStock_red_a_pack_item_whose_own_consumableRef_carries_no_matching_tag()
    {
        var cells = new[] { Cell(0, 0, "consumable.bandage-001") };
        var tagsByRef = TagsByRef(("consumable.bandage-001", new[] { "watch" }));
        Assert.False(HoldsOverrideStockBridge.HoldsOverrideStock(cells, "herbs", tagsByRef));
    }

    [Fact]
    public void HoldsOverrideStock_red_a_pack_item_with_no_entry_at_all_in_the_tag_map()
    {
        var cells = new[] { Cell(0, 0, "consumable.unregistered-999") };
        Assert.False(HoldsOverrideStockBridge.HoldsOverrideStock(cells, "herbs", TagsByRef()));
    }

    /// <summary>The verify line's own headline — a real green case, holding the tag-bearing supply.</summary>
    [Fact]
    public void HoldsOverrideStock_green_a_pack_item_whose_consumableRef_carries_the_exact_tag()
    {
        var cells = new[] { Cell(0, 0, "consumable.herbs-bundle-001") };
        var tagsByRef = TagsByRef(("consumable.herbs-bundle-001", new[] { "herbs" }));
        Assert.True(HoldsOverrideStockBridge.HoldsOverrideStock(cells, "herbs", tagsByRef));
    }

    [Fact]
    public void HoldsOverrideStock_green_finds_the_tag_among_several_unrelated_pack_cells()
    {
        var cells = new[]
        {
            Cell(0, 0, "consumable.bandage-001"),
            Cell(0, 1, "consumable.unregistered-999"),
            Cell(1, 0, "consumable.herbs-bundle-001"), // the one that matters
        };
        var tagsByRef = TagsByRef(
            ("consumable.bandage-001", new[] { "watch" }),
            ("consumable.herbs-bundle-001", new[] { "herbs", "bait" }));
        Assert.True(HoldsOverrideStockBridge.HoldsOverrideStock(cells, "herbs", tagsByRef));
    }

    [Fact]
    public void HoldsOverrideStock_a_supply_carrying_OTHER_tags_but_not_this_one_still_reads_false()
    {
        var cells = new[] { Cell(0, 0, "consumable.multi-tag-001") };
        var tagsByRef = TagsByRef(("consumable.multi-tag-001", new[] { "key", "watch", "bait" }));
        Assert.False(HoldsOverrideStockBridge.HoldsOverrideStock(cells, "herbs", tagsByRef));
    }
}
