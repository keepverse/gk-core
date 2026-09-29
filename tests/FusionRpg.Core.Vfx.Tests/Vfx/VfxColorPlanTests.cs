using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Locks the vfx-ssot.md §16.4 color precedence table.</summary>
public class VfxColorPlanTests
{
    static List<ElementPayloadComponentDto> Fire() =>
        new() { new ElementPayloadComponentDto { Element = "fire", Weight = 1.0 } };

    static List<ElementPayloadComponentDto> FireIce() => new()
    {
        new ElementPayloadComponentDto { Element = "fire", Weight = 0.7 },
        new ElementPayloadComponentDto { Element = "ice", Weight = 0.3 }
    };

    [Fact]
    public void Semantic_tags_keep_tag_palette_even_with_elements()
    {
        foreach (var tag in new[]
                 {
                     DamageFxTag.Dodge, DamageFxTag.Block, DamageFxTag.Null, DamageFxTag.Absorb,
                     DamageFxTag.Reflect, DamageFxTag.Heal, DamageFxTag.Weak, DamageFxTag.Resist
                 })
        {
            var plan = VfxColorPlan.For(tag, Fire(), elementFxOn: true, amount: -10);
            Assert.Equal(DamageFxPalette.Rgb(tag), plan.Rgb);
            Assert.False(plan.Hybrid);
            Assert.Equal(1f, plan.FontScale);
        }
    }

    [Fact]
    public void Plain_damage_with_element_takes_element_color()
    {
        var plan = VfxColorPlan.For(DamageFxTag.Neutral, Fire(), elementFxOn: true, amount: -120);
        Assert.Equal(ElementFxPalette.Rgb("fire"), plan.Rgb);
        Assert.Equal("120", plan.Label);
        Assert.Equal(1f, plan.FontScale);
    }

    [Fact]
    public void Crit_with_element_keeps_element_color_at_bigger_font()
    {
        var plan = VfxColorPlan.For(DamageFxTag.Crit, Fire(), elementFxOn: true, amount: -300);
        Assert.Equal(ElementFxPalette.Rgb("fire"), plan.Rgb);
        Assert.Equal(VfxRules.CritFontScale, plan.FontScale);
    }

    [Fact]
    public void No_payload_keeps_current_palette()
    {
        var plain = VfxColorPlan.For(DamageFxTag.Neutral, null, elementFxOn: true, amount: -10);
        Assert.Equal(DamageFxPalette.Rgb(DamageFxTag.Neutral), plain.Rgb);
        var crit = VfxColorPlan.For(DamageFxTag.Crit, null, elementFxOn: true, amount: -10);
        Assert.Equal(DamageFxPalette.Rgb(DamageFxTag.Crit), crit.Rgb);
        Assert.Equal(1f, crit.FontScale);
    }

    [Fact]
    public void Element_toggle_off_renders_exactly_as_today()
    {
        var plan = VfxColorPlan.For(DamageFxTag.Neutral, FireIce(), elementFxOn: false, amount: -50);
        Assert.Equal(DamageFxPalette.Rgb(DamageFxTag.Neutral), plan.Rgb);
        Assert.False(plan.Hybrid);
    }

    [Fact]
    public void Hybrid_payload_marks_hybrid_and_cycles()
    {
        var plan = VfxColorPlan.For(DamageFxTag.Neutral, FireIce(), elementFxOn: true, amount: -50);
        Assert.True(plan.Hybrid);
        Assert.Equal(2, plan.HybridComponents.Count);
        Assert.Equal(ElementFxPalette.Rgb("fire"), plan.ColorAt(0f));
        Assert.NotEqual(plan.ColorAt(0f), plan.ColorAt(0.25f));
    }

    [Fact]
    public void Burst_color_maps_white_to_legacy_orange_only()
    {
        var white = VfxColorPlan.For(DamageFxTag.Neutral, null, elementFxOn: true, amount: -10);
        Assert.Equal(VfxSeedCatalog.ProbeOrange, white.BurstRgb);
        var fire = VfxColorPlan.For(DamageFxTag.Neutral, Fire(), elementFxOn: true, amount: -10);
        Assert.Equal(ElementFxPalette.Rgb("fire"), fire.BurstRgb);
    }

    [Fact]
    public void Element_colored_flag_gates_element_only_specs()
    {
        Assert.True(VfxColorPlan.For(DamageFxTag.Neutral, Fire(), true, -10).ElementColored);
        Assert.True(VfxColorPlan.For(DamageFxTag.Crit, FireIce(), true, -10).ElementColored);
        // plain, toggle-off, and semantic paths are never element-colored
        Assert.False(VfxColorPlan.For(DamageFxTag.Neutral, null, true, -10).ElementColored);
        Assert.False(VfxColorPlan.For(DamageFxTag.Neutral, Fire(), false, -10).ElementColored);
        Assert.False(VfxColorPlan.For(DamageFxTag.Heal, Fire(), true, 25).ElementColored);
    }

    [Fact]
    public void Labels_come_from_the_tag_palette()
    {
        Assert.Equal("MISS", VfxColorPlan.For(DamageFxTag.Dodge, null, true, 0).Label);
        Assert.Equal("+25", VfxColorPlan.For(DamageFxTag.Heal, null, true, 25).Label);
        Assert.Equal("40", VfxColorPlan.For(null, null, true, -40).Label);
    }
}
