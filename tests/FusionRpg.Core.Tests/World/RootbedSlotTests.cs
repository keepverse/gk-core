using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// L1 acceptance (spec-loam-model.md): the rootbed slot type is expressible and self-validating,
/// and — because no template places one yet — its introduction moves no world hash.
/// </summary>
public class RootbedSlotTests
{
    [Fact]
    public void Rootbed_is_the_last_slot_kind_so_a_future_insert_fails_loudly()
    {
        var kinds = Enum.GetValues<SlotKind>();
        Assert.Equal(SlotKind.Rootbed, kinds[^1]);
    }

    [Fact]
    public void Rootbed_catalog_row_is_buildable_and_yields()
    {
        var rootbed = SlotTypeCatalog.Get(SlotTypeCatalog.RootbedSlotTypeId);
        Assert.Equal(SlotKind.Rootbed, rootbed.Kind);
        Assert.True(rootbed.Buildable);
        Assert.True(rootbed.Yields);
    }

    [Fact]
    public void Rootbed_is_allowed_only_where_a_base_could_stand()
    {
        foreach (var sector in SectorTypeCatalog.All)
        {
            var allowsRootbed = sector.AllowedSlotTypes.Contains(SlotTypeCatalog.RootbedSlotTypeId);
            Assert.Equal(sector.CanHostSeat, allowsRootbed);
        }
    }

}
