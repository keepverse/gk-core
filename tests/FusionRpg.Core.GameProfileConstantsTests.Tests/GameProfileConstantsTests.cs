using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.Core.Tests;

/// <summary>Charter lock: webrpg-1 is a real profile id, distinct from every PvZ profile.</summary>
public class GameProfileConstantsTests
{
    [Fact]
    public void WebRpg_profile_id_is_stable()
    {
        Assert.Equal("webrpg-1", RpgConstants.GameIdWebRpg);
    }

    [Fact]
    public void WebRpg_profile_is_distinct_from_pvz_profiles()
    {
        Assert.NotEqual(RpgConstants.GameId381, RpgConstants.GameIdWebRpg);
        Assert.NotEqual(RpgConstants.GameId39, RpgConstants.GameIdWebRpg);
        Assert.NotEqual(RpgConstants.GameId, RpgConstants.GameIdWebRpg);
    }

    [Fact]
    public void Web_source_tag_is_distinct_from_existing_sources()
    {
        Assert.Equal("web", RpgConstants.SourceWeb);
        Assert.NotEqual(RpgConstants.SourceInjector, RpgConstants.SourceWeb);
        Assert.NotEqual(RpgConstants.SourceSim, RpgConstants.SourceWeb);
    }
}
