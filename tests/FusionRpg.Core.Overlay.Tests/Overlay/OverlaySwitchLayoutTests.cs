using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

/// <summary>
/// Button geometry. Pure so the DPI behaviour is pinned without a screen — the button was
/// fixed-pixel at first, which left it tiny and hard to hit on a 4K display.
/// </summary>
public class OverlaySwitchLayoutTests
{
    [Fact]
    public void Scale_is_one_at_the_reference_height()
    {
        Assert.Equal(1f, OverlaySwitchLayout.ScaleFor(1080), 3);
    }

    [Fact]
    public void Scale_grows_with_the_display()
    {
        Assert.Equal(2f, OverlaySwitchLayout.ScaleFor(2160), 3);
        Assert.Equal(1.5f, OverlaySwitchLayout.ScaleFor(1620), 3);
    }

    [Theory]
    [InlineData(720)]
    [InlineData(600)]
    [InlineData(1)]
    public void Scale_never_shrinks_below_one(int height)
    {
        // Shrinking on a small display would make an already-small target unhittable.
        Assert.Equal(1f, OverlaySwitchLayout.ScaleFor(height), 3);
    }

    [Fact]
    public void Scale_is_capped_so_the_button_cannot_swallow_the_lawn()
    {
        Assert.Equal(OverlaySwitchLayout.MaxScale, OverlaySwitchLayout.ScaleFor(20_000), 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1080)]
    public void Nonsense_heights_fall_back_to_the_base_scale(int height)
    {
        Assert.Equal(1f, OverlaySwitchLayout.ScaleFor(height), 3);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1280, 720)]
    public void The_button_sits_fully_on_screen_in_the_bottom_right(int w, int h)
    {
        var r = OverlaySwitchLayout.BottomRight(w, h);

        Assert.True(r.X >= 0, "off the left edge");
        Assert.True(r.Y >= 0, "off the top edge");
        Assert.True(r.X + r.Width <= w, "off the right edge");
        Assert.True(r.Y + r.Height <= h, "off the bottom edge");

        // Bottom-right means bottom-right: past the midpoint on both axes.
        Assert.True(r.X > w / 2f);
        Assert.True(r.Y > h / 2f);
    }

    [Fact]
    public void The_button_keeps_its_margin_from_the_corner()
    {
        var r = OverlaySwitchLayout.BottomRight(1920, 1080);
        Assert.Equal(1920 - r.Width - OverlaySwitchLayout.BaseMargin, r.X, 3);
        Assert.Equal(1080 - r.Height - OverlaySwitchLayout.BaseMargin, r.Y, 3);
    }

    [Fact]
    public void A_4k_button_is_twice_the_1080p_button()
    {
        var hd = OverlaySwitchLayout.BottomRight(1920, 1080);
        var uhd = OverlaySwitchLayout.BottomRight(3840, 2160);
        Assert.Equal(hd.Width * 2f, uhd.Width, 3);
        Assert.Equal(hd.Height * 2f, uhd.Height, 3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(40, 20)]
    public void A_degenerate_screen_never_produces_a_negative_rect(int w, int h)
    {
        var r = OverlaySwitchLayout.BottomRight(w, h);
        Assert.True(r.X >= 0);
        Assert.True(r.Y >= 0);
        Assert.True(r.Width > 0);
        Assert.True(r.Height > 0);
    }
}
