using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

public class PvzStatsDerivedChannelTests
{
    [Fact]
    public void TryCanonicalizeOrDerivedChannel_accepts_status_power()
    {
        var channel = PvzStatsSheetComposer.TryCanonicalizeOrDerivedChannel("status.power.wither");
        Assert.Equal("status.power.wither", channel);
    }

    [Fact]
    public void TryCanonicalizeOrDerivedChannel_accepts_primary_hp()
    {
        var channel = PvzStatsSheetComposer.TryCanonicalizeOrDerivedChannel("hp");
        Assert.Equal("hp", channel);
    }

    [Fact]
    public void TryCanonicalizeOrDerivedChannel_rejects_unknown()
    {
        Assert.Null(PvzStatsSheetComposer.TryCanonicalizeOrDerivedChannel("not.a.real.channel"));
    }
}
