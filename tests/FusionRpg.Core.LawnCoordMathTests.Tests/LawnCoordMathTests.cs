using FusionRpg.Core.Effects;
using FusionRpg.Core.Lawn;
using Xunit;

namespace FusionRpg.Core.Tests;

public class LawnCoordMathTests
{
    [Fact]
    public void ClampIndex_clamps_to_inclusive_last()
    {
        Assert.Equal(0, LawnCoordMath.ClampIndex(-1, 4));
        Assert.Equal(2, LawnCoordMath.ClampIndex(2, 4));
        Assert.Equal(4, LawnCoordMath.ClampIndex(5, 4));
        Assert.Equal(0, LawnCoordMath.ClampIndex(3, -1));
        Assert.Equal(0, LawnCoordMath.ClampIndex(-8, -2));
    }

    [Fact]
    public void HalfCellY_is_half_abs_delta_or_zero_when_degenerate()
    {
        Assert.Equal(1f, LawnCoordMath.HalfCellY(2f, 0f));
        Assert.Equal(0f, LawnCoordMath.HalfCellY(3f, 3f));
        Assert.Equal(0f, LawnCoordMath.HalfCellY(0f, 1e-5f));
    }

    [Fact]
    public void GuiPoint_applies_pixelRect_and_rise()
    {
        var a = LawnCoordMath.GuiPoint(800f, 100f, 80f, 0f, 0f, 0f);
        Assert.Equal(100f, a.X);
        Assert.Equal(720f, a.Y);

        var b = LawnCoordMath.GuiPoint(800f, 100f, 80f, 10f, 80f, 0f);
        Assert.Equal(110f, b.X);
        Assert.Equal(640f, b.Y);

        var c = LawnCoordMath.GuiPoint(800f, 100f, 80f, 0f, 0f, 1f);
        Assert.Equal(100f, c.X);
        Assert.Equal(720f - DamageFxFloaterRules.RisePixels, c.Y);
    }

    [Fact]
    public void Defaults_match_adventure_lawn()
    {
        Assert.Equal(9, LawnCoordMath.DefaultLastCol);
        Assert.Equal(4, LawnCoordMath.DefaultLastRow);
    }
}
