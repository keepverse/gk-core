using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

public class ShieldBarVisualTests
{
    [Theory]
    [InlineData(0, 100, 0f)]
    [InlineData(50, 0, 0f)]
    [InlineData(-1, 100, 0f)]
    public void Empty_or_invalid_is_zero(long hp, long max, float expected) =>
        Assert.Equal(expected, ShieldBarVisual.DisplayRatio(hp, max));

    [Theory]
    [InlineData(100, 100, 1.0f)]
    [InlineData(99, 100, 0.9f)]
    [InlineData(91, 100, 0.9f)]
    [InlineData(90, 100, 0.9f)]
    [InlineData(89, 100, 0.8f)]
    [InlineData(81, 100, 0.8f)]
    [InlineData(55, 100, 0.5f)]
    [InlineData(50, 100, 0.5f)]
    [InlineData(49, 100, 0.4f)]
    [InlineData(10, 100, 0.1f)]
    [InlineData(9, 100, 0.1f)]
    [InlineData(1, 100, 0.1f)]
    public void Floors_to_tenths_with_min_sliver(long hp, long max, float expected) =>
        Assert.Equal(expected, ShieldBarVisual.DisplayRatio(hp, max));

    [Fact]
    public void Example_89_percent_renders_as_80() =>
        Assert.Equal(0.8f, ShieldBarVisual.DisplayRatio(89, 100));
}
