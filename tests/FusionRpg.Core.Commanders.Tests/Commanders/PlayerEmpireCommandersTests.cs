using FusionRpg.Core.Commanders;
using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

public class PlayerEmpireCommandersTests
{
    [Fact]
    public void ForPlayer_returns_the_human_empires_default_commander_for_a_positive_player_id()
    {
        var roster = PlayerEmpireCommanders.ForPlayer(ShippedCommanders.Directory, 1);
        Assert.Single(roster);
        Assert.Equal(EmpireId.Dave, ShippedCommanders.Directory.EmpireOf(roster[0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ForPlayer_returns_empty_for_non_positive_player_id(long playerId)
    {
        Assert.Empty(PlayerEmpireCommanders.ForPlayer(ShippedCommanders.Directory, playerId));
    }

    [Fact]
    public void IsPlayerDefaultAllowed_allows_only_the_human_empires_commander()
    {
        Assert.True(PlayerEmpireCommanders.IsPlayerDefaultAllowed(ShippedCommanders.Directory, ShippedCommanders.Dave));
        Assert.False(PlayerEmpireCommanders.IsPlayerDefaultAllowed(ShippedCommanders.Directory, ShippedCommanders.Zomboss));
    }

    [Fact]
    public void Names_and_scope_keys_come_from_the_directory_never_a_switch_here()
    {
        // SE4.1's one ruled change: the player's own commander shows the player's name, which the
        // caller supplies per save; Dr. Zomboss keeps his authored one. This class maps neither.
        // identity-rename T12: the registry's value, never the literal (owner rulings R9/R11).
        Assert.Equal(
            FusionRpg.Core.Narrative.LeadNamesHub.Current.Display(
                FusionRpg.Core.Narrative.LeadTokens.Antagonist),
            ShippedCommanders.Directory.DisplayName(ShippedCommanders.Zomboss, "Nene"));
        Assert.Equal("player:1", ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Dave, 1));
        Assert.Equal("zomboss:1", ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Zomboss, 1));
    }
}
