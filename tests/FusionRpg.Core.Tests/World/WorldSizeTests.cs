using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// L16 acceptance (spec-loam-maps.md): the five-tier size ladder, and an unavailable tier refuses
/// at creation with a reason naming the tier rather than building a map nobody can afford to compute.
/// </summary>
public class WorldSizeTests
{
    [Fact]
    public void All_five_tiers_exist_ids_plain_names_content()
    {
        Assert.Equal(5, WorldSizeCatalog.All.Count);
        Assert.True(WorldSizeCatalog.IsKnown(WorldSizeCatalog.SmallId));
        Assert.True(WorldSizeCatalog.IsKnown(WorldSizeCatalog.MediumId));
        Assert.True(WorldSizeCatalog.IsKnown(WorldSizeCatalog.LargeId));
        Assert.True(WorldSizeCatalog.IsKnown(WorldSizeCatalog.HugeId));
        Assert.True(WorldSizeCatalog.IsKnown(WorldSizeCatalog.GiantId));
    }

    [Fact]
    public void Only_small_and_medium_are_available()
    {
        Assert.True(WorldSizeCatalog.Get(WorldSizeCatalog.SmallId).Available);
        Assert.True(WorldSizeCatalog.Get(WorldSizeCatalog.MediumId).Available);
        Assert.False(WorldSizeCatalog.Get(WorldSizeCatalog.LargeId).Available);
        Assert.False(WorldSizeCatalog.Get(WorldSizeCatalog.HugeId).Available);
        Assert.False(WorldSizeCatalog.Get(WorldSizeCatalog.GiantId).Available);
    }

    [Theory]
    [InlineData(WorldSizeCatalog.LargeId)]
    [InlineData(WorldSizeCatalog.HugeId)]
    [InlineData(WorldSizeCatalog.GiantId)]
    public void An_unavailable_tier_refuses_with_a_reason_naming_the_tier(string sizeId)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorldSizeCatalog.RequireAvailable(sizeId));
        Assert.Contains(sizeId, ex.Message);
    }

    [Theory]
    [InlineData(WorldSizeCatalog.SmallId)]
    [InlineData(WorldSizeCatalog.MediumId)]
    public void An_available_tier_does_not_refuse(string sizeId) =>
        WorldSizeCatalog.RequireAvailable(sizeId); // throws on failure

    [Fact]
    public void Medium_is_a_range_not_a_number()
    {
        var medium = WorldSizeCatalog.Get(WorldSizeCatalog.MediumId);
        Assert.True(medium.MaxNodes > medium.MinNodes, "medium must be a range, not a single number");
        Assert.Equal(14, medium.MinNodes);
        Assert.Equal(18, medium.MaxNodes);
    }

    [Fact]
    public void Unknown_size_lookups_name_the_id()
    {
        var ex = Assert.Throws<ArgumentException>(() => WorldSizeCatalog.Get("nope-size"));
        Assert.Contains("nope-size", ex.Message);
    }

    [Fact]
    public void First_light_is_declared_small_and_two_hearths_medium()
    {
        Assert.Equal(WorldSizeCatalog.SmallId, WorldTemplateCatalog.SizeIdOf(WorldTemplateCatalog.FirstLightId));
        Assert.Equal(WorldSizeCatalog.MediumId, WorldTemplateCatalog.SizeIdOf(WorldTemplateCatalog.TwoHeartsId));
    }

    [Fact]
    public void Every_shipped_templates_sector_count_matches_its_declared_tier()
    {
        foreach (var templateId in WorldTemplateCatalog.All)
        {
            var world = WorldTemplateCatalog.Build(templateId, seed: 1);
            var size = WorldSizeCatalog.Get(WorldTemplateCatalog.SizeIdOf(templateId));
            Assert.InRange(world.Sectors.Count, size.MinNodes, size.MaxNodes);
        }
    }
}
