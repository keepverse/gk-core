using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// lawn-combat-wire L-N25: a type-keyed owner (<c>plant:{tid}</c> / <c>zombie:{tid}</c>) matches
/// <c>OnDamageDealt</c> only on the ATTACKER's own side and type. For OnDamageDealt every producer stamps
/// <c>Side</c> = attacker side (<c>EventDrain.ToDto</c>, <c>EffectEventAdapterCore.MapDealtFromCombatHit</c>).
/// The zombie branch used to fall back to <c>TargetTypeId</c> when <c>TypeId</c> was absent, so a
/// <c>zombie:0</c> grant matched a hit dealt TO a type-0 plant by some other zombie.
/// </summary>
public class TypeOwnerKeyDirectionTests
{
    static EffectGrant Grant(string ownerKey) => EffectGrant.FromDto(new EffectGrantDto
    {
        GrantId = "g",
        EffectId = "fx.probe",
        OwnerKey = ownerKey
    });

    static EffectEventDto Dealt(string? side, int? typeId, int? targetTypeId) => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        Side = side,
        TypeId = typeId,
        TargetTypeId = targetTypeId,
        Tick = 1
    };

    [Fact]
    public void Zombie_key_matches_a_hit_dealt_by_its_own_type()
    {
        Assert.True(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), Dealt("zombie", 7, 0)));
    }

    [Fact]
    public void Zombie_key_does_not_match_on_the_victims_type_when_the_attacker_type_is_absent()
    {
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("zombie:0"), Dealt("zombie", null, 0)));
    }

    [Fact]
    public void Zombie_key_does_not_match_a_hit_dealt_by_a_different_zombie_type()
    {
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), Dealt("zombie", 3, 7)));
    }

    [Fact]
    public void Zombie_key_does_not_match_a_hit_dealt_by_a_plant_of_the_same_number()
    {
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), Dealt("plant", 7, 2)));
    }

    [Fact]
    public void Plant_key_matches_only_its_own_attacker_type()
    {
        Assert.True(EffectOwnerKey.MatchesEvent(Grant("plant:4"), Dealt("plant", 4, 0)));
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("plant:4"), Dealt("plant", null, 4)));
        Assert.False(EffectOwnerKey.MatchesEvent(Grant("plant:4"), Dealt("zombie", 4, 1)));
    }

    [Fact]
    public void Zombie_key_OnDamageTaken_still_matches_the_victim_type()
    {
        var taken = new EffectEventDto { Trigger = EffectTriggers.OnDamageTaken, Side = "zombie", TargetTypeId = 7, Tick = 1 };
        Assert.True(EffectOwnerKey.MatchesEvent(Grant("zombie:7"), taken));
    }
}
