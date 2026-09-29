using FusionRpg.Core.Commanders;
using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

/// <summary>
/// `commander-identity` SE4.2: the three id spaces stay apart now that a commander is data. The
/// stable-id, scope-key and parse facts this file used to assert on the retired `CommanderId` enum now
/// live on the directory (see <c>CommanderDirectoryTests</c>); kept here is the separation the enum's
/// own comment was about — a commander's stable id can never alias a bare faction id or a battle actor
/// key. The old count pin (`Assert.Equal(2, CommanderIds.All.Count)`) is gone with `CommanderIds.All`:
/// commanders are a population now, so "two" was a reading, not a contract.
/// </summary>
public class CommanderIdTests
{
    [Theory]
    [InlineData("commander:dave")]
    [InlineData("commander:zomboss")]
    public void A_commander_stable_id_never_collides_with_a_bare_faction_id(string stableId)
    {
        // WorldTemplateCatalog's faction ids are bare "dave"/"zomboss" — no prefix. The commander:
        // prefix is what keeps these two id spaces from ever aliasing to the same string.
        Assert.True(ShippedCommanders.Directory.TryResolve(stableId, out var commander));
        Assert.Equal(stableId, commander.StableId);
        Assert.NotEqual("dave", commander.StableId);
        Assert.NotEqual("zomboss", commander.StableId);
        Assert.StartsWith("commander:", commander.StableId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("commander:dave")]
    [InlineData("commander:zomboss")]
    public void A_commander_stable_id_never_collides_with_a_battle_actor_key(string stableId)
    {
        // BattleActorSetup.Key values are "squad:N" / "wave:N" — a different namespace prefix entirely,
        // so no commander id can ever alias a real battle actor's key.
        Assert.DoesNotContain("squad:", stableId, StringComparison.Ordinal);
        Assert.DoesNotContain("wave:", stableId, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_stable_ids_never_collide_with_each_other()
    {
        Assert.NotEqual(ShippedCommanders.Dave.StableId, ShippedCommanders.Zomboss.StableId);
    }

    [Fact]
    public void The_scope_keys_match_AptitudeEndpoints_ScopeKey_shape_exactly()
    {
        // Core cannot reference FusionRpg.Server (wrong dependency direction), so this pins the literal
        // string shape AptitudeEndpoints.ScopeKey(playerId) => $"player:{playerId}" produces, by
        // convention rather than by a shared reference.
        Assert.Equal("player:42", ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Dave, 42));
        Assert.Equal("zomboss:42", ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Zomboss, 42));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dave")]
    [InlineData("commander:penny")]
    [InlineData("not-a-commander")]
    public void An_unknown_stable_id_is_refused(string? stableId)
    {
        Assert.False(ShippedCommanders.Directory.TryResolve(stableId, out _));
    }

    [Fact]
    public void Different_saves_get_different_scope_keys_for_the_same_commander()
    {
        Assert.NotEqual(
            ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Dave, 1),
            ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Dave, 2));
        Assert.NotEqual(
            ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Zomboss, 1),
            ShippedCommanders.Directory.AllocationScopeKey(ShippedCommanders.Zomboss, 2));
    }
}
