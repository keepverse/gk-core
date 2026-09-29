using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// T12/T13 (buff-debuff-scope-todo.md Phase 4). `ScopeModifierMilli` follows
/// `UpkeepHandicapMilli`'s exact hashing precedent; own-side/unique-creature resolution reuse
/// `WorldEntity.OwnerFactionId`/`Members[].InstanceId` directly.
/// </summary>
public class WorldMapScopeTests
{
    static WorldState BaseWorld(int scopeModifierMilli = 1000) => new()
    {
        WorldId = "w-test", TemplateId = "t-test", Seed = 7,
        Factions = new[]
        {
            new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Zomboss", ScopeModifierMilli = scopeModifierMilli },
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
        },
    };

    [Fact]
    public void A_world_with_an_active_modifier_hashes_differently_from_one_without()
    {
        var withDefault = WorldCanonical.Write(BaseWorld(1000));
        var withModifier = WorldCanonical.Write(BaseWorld(1200));

        Assert.NotEqual(withDefault, withModifier);
    }

    [Fact]
    public void Replaying_the_same_world_twice_produces_byte_identical_canonical_text()
    {
        var first = WorldCanonical.Write(BaseWorld(1150));
        var second = WorldCanonical.Write(BaseWorld(1150));

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_default_modifier_matches_UpkeepHandicapMilli_own_neutral_value()
    {
        Assert.Equal(1000, new WorldFaction { FactionId = "x", Name = "X" }.ScopeModifierMilli);
    }

    [Fact]
    public void Own_side_resolves_by_plain_OwnerFactionId_comparison()
    {
        var mine = new WorldEntity { EntityId = "e1", OwnerFactionId = "dave" };
        var theirs = new WorldEntity { EntityId = "e2", OwnerFactionId = "zomboss" };

        Assert.True(WorldMapScopeExecutor.IsOwnSide(mine, "dave"));
        Assert.False(WorldMapScopeExecutor.IsOwnSide(theirs, "dave"));
    }

    [Fact]
    public void Unique_creature_resolves_by_walking_Members_for_a_matching_InstanceId()
    {
        var world = BaseWorld() with
        {
            Entities = new[]
            {
                new WorldEntity
                {
                    EntityId = "legion-1", OwnerFactionId = "dave",
                    Members = new List<WorldEntityMember>
                    {
                        new() { InstanceId = "inst-alpha", SpeciesId = "sp1" },
                        new() { InstanceId = "inst-beta", SpeciesId = "sp2" },
                    },
                },
                new WorldEntity { EntityId = "legion-2", OwnerFactionId = "zomboss" },
            },
        };

        var found = WorldMapScopeExecutor.FindEntityForInstance(world, "inst-beta");

        Assert.NotNull(found);
        Assert.Equal("legion-1", found!.EntityId);
    }

    [Fact]
    public void Unique_creature_resolves_null_when_the_specimen_has_no_legion_presence()
    {
        var world = BaseWorld();
        Assert.Null(WorldMapScopeExecutor.FindEntityForInstance(world, "inst-nowhere"));
    }
}
