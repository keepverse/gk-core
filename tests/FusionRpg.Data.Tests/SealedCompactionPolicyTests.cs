using FusionRpg.Data.Policies;
using Xunit;

namespace FusionRpg.Data.Tests;

public class SealedCompactionPolicyTests
{
    [Fact]
    public void Sealed_limits_match_checklist()
    {
        Assert.Equal(10_000, SealedCompactionPolicy.ActivityRetainTail);
        Assert.Equal(5_000, SealedCompactionPolicy.XpRetainTailPerActor);
        Assert.Equal(50, SealedCompactionPolicy.KeepLastNFullCaptureRuns);
        Assert.Equal(1, SealedCompactionPolicy.ActivitySnapshotSchemaVersion);
        Assert.Equal(1, SealedCompactionPolicy.XpSnapshotSchemaVersion);
    }
}
