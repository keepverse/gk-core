using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

public class ShieldBarColorTests
{
    [Fact]
    public void Rgb_roster_matches_element_palette_untyped_is_pale_blue()
    {
        Assert.Equal(ElementFxPalette.Rgb("fire"), ShieldBarColor.Rgb("fire"));
        Assert.Equal(ElementFxPalette.Rgb("ice"), ShieldBarColor.Rgb("ice"));
        Assert.Equal(ElementFxPalette.Rgb("light"), ShieldBarColor.Rgb("light"));
        Assert.Equal(ShieldBarColor.UntypedRgb, ShieldBarColor.Rgb(null));
        Assert.Equal(ShieldBarColor.UntypedRgb, ShieldBarColor.Rgb("none"));
        Assert.Equal(ShieldBarColor.UntypedRgb, ShieldBarColor.Rgb("omni"));
        Assert.Equal(ShieldBarColor.UntypedRgb, ShieldBarColor.Rgb("bogus"));
    }

    [Fact]
    public void Zero_or_empty_stacks_skip_draw()
    {
        var into = new List<ShieldBarColor.Stop>();
        Assert.False(ShieldBarColor.TryBuildStops(Array.Empty<(string?, long)>(), into));
        Assert.False(ShieldBarColor.TryBuildStops(new (string?, long)[] { ("fire", 0), ("ice", 0) }, into));
        Assert.False(ShieldBarColor.TryColorAt(new (string?, long)[] { ("fire", 0) }, 0.5f, out _));
    }

    [Fact]
    public void Single_stack_is_solid_full_span()
    {
        var into = new List<ShieldBarColor.Stop>();
        Assert.True(ShieldBarColor.TryBuildStops(new (string?, long)[] { ("fire", 100) }, into));
        Assert.Single(into);
        Assert.Equal(0f, into[0].StartU);
        Assert.Equal(1f, into[0].EndU);
        Assert.Equal(ElementFxPalette.Rgb("fire"), (into[0].R, into[0].G, into[0].B));
        Assert.True(ShieldBarColor.TryColorAt(into, 0f, out var a));
        Assert.True(ShieldBarColor.TryColorAt(into, 0.5f, out var b));
        Assert.True(ShieldBarColor.TryColorAt(into, 1f, out var c));
        Assert.Equal(ElementFxPalette.Rgb("fire"), a);
        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void Two_stacks_split_by_hp_weight()
    {
        var stacks = new (string?, long)[] { ("fire", 60), ("ice", 40) };
        var into = new List<ShieldBarColor.Stop>();
        Assert.True(ShieldBarColor.TryBuildStops(stacks, into));
        Assert.Equal(2, into.Count);
        Assert.Equal(0f, into[0].StartU);
        Assert.Equal(0.6f, into[0].EndU, 3);
        Assert.Equal(0.6f, into[1].StartU, 3);
        Assert.Equal(1f, into[1].EndU);
        Assert.True(ShieldBarColor.TryColorAt(into, 0.3f, out var left));
        Assert.True(ShieldBarColor.TryColorAt(into, 0.8f, out var right));
        Assert.Equal(ElementFxPalette.Rgb("fire"), left);
        Assert.Equal(ElementFxPalette.Rgb("ice"), right);
        // Boundary prefers left stop (u < EndU)
        Assert.True(ShieldBarColor.TryColorAt(into, 0.599f, out var near));
        Assert.Equal(ElementFxPalette.Rgb("fire"), near);
    }

    [Fact]
    public void Three_stacks_and_skips_zero_hp()
    {
        var stacks = new (string?, long)[] { ("fire", 50), ("ice", 0), ("earth", 50), (null, 100) };
        var into = new List<ShieldBarColor.Stop>();
        Assert.True(ShieldBarColor.TryBuildStops(stacks, into));
        Assert.Equal(3, into.Count);
        Assert.Equal("fire", into[0].ElementId);
        Assert.Equal("earth", into[1].ElementId);
        Assert.Equal("none", into[2].ElementId);
        Assert.Equal(0.25f, into[0].EndU, 3);
        Assert.Equal(0.5f, into[1].EndU, 3);
        Assert.Equal(1f, into[2].EndU);
        Assert.True(ShieldBarColor.TryColorAt(into, 0.9f, out var untyped));
        Assert.Equal(ShieldBarColor.UntypedRgb, untyped);
    }
}
