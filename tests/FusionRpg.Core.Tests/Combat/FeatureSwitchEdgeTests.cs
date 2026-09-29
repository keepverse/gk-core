using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>lawn-combat-wire L-N8: the basic-attack kill switch turning off mid-match withdraws the grants bound while
/// it was on. The edge must fire once per on→off transition and never on the first observation.</summary>
public class FeatureSwitchEdgeTests
{
    [Fact]
    public void Fires_once_per_on_to_off_transition()
    {
        var edge = new FeatureSwitchEdge();
        var fired = new[] { true, true, false, false, true, false }.Select(edge.TurnedOff).ToArray();

        Assert.Equal(new[] { false, false, true, false, false, true }, fired);
    }

    [Fact]
    public void A_process_that_starts_off_never_fires()
    {
        var edge = new FeatureSwitchEdge();
        Assert.False(edge.TurnedOff(false));
        Assert.False(edge.TurnedOff(false));
    }

    [Theory]
    [InlineData("1A2B", true)]
    [InlineData("0x1a2b", true)]
    public void Basic_attack_grant_ids_are_recognised(string ptr, bool expected) =>
        Assert.Equal(expected, BasicAttackGrantBuilder.IsBasicAttackGrantId(BasicAttackGrantBuilder.GrantIdFor(ptr)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("patron:aura")]
    [InlineData("lawn-basic-attack-other@1A2B")]
    public void Other_grant_ids_are_not_withdrawn(string? grantId) =>
        Assert.False(BasicAttackGrantBuilder.IsBasicAttackGrantId(grantId));
}
