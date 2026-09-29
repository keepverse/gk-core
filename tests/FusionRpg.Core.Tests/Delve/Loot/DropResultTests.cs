using FusionRpg.Core.Delve.Loot;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>D3.11/D3.14 (spec-dungeon-loot.md §7, `:203`) — `DropResult`'s own record shape, the piece
/// buildable before `RoomLootInput` (D3.11's own still-blocked type) lands and its `.From`/`.Key`
/// factories can be built.</summary>
public class DropResultTests
{
    [Fact]
    public void An_item_result_carries_every_field_verbatim()
    {
        var r = new DropResult(DropResultKind.Item, RefId: "helm-a", InstanceId: "inst-1", Count: 3, Row: 2, Col: 5, GrantIndex: 4);
        Assert.Equal(DropResultKind.Item, r.Kind);
        Assert.Equal("helm-a", r.RefId);
        Assert.Equal("inst-1", r.InstanceId);
        Assert.Equal(3, r.Count);
        Assert.Equal(2, r.Row);
        Assert.Equal(5, r.Col);
        Assert.Equal(4, r.GrantIndex);
    }

    [Fact]
    public void A_key_result_carries_no_instance_id()
    {
        // Spec, verbatim: "no roll, fires on clear" -- a key is never rolled or minted, so it never
        // carries an InstanceId the way a real item grant does.
        var r = new DropResult(DropResultKind.Key, RefId: "lane-a", InstanceId: null, Count: 1, Row: 0, Col: 0, GrantIndex: -1);
        Assert.Equal(DropResultKind.Key, r.Kind);
        Assert.Null(r.InstanceId);
    }

    [Fact]
    public void Two_results_with_identical_fields_are_equal_value_semantics()
    {
        var a = new DropResult(DropResultKind.Item, "helm-a", "inst-1", 1, 0, 0, 0);
        var b = new DropResult(DropResultKind.Item, "helm-a", "inst-1", 1, 0, 0, 0);
        Assert.Equal(a, b);
    }
}
