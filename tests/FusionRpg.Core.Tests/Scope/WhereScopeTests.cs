using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Scope;

public class WhereScopeTests
{
    [Theory]
    [InlineData(WhereScope.Battlefield)]
    [InlineData(WhereScope.WorldMap)]
    public void Every_WhereScope_value_round_trips_through_Name_and_TryParse(WhereScope scope)
    {
        var name = WhereScopes.Name(scope);
        Assert.NotEqual("", name);
        Assert.True(WhereScopes.TryParse(name, out var parsed));
        Assert.Equal(scope, parsed);
    }

    [Fact]
    public void An_unknown_WhereScope_string_rejects_rather_than_defaulting()
    {
        Assert.False(WhereScopes.TryParse("lawn", out _));
        Assert.False(WhereScopes.TryParse(null, out _));
        Assert.False(WhereScopes.TryParse("", out _));
    }

    [Theory]
    [InlineData(ScopeHost.Sim)]
    [InlineData(ScopeHost.Live)]
    public void Every_ScopeHost_value_round_trips_through_Name_and_TryParse(ScopeHost host)
    {
        var name = ScopeHosts.Name(host);
        Assert.NotEqual("", name);
        Assert.True(ScopeHosts.TryParse(name, out var parsed));
        Assert.Equal(host, parsed);
    }

    [Fact]
    public void An_unknown_ScopeHost_string_rejects_rather_than_defaulting()
    {
        Assert.False(ScopeHosts.TryParse("unity", out _));
        Assert.False(ScopeHosts.TryParse(null, out _));
    }
}
