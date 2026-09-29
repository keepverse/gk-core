using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>vfx-v3 M4: composite math — strength clamp, stacking, identity preservation.</summary>
public class VfxTintMathTests
{
    static readonly (byte R, byte G, byte B) White = (255, 255, 255);
    static readonly (byte R, byte G, byte B) Red = (180, 60, 60);

    [Fact]
    public void Empty_stack_is_identity()
    {
        Assert.Equal(White, VfxTintMath.Composite(White, Array.Empty<((byte, byte, byte), float)>()));
    }

    [Fact]
    public void Single_layer_lerps_toward_status_color()
    {
        var (r, g, b) = VfxTintMath.Composite(White, new[] { (Red, 0.15f) });
        Assert.True(r < 255 && r > Red.R);
        Assert.True(g < 255 && g > Red.G);
        // 15% toward red from white: 255 - (255-180)*0.15 ≈ 244
        Assert.InRange(r, 243, 245);
    }

    [Fact]
    public void Strength_clamps_at_35_percent()
    {
        var full = VfxTintMath.Composite(White, new[] { (Red, 1.0f) });
        var capped = VfxTintMath.Composite(White, new[] { (Red, VfxTintMath.MaxStrength) });
        Assert.Equal(capped, full); // 100% requested, 35% delivered — identity never lost
        Assert.True(full.R > Red.R);
    }

    [Fact]
    public void Layers_stack_sequentially()
    {
        var green = ((byte)60, (byte)200, (byte)60);
        var one = VfxTintMath.Composite(White, new[] { (Red, 0.2f) });
        var two = VfxTintMath.Composite(White, new[] { (Red, 0.2f), (green, 0.2f) });
        // each layer lerps every channel toward its own color
        Assert.True(two.R < one.R, "green layer pulls red down");
        Assert.True(Math.Abs(two.G - green.Item2) < Math.Abs(one.G - green.Item2),
            "G lands closer to the green layer's channel");
    }
}
