using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

/// <summary>W0-D: LIVE combat.hit shapes (TakeDamage Bullet / AttackPlant) → OnDamageDealt.</summary>
public class EffectEventAdapterCoreTests
{
    [Fact]
    public void TryMap_combat_hit_takeDamage_bullet_maps_OnDamageDealt()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "combat.hit",
            new Dictionary<string, object>
            {
                ["side"] = "zombie",
                ["source"] = "takeDamage",
                ["attackerKind"] = "bullet",
                ["bulletPtr"] = "0xBEEF",
                ["targetPtr"] = "0xZOMB",
                ["fromType"] = 0,
                ["targetType"] = 1,
                ["damage"] = 20
            },
            tick: 42,
            matchKey: "m1");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnDamageDealt, ev!.Trigger);
        Assert.Equal("plant", ev.Side);
        Assert.Equal("0xBEEF", ev.ActorPtr);
        Assert.Equal("0xZOMB", ev.TargetPtr);
        Assert.Equal(0, ev.TypeId);
        Assert.Equal(1, ev.TargetTypeId);
        Assert.Equal(20, ev.Damage);
        Assert.Equal(42, ev.Tick);
        Assert.Equal("m1", ev.MatchKey);
    }

    [Fact]
    public void TryMap_combat_hit_attackPlant_maps_OnDamageDealt()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "combat.hit",
            new Dictionary<string, object>
            {
                ["side"] = "plant",
                ["source"] = "attackPlant",
                ["attackerKind"] = "zombie",
                ["attackerPtr"] = "0xZATK",
                ["targetPtr"] = "0xPLNT",
                ["fromType"] = 2,
                ["targetType"] = 3,
                ["damage"] = 100
            },
            tick: 7,
            matchKey: "m2");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnDamageDealt, ev!.Trigger);
        Assert.Equal("zombie", ev.Side);
        Assert.Equal("0xZATK", ev.ActorPtr);
        Assert.Equal("0xPLNT", ev.TargetPtr);
        Assert.Equal(2, ev.TypeId);
        Assert.Equal(3, ev.TargetTypeId);
        Assert.Equal(100, ev.Damage);
        Assert.Equal(7, ev.Tick);
        Assert.Equal("m2", ev.MatchKey);
    }

    [Fact]
    public void TryMap_combat_hit_prefers_attackerPtr_over_bulletPtr()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "combat.hit",
            new Dictionary<string, object>
            {
                ["side"] = "zombie",
                ["attackerPtr"] = "0xATK",
                ["bulletPtr"] = "0xBUL",
                ["targetPtr"] = "0xT",
                ["damage"] = 1
            },
            tick: 1);

        Assert.NotNull(ev);
        Assert.Equal("0xATK", ev!.ActorPtr);
    }

    [Fact]
    public void TryMap_combat_hit_plant_side_takeDamage_maps_actor_zombie()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "combat.hit",
            new Dictionary<string, object>
            {
                ["side"] = "plant",
                ["source"] = "takeDamage",
                ["bulletPtr"] = "0xPROJ",
                ["targetPtr"] = "0xPLNT",
                ["fromType"] = 4,
                ["targetType"] = 5,
                ["damage"] = 15
            },
            tick: 3);

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnDamageDealt, ev!.Trigger);
        Assert.Equal("zombie", ev.Side);
        Assert.Equal("0xPROJ", ev.ActorPtr);
        Assert.Equal("0xPLNT", ev.TargetPtr);
        Assert.Equal(15, ev.Damage);
    }

    [Fact]
    public void TryMap_plant_damage_maps_OnDamageTaken()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "plant.damage",
            new Dictionary<string, object>
            {
                ["ptr"] = "0xP",
                ["damageFrom"] = "0xDF",
                ["type"] = 7,
                ["damage"] = 50
            },
            tick: 9);

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnDamageTaken, ev!.Trigger);
        Assert.Equal("plant", ev.Side);
        Assert.Equal("0xDF", ev.ActorPtr);
        Assert.Equal("0xP", ev.TargetPtr);
        Assert.Equal(7, ev.TypeId);
        Assert.Equal(50, ev.Damage);
    }

    [Fact]
    public void TryMap_zombie_damage_maps_OnDamageTaken()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "zombie.damage",
            new Dictionary<string, object>
            {
                ["ptr"] = "0xZ",
                ["damageFrom"] = "0xB",
                ["typeId"] = 8,
                ["damage"] = 20
            },
            tick: 2);

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnDamageTaken, ev!.Trigger);
        Assert.Equal("zombie", ev.Side);
        Assert.Equal("0xB", ev.ActorPtr);
        Assert.Equal("0xZ", ev.TargetPtr);
        Assert.Equal(8, ev.TypeId);
    }

    [Fact]
    public void TryMap_combat_hitland_returns_null()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "combat.hitland",
            new Dictionary<string, object> { ["side"] = "zombie", ["damage"] = 1 },
            tick: 1);
        Assert.Null(ev);
    }
}
