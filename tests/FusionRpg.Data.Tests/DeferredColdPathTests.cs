using FusionRpg.Data.Abstractions;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("VerificationId", "data.deferred-cold-path")]
public class DeferredColdPathTests
{
    [Fact]
    public void Deferred_stubs_remain_unimplemented()
    {
        Assert.False(new DeferredColdPathQuery().IsImplemented);
        Assert.False(new DeferredGarbageCollector().IsImplemented);
    }
}
