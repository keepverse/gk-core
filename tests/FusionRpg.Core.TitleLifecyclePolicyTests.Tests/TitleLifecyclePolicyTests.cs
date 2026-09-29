using FusionRpg.Core.Achievements;
using Xunit;

namespace FusionRpg.Core.Tests;

// T6: lifecycle policy — clocks, remaining math (integer turns; no float exists here).
public class TitleLifecyclePolicyTests
{
    [Fact]
    public void Remaining_converges_from_state()
    {
        Assert.Equal(20, TitleLifecyclePolicy.Remaining(10, 20, 10));
        Assert.Equal(1, TitleLifecyclePolicy.Remaining(10, 20, 29));
        Assert.Equal(0, TitleLifecyclePolicy.Remaining(10, 20, 30));
        Assert.Equal(-5, TitleLifecyclePolicy.Remaining(10, 20, 35));
    }

    [Theory]
    [InlineData("empire", "world-turns", null, true)]
    [InlineData("empire", "wall-clock", null, false)]
    [InlineData("unique-actor", "battle-ticks", "round", true)]
    [InlineData("unique-actor", "battle-ticks", null, false)]
    [InlineData("unique-actor", "world-turns", null, false)]
    [InlineData("void", "world-turns", null, false)]
    public void Clock_table_enforced(string scope, string clock, string? counter, bool ok)
    {
        Assert.Equal(ok, TitleLifecyclePolicy.ClockRefusal(scope, clock, counter) is null);
    }
}
