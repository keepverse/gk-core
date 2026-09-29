using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class DamageFxPaletteTests
{
    [Fact]
    public void Palette_maps_every_tag_to_a_color()
    {
        foreach (DamageFxTag tag in Enum.GetValues(typeof(DamageFxTag)))
        {
            var rgb = DamageFxPalette.Rgb(tag);
            Assert.True(rgb.R > 0 || rgb.G > 0 || rgb.B > 0, tag.ToString());
            Assert.False(string.IsNullOrWhiteSpace(DamageFxPalette.Label(tag, 12)));
        }
    }

    [Fact]
    public void Palette_maps_every_tag_to_plan_rgb()
    {
        Assert.Equal((255, 255, 255), DamageFxPalette.Rgb(DamageFxTag.Neutral));
        Assert.Equal((80, 220, 80), DamageFxPalette.Rgb(DamageFxTag.Heal));
        Assert.Equal((255, 220, 40), DamageFxPalette.Rgb(DamageFxTag.Weak));
        Assert.Equal((160, 160, 160), DamageFxPalette.Rgb(DamageFxTag.Resist));
        Assert.Equal((80, 140, 255), DamageFxPalette.Rgb(DamageFxTag.Null));
        Assert.Equal((40, 220, 220), DamageFxPalette.Rgb(DamageFxTag.Absorb));
        Assert.Equal((180, 80, 220), DamageFxPalette.Rgb(DamageFxTag.Reflect));
        Assert.Equal((255, 255, 255), DamageFxPalette.Rgb(DamageFxTag.Dodge));
        Assert.Equal((255, 140, 40), DamageFxPalette.Rgb(DamageFxTag.Crit));
        Assert.Equal((255, 200, 60), DamageFxPalette.Rgb(DamageFxTag.Penetrate));
        Assert.Equal((140, 140, 140), DamageFxPalette.Rgb(DamageFxTag.Block));
    }

    [Fact]
    public void Dodge_and_block_use_words_not_numbers()
    {
        Assert.Equal("MISS", DamageFxPalette.Label(DamageFxTag.Dodge, 0));
        Assert.Equal("BLOCK", DamageFxPalette.Label(DamageFxTag.Block, 0));
        Assert.Equal("NULL", DamageFxPalette.Label(DamageFxTag.Null, 0));
        Assert.Equal("+40", DamageFxPalette.Label(DamageFxTag.Heal, 40));
        Assert.Equal("ABSORB", DamageFxPalette.Label(DamageFxTag.Absorb, 0));
        Assert.Equal("+12", DamageFxPalette.Label(DamageFxTag.Absorb, 12));
        Assert.Equal("12", DamageFxPalette.Label(DamageFxTag.Neutral, 12));
        Assert.Equal("12", DamageFxPalette.Label(DamageFxTag.Crit, -12));
    }

    [Fact]
    public void Floater_rules_expire_cap_and_gui_math()
    {
        Assert.Equal(64, DamageFxFloaterRules.Cap);
        Assert.Equal(0.9f, DamageFxFloaterRules.LifeSeconds);
        Assert.False(DamageFxFloaterRules.AtCap(63));
        Assert.True(DamageFxFloaterRules.AtCap(64));
        Assert.False(DamageFxFloaterRules.Expired(0.89f));
        Assert.True(DamageFxFloaterRules.Expired(0.9f));
        Assert.Equal(0f, DamageFxFloaterRules.T(0f));
        Assert.Equal(1f, DamageFxFloaterRules.T(0.9f));
        Assert.Equal(1f, DamageFxFloaterRules.T(2f));
        Assert.Equal(720f, DamageFxFloaterRules.GuiY(800f, 80f, 0f));
        Assert.Equal(720f - 56f, DamageFxFloaterRules.GuiY(800f, 80f, 1f));
        Assert.Equal(720f, DamageFxFloaterRules.GuiY(800f, 80f, 0f, 0f));
        Assert.Equal(640f, DamageFxFloaterRules.GuiY(800f, 80f, 80f, 0f));
        Assert.Equal(640f - 56f, DamageFxFloaterRules.GuiY(800f, 80f, 80f, 1f));
        Assert.Equal(1f, DamageFxFloaterRules.Alpha(0f));
        Assert.Equal(0f, DamageFxFloaterRules.Alpha(1f));
    }
}
