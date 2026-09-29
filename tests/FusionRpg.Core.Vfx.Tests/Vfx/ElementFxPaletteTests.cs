using FusionRpg.Contracts;
using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Locks the vfx-ssot.md §16.2 palette and §16.3 hybrid math.</summary>
public class ElementFxPaletteTests
{
    static ElementPayloadComponentDto C(string element, double weight = 1.0) =>
        new() { Element = element, Weight = weight };

    [Fact]
    public void Palette_matches_ssot_16_2()
    {
        Assert.Equal(((byte)255, (byte)90, (byte)40), ElementFxPalette.Rgb("fire"));
        Assert.Equal(((byte)110, (byte)210, (byte)255), ElementFxPalette.Rgb("ice"));
        Assert.Equal(((byte)190, (byte)255, (byte)170), ElementFxPalette.Rgb("air"));
        Assert.Equal(((byte)210, (byte)160, (byte)70), ElementFxPalette.Rgb("earth"));
        // omni / unknown / empty stay white — identical to the existing damage overlay.
        Assert.Equal(((byte)255, (byte)255, (byte)255), ElementFxPalette.Rgb("omni"));
        Assert.Equal(((byte)255, (byte)255, (byte)255), ElementFxPalette.Rgb("chaos"));
        Assert.Equal(((byte)255, (byte)255, (byte)255), ElementFxPalette.Rgb(null));
        Assert.Equal(((byte)255, (byte)90, (byte)40), ElementFxPalette.Rgb(" FIRE "));
    }

    [Fact]
    public void Concrete_filters_omni_unknown_and_nonpositive_weights()
    {
        var concrete = ElementFxPalette.Concrete(new[]
        {
            C("omni"), C("fire", 0.7), C("air", 0), C("ice", -1), C("earth", 0.3), C("bogus")
        });
        Assert.Equal(2, concrete.Count);
        Assert.Equal("fire", concrete[0].Element);
        Assert.Equal("earth", concrete[1].Element);
        Assert.Empty(ElementFxPalette.Concrete(null));
    }

    [Fact]
    public void Hybrid_color_cycles_components_and_wraps()
    {
        var comps = ElementFxPalette.Concrete(new[] { C("fire"), C("ice") });
        Assert.Equal(ElementFxPalette.Rgb("fire"), ElementFxPalette.HybridColorAt(comps, 0f));
        // mid-first-segment is a fire→ice blend, not either endpoint
        var mid = ElementFxPalette.HybridColorAt(comps, 0.25f);
        Assert.NotEqual(ElementFxPalette.Rgb("fire"), mid);
        Assert.NotEqual(ElementFxPalette.Rgb("ice"), mid);
        // second segment wraps back toward fire; t=1 lands on the wrap target
        Assert.Equal(ElementFxPalette.Rgb("fire"), ElementFxPalette.HybridColorAt(comps, 1f));
        // single component never cycles
        var one = ElementFxPalette.Concrete(new[] { C("earth") });
        Assert.Equal(ElementFxPalette.Rgb("earth"), ElementFxPalette.HybridColorAt(one, 0.5f));
    }

    [Fact]
    public void Particle_colors_distribute_by_weight_and_are_deterministic()
    {
        var comps = ElementFxPalette.Concrete(new[] { C("fire", 0.75), C("ice", 0.25) });
        const int count = 28;
        var fireBase = ElementFxPalette.Rgb("fire");
        var fireish = 0;
        for (var i = 0; i < count; i++)
        {
            var c = ElementFxPalette.ParticleColor(comps, i, count);
            // deterministic: same inputs, same output
            Assert.Equal(c, ElementFxPalette.ParticleColor(comps, i, count));
            // classify by dominant channel: fire is red-dominant, ice blue-dominant
            if (c.R > c.B) fireish++;
            // jitter never overflows to a different hue family
            Assert.True(c.R <= 255 && c.G <= 255 && c.B <= 255);
        }

        // 75/25 split over 28 stratified samples → 21 fire-ish
        Assert.Equal(21, fireish);
        Assert.True(fireBase.R > fireBase.B);
    }

    [Fact]
    public void Particle_color_single_component_is_flat()
    {
        var comps = ElementFxPalette.Concrete(new[] { C("ice") });
        Assert.Equal(ElementFxPalette.Rgb("ice"), ElementFxPalette.ParticleColor(comps, 0, 28));
        Assert.Equal(ElementFxPalette.Rgb("ice"), ElementFxPalette.ParticleColor(comps, 27, 28));
    }
}
