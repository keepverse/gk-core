using FusionRpg.Core.Hud;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudScreenAnchorTests
{
    [Fact]
    public void TryResolve_centers_below_the_visual_rectangle()
    {
        var visual = new ActorHudScreenRect(100f, 200f, 80f, 120f);

        var ok = ActorHudScreenAnchorMath.TryResolve(
            visual, gapPixels: 12f, widthFactor: 0.8f, minWidthPixels: 32f, maxWidthPixels: 96f,
            out var anchor);

        Assert.True(ok);
        Assert.Equal(140f, anchor.CenterX);
        Assert.Equal(188f, anchor.TopY);
        Assert.Equal(64f, anchor.Width);
    }

    [Fact]
    public void TryResolve_applies_readability_width_bounds_without_moving_the_anchor()
    {
        var visual = new ActorHudScreenRect(20f, 40f, 20f, 30f);

        var ok = ActorHudScreenAnchorMath.TryResolve(
            visual, gapPixels: 6f, widthFactor: 0.5f, minWidthPixels: 24f, maxWidthPixels: 80f,
            out var anchor);

        Assert.True(ok);
        Assert.Equal(30f, anchor.CenterX);
        Assert.Equal(34f, anchor.TopY);
        Assert.Equal(24f, anchor.Width);
    }

    [Theory]
    [InlineData(0f, 20f)]
    [InlineData(20f, 0f)]
    [InlineData(-1f, 20f)]
    public void TryResolve_rejects_non_visual_rectangles(float width, float height)
    {
        var visual = new ActorHudScreenRect(0f, 0f, width, height);

        Assert.False(ActorHudScreenAnchorMath.TryResolve(
            visual, gapPixels: 6f, widthFactor: 0.8f, minWidthPixels: 24f, maxWidthPixels: 80f,
            out _));
    }

    [Fact]
    public void CenteredPackedStart_centers_a_mixed_width_identity_row()
    {
        const float roleWidth = 9f;
        const float tierWidth = 12f;
        const float levelWidth = 13.2f;
        const float gap = 1.8f;

        var left = ActorHudScreenLayoutMath.CenteredPackedStart(roleWidth + tierWidth + levelWidth, 3, gap);
        var roleCenter = ActorHudScreenLayoutMath.TakeItemCenter(ref left, roleWidth, gap);
        var tierCenter = ActorHudScreenLayoutMath.TakeItemCenter(ref left, tierWidth, gap);
        var levelCenter = ActorHudScreenLayoutMath.TakeItemCenter(ref left, levelWidth, gap);

        Assert.Equal(-14.4f, roleCenter, 4);
        Assert.Equal(-2.1f, tierCenter, 4);
        Assert.Equal(12.3f, levelCenter, 4);
        Assert.Equal(0f, (roleCenter - roleWidth * 0.5f + levelCenter + levelWidth * 0.5f) * 0.5f, 4);
    }
}
