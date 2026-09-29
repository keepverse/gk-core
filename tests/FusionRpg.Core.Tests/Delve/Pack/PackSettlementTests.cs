using FusionRpg.Core.Delve.Pack;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>D3.22 (spec-loot-pack.md §7) — `PackSettlement.Decide`/`RoundRobinParty`.</summary>
public class PackSettlementTests
{
    static PackItem HaulGear(string refId) =>
        new("Equipment", refId, "inst-" + refId, 1, 1, 1, 0, PackItemOrigin.Haul);
    static PackItem HaulStack(string refId, long qty) =>
        new("Material", refId, null, qty, 1, 1, 0, PackItemOrigin.Haul);
    static PackItem CarryInGear(string refId) =>
        new("Equipment", refId, "inst-" + refId, 1, 1, 1, 0, PackItemOrigin.CarryIn);
    static PackItem CarryInStack(string refId, long qty) =>
        new("Material", refId, null, qty, 1, 1, 0, PackItemOrigin.CarryIn);

    [Fact]
    public void Decide_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => PackSettlement.Decide(null!, true));
    }

    [Fact]
    public void Extracted_unlocks_haul_gear_with_no_stock_write()
    {
        var writes = PackSettlement.Decide(new[] { HaulGear("relic") }, extracted: true);
        var write = Assert.Single(writes);
        Assert.Equal(PackSettlementAction.UnlockHaulInstance, write.Action);
        Assert.Equal("relic", write.RefId);
    }

    [Fact]
    public void Extracted_banks_a_haul_stack_by_its_own_qty()
    {
        var writes = PackSettlement.Decide(new[] { HaulStack("shard", 12) }, extracted: true);
        var write = Assert.Single(writes);
        Assert.Equal(PackSettlementAction.BankStack, write.Action);
        Assert.Equal(12, write.Qty);
        Assert.Null(write.InstanceId);
    }

    [Fact]
    public void Extracted_unlocks_carry_in_gear_never_a_bank_write()
    {
        var writes = PackSettlement.Decide(new[] { CarryInGear("sword") }, extracted: true);
        var write = Assert.Single(writes);
        Assert.Equal(PackSettlementAction.UnlockCarryInInstance, write.Action);
    }

    [Fact]
    public void Extracted_banks_unconsumed_carry_in_stock_the_same_way_as_haul()
    {
        var writes = PackSettlement.Decide(new[] { CarryInStack("ration", 3) }, extracted: true);
        var write = Assert.Single(writes);
        Assert.Equal(PackSettlementAction.BankStack, write.Action);
        Assert.Equal(3, write.Qty);
    }

    [Fact]
    public void Wiped_destroys_every_haul_item_gear_and_stack_alike()
    {
        var writes = PackSettlement.Decide(new[] { HaulGear("relic"), HaulStack("shard", 5) }, extracted: false);
        Assert.All(writes, w => Assert.Equal(PackSettlementAction.DestroyHaulInstance, w.Action));
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public void Wiped_still_returns_carry_in_gear_and_stock_home()
    {
        var writes = PackSettlement.Decide(new[] { CarryInGear("sword"), CarryInStack("ration", 3) }, extracted: false);
        Assert.Contains(writes, w => w.Action == PackSettlementAction.UnlockCarryInInstance && w.RefId == "sword");
        Assert.Contains(writes, w => w.Action == PackSettlementAction.BankStack && w.RefId == "ration" && w.Qty == 3);
    }

    [Fact]
    public void An_empty_pack_settles_to_no_writes()
    {
        Assert.Empty(PackSettlement.Decide(Array.Empty<PackItem>(), true));
        Assert.Empty(PackSettlement.Decide(Array.Empty<PackItem>(), false));
    }

    // ---- RoundRobinParty ----

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 4, 1)]
    [InlineData(4, 4, 0)]
    [InlineData(5, 4, 1)]
    [InlineData(7, 2, 1)]
    public void RoundRobinParty_is_grantIndex_mod_partyCount(int grantIndex, int partyCount, int expected)
    {
        Assert.Equal(expected, PackSettlement.RoundRobinParty(grantIndex, partyCount));
    }

    [Fact]
    public void Four_party_round_robin_golden_over_eight_grants()
    {
        var assignments = Enumerable.Range(0, 8).Select(i => PackSettlement.RoundRobinParty(i, 4)).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3, 0, 1, 2, 3 }, assignments);
    }

    [Fact]
    public void RoundRobinParty_rejects_non_positive_party_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PackSettlement.RoundRobinParty(0, 0));
    }

    [Fact]
    public void RoundRobinParty_rejects_a_negative_grant_index()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PackSettlement.RoundRobinParty(-1, 4));
    }
}
