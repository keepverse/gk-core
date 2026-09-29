using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class CombatHitEmitPolicyTests
{
    [Fact]
    public void WillSkipTaken_bullet_and_willEmitHit_skips()
    {
        var payload = new Dictionary<string, object> { ["damageFromIsBullet"] = true };
        Assert.True(CombatHitEmitPolicy.WillSkipTakenFromDamage("zombie.damage", payload, willEmitHit: true));
        Assert.True(CombatHitEmitPolicy.WillSkipTakenFromDamage("plant.damage", payload, willEmitHit: true));
    }

    [Fact]
    public void WillSkipTaken_willEmitHit_false_never_skips()
    {
        var payload = new Dictionary<string, object> { ["damageFromIsBullet"] = true };
        Assert.False(CombatHitEmitPolicy.WillSkipTakenFromDamage("zombie.damage", payload, willEmitHit: false));
    }

    [Fact]
    public void WillSkipTaken_live_plant_bite_damageFromIsPlant_does_not_skip()
    {
        var payload = new Dictionary<string, object>
        {
            ["damageFromIsPlant"] = true,
            ["damageFromIsZombie"] = false,
            ["damageFromIsBullet"] = false
        };
        Assert.False(CombatHitEmitPolicy.WillSkipTakenFromDamage("plant.damage", payload, willEmitHit: true));
    }

    [Fact]
    public void WillSkipTaken_damageFromIsZombie_does_not_skip()
    {
        // LIVE bites stamp plant-self; even if zombie were stamped, melee relies on DealtIdentity.
        var payload = new Dictionary<string, object> { ["damageFromIsZombie"] = true };
        Assert.False(CombatHitEmitPolicy.WillSkipTakenFromDamage("plant.damage", payload, willEmitHit: true));
    }

    [Fact]
    public void WillSkipTaken_non_damage_kind_false()
    {
        var payload = new Dictionary<string, object> { ["damageFromIsBullet"] = true };
        Assert.False(CombatHitEmitPolicy.WillSkipTakenFromDamage("combat.hit", payload, willEmitHit: true));
    }
}
