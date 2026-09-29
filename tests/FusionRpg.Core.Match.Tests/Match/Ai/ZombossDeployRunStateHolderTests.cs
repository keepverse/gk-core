using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

public class ZombossDeployRunStateHolderTests
{
    public ZombossDeployRunStateHolderTests() => ZombossDeployRunStateHolder.BeginMatch();

    [Fact]
    public void Fresh_holder_has_not_fired()
    {
        Assert.False(ZombossDeployRunStateHolder.AlreadyFired);
    }

    [Fact]
    public void RecordFired_sets_AlreadyFired()
    {
        ZombossDeployRunStateHolder.RecordFired();
        Assert.True(ZombossDeployRunStateHolder.AlreadyFired);
    }

    [Fact]
    public void BeginMatch_resets_even_after_a_fire_was_recorded()
    {
        ZombossDeployRunStateHolder.RecordFired();
        ZombossDeployRunStateHolder.BeginMatch();
        Assert.False(ZombossDeployRunStateHolder.AlreadyFired);
    }

    [Fact]
    public void EndMatch_also_resets()
    {
        ZombossDeployRunStateHolder.RecordFired();
        ZombossDeployRunStateHolder.EndMatch();
        Assert.False(ZombossDeployRunStateHolder.AlreadyFired);
    }
}
