using FusionRpg.Contracts;
using FusionRpg.Core;
using Xunit;

namespace FusionRpg.Core.Tests;

/// <summary>U15 — sim shield probe: the SSOT shield stack with no game and no funnel.</summary>
public class SimEngineShieldTests
{
    static SimEngine EngineWithPlant()
    {
        var engine = new SimEngine();
        engine.BoardStart(null);
        engine.SpawnPlant(new StatsConfig(), new SimSpawnPlantRequest
        {
            Ptr = "P1", Row = 2, Col = 3, Hp = 300, MaxHp = 300
        });
        return engine;
    }

    static StatsConfig Logging() => new() { LogDamage = true };

    static Dictionary<string, object> Payload(SimResult result, string kind) =>
        (Dictionary<string, object>)result.Events.Single(e => e.Kind == kind).Payload!;

    [Fact]
    public void Grant_then_damage_absorbs_before_hp_no_game_required()
    {
        var engine = EngineWithPlant();
        var grant = engine.GrantShield(new SimShieldGrantRequest { Ptr = "P1", Amount = 50 });
        Assert.Equal("Applied", Payload(grant, "shield.granted")["outcome"]);
        Assert.Equal(50L, engine.ShieldTotals("P1").Hp);

        var dmg = engine.DamagePlant(Logging(), new SimDamageRequest { Ptr = "P1", Damage = 80 });
        Assert.Equal(50L, Convert.ToInt64(Payload(dmg, "plant.damage")["shieldAbsorbed"]));

        // Shield broke; remainder round(80 × 30/80) = 30 reached HP.
        Assert.Equal(0L, engine.ShieldTotals("P1").Hp);
        Assert.Equal(270, engine.Plants.Single(p => p.Ptr == "P1").Hp);
    }

    [Fact]
    public void Full_absorb_leaves_hp_untouched()
    {
        var engine = EngineWithPlant();
        engine.GrantShield(new SimShieldGrantRequest { Ptr = "P1", Amount = 500 });
        engine.DamagePlant(Logging(), new SimDamageRequest { Ptr = "P1", Damage = 80 });
        Assert.Equal(300, engine.Plants.Single(p => p.Ptr == "P1").Hp);
        Assert.Equal(420L, engine.ShieldTotals("P1").Hp);
    }

    [Fact]
    public void No_shield_damage_path_is_byte_identical()
    {
        var engine = EngineWithPlant();
        var dmg = engine.DamagePlant(Logging(), new SimDamageRequest { Ptr = "P1", Damage = 80 });
        var payload = Payload(dmg, "plant.damage");
        Assert.False(payload.ContainsKey("shieldAbsorbed"));   // additive key absent
        Assert.Equal(80, Convert.ToInt32(payload["damage"]));
        Assert.Equal(220, engine.Plants.Single(p => p.Ptr == "P1").Hp);
    }

    [Fact]
    public void Unknown_element_missing_entity_and_zero_amount_fail_loudly()
    {
        var engine = EngineWithPlant();
        Assert.NotNull(engine.GrantShield(new SimShieldGrantRequest { Ptr = "P1", Amount = 50, Element = "water" }).Error);
        Assert.NotNull(engine.GrantShield(new SimShieldGrantRequest { Ptr = "nope", Amount = 50 }).Error);
        Assert.NotNull(engine.GrantShield(new SimShieldGrantRequest { Ptr = "P1", Amount = 0 }).Error);
    }

    [Fact]
    public void Board_reset_clears_shields()
    {
        var engine = EngineWithPlant();
        engine.GrantShield(new SimShieldGrantRequest { Ptr = "P1", Amount = 50 });
        engine.BoardEnd(null);
        engine.BoardStart(null);
        engine.SpawnPlant(new StatsConfig(), new SimSpawnPlantRequest
        {
            Ptr = "P1", Row = 2, Col = 3, Hp = 300, MaxHp = 300
        });
        Assert.Equal((0L, 0L), engine.ShieldTotals("P1"));
    }

    [Fact]
    public void Entity_stats_dump_carries_live_shield_totals()
    {
        var engine = EngineWithPlant();
        engine.GrantShield(new SimShieldGrantRequest { Ptr = "P1", Amount = 75 });
        var dump = Payload(engine.Recapture(new SimEntityStatsRequest { Side = "plant", Ptr = "P1" }),
            "entity.stats");
        Assert.Equal(75L, Convert.ToInt64(dump["rpgShieldHp"]));
        Assert.Equal(75L, Convert.ToInt64(dump["rpgShieldMax"]));
    }
}
