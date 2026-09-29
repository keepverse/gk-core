using FusionRpg.Core.Hud;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class MagnitudeBandDisplayTests
{
    [Theory]
    [InlineData(0, MagnitudeBand.Low)]
    [InlineData(9.9, MagnitudeBand.Low)]
    [InlineData(10, MagnitudeBand.Mid)]
    [InlineData(29.9, MagnitudeBand.Mid)]
    [InlineData(30, MagnitudeBand.High)]
    [InlineData(100, MagnitudeBand.High)]
    public void FromEffectiveMagnitude_tertiles(double magnitude, MagnitudeBand expected) =>
        Assert.Equal(expected, MagnitudeBandDisplay.FromEffectiveMagnitude(magnitude));

    [Fact]
    public void FromEffectiveMagnitude_nan_is_low() =>
        Assert.Equal(MagnitudeBand.Low, MagnitudeBandDisplay.FromEffectiveMagnitude(double.NaN));
}
