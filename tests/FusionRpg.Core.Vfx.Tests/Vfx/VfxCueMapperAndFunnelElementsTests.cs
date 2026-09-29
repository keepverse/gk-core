using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Phase-1 adapter mapping and element payload flow through the Funnel present mailbox.</summary>
public class VfxCueMapperAndFunnelElementsTests
{
    static List<ElementPayloadComponentDto> Fire() =>
        new() { new ElementPayloadComponentDto { Element = "fire", Weight = 1.0 } };

    [Fact]
    public void Heal_tag_maps_to_heal_cue_everything_else_to_hit()
    {
        var heal = VfxCueMapper.FromDamageFx(new DamageFxDto { TargetPtr = "0xA", Amount = 25, Tag = DamageFxTag.Heal });
        Assert.Equal(VfxCueIds.CombatHeal, heal.CueId);
        foreach (var tag in new[] { DamageFxTag.Neutral, DamageFxTag.Crit, DamageFxTag.Dodge, DamageFxTag.Block })
        {
            var cue = VfxCueMapper.FromDamageFx(new DamageFxDto { TargetPtr = "0xA", Amount = -5, Tag = tag });
            Assert.Equal(VfxCueIds.CombatHit, cue.CueId);
            Assert.Equal(tag, cue.Tag);
        }
    }

    [Fact]
    public void Mapper_copies_ptr_amount_and_elements()
    {
        var cue = VfxCueMapper.FromDamageFx(new DamageFxDto
        {
            TargetPtr = "0xBEEF",
            Amount = -120,
            Tag = DamageFxTag.Crit,
            Elements = Fire()
        });
        Assert.Equal("0xBEEF", cue.TargetPtr);
        Assert.Equal(-120, cue.Amount);
        Assert.NotNull(cue.Elements);
        Assert.Equal("fire", cue.Elements![0].Element);
    }

    [Fact]
    public void Present_carries_elements_through_flush()
    {
        var h = new FoundationHarness();
        h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "0xAAA",
            Amount = -50,
            Tag = DamageFxTag.Crit,
            Elements = Fire()
        });
        h.Funnel.Flush();
        var shown = Assert.Single(h.Fx.Items);
        Assert.NotNull(shown.Elements);
        Assert.Equal("fire", shown.Elements![0].Element);
    }

    [Fact]
    public void Present_merge_keeps_latest_elements()
    {
        var h = new FoundationHarness();
        h.Funnel.EnqueuePresent(new DamageFxDto { TargetPtr = "0xAAA", Amount = -10, Tag = DamageFxTag.Crit });
        h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "0xAAA",
            Amount = -20,
            Tag = DamageFxTag.Crit,
            Elements = Fire()
        });
        h.Funnel.Flush();
        var shown = Assert.Single(h.Fx.Items);
        Assert.Equal(-30, shown.Amount);
        Assert.NotNull(shown.Elements);
    }

    [Fact]
    public void Mutation_with_elements_produces_element_tagged_default_present()
    {
        var h = new FoundationHarness();
        h.Funnel.EnqueueMutation("entity:0xAAA", -100, pluginId: "combat", elements: Fire());
        h.Funnel.Flush();
        var shown = Assert.Single(h.Fx.Items);
        Assert.Equal(DamageFxTag.Neutral, shown.Tag);
        Assert.NotNull(shown.Elements);
        Assert.Equal("fire", shown.Elements![0].Element);
    }

    [Fact]
    public void Mutation_without_elements_keeps_present_neutral_and_elementless()
    {
        var h = new FoundationHarness();
        h.Funnel.EnqueueMutation("entity:0xBBB", -100, pluginId: "combat");
        h.Funnel.Flush();
        var shown = Assert.Single(h.Fx.Items);
        Assert.Null(shown.Elements);
    }
}
