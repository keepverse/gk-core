using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class OverlayApplyGuardTests
{
    [Fact]
    public void Nested_using_stays_active_until_outer_dispose()
    {
        Assert.False(OverlayApplyGuard.IsActive);
        using (OverlayApplyGuard.Enter())
        {
            Assert.True(OverlayApplyGuard.IsActive);
            using (OverlayApplyGuard.Enter())
                Assert.True(OverlayApplyGuard.IsActive);
            Assert.True(OverlayApplyGuard.IsActive);
        }

        Assert.False(OverlayApplyGuard.IsActive);
    }

    [Fact]
    public void Exception_still_pops()
    {
        try
        {
            using (OverlayApplyGuard.Enter())
                throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        Assert.False(OverlayApplyGuard.IsActive);
    }

    [Fact]
    public void Extra_dispose_does_not_go_negative()
    {
        var d = OverlayApplyGuard.Enter();
        Assert.True(OverlayApplyGuard.IsActive);
        d.Dispose();
        Assert.False(OverlayApplyGuard.IsActive);
        d.Dispose();
        d.Dispose();
        Assert.False(OverlayApplyGuard.IsActive);
        using (OverlayApplyGuard.Enter())
            Assert.True(OverlayApplyGuard.IsActive);
        Assert.False(OverlayApplyGuard.IsActive);
    }
}
