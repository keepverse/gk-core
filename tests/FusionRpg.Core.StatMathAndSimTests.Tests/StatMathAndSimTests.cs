using FusionRpg.Contracts;
using FusionRpg.Core;
using Xunit;

namespace FusionRpg.Core.Tests;

public class StatMathTests
{
    [Fact]
    public void HpAtk_percent_and_flat()
    {
        Assert.Equal(600, StatMath.ScaleHpOrAtk(300, 2f, 0));
        Assert.Equal(320, StatMath.ScaleHpOrAtk(300, 1f, 20));
        Assert.Equal(1, StatMath.ScaleHpOrAtk(0, 1f, 0));
    }

    [Fact]
    public void Defense_divides_then_flat()
    {
        Assert.Equal(25, StatMath.ScaleIncoming(50, 2f, 0));
        Assert.Equal(40, StatMath.ScaleIncoming(50, 1f, 10));
        Assert.Equal(0, StatMath.ScaleIncoming(5, 1f, 10));
    }

    [Fact]
    public void Defense_percent_zero_or_negative_is_one()
    {
        Assert.Equal(50, StatMath.ScaleIncoming(50, 0f, 0));
        Assert.Equal(50, StatMath.ScaleIncoming(50, -1f, 0));
    }
}

public class SimEngineTests
{
    [Fact]
    public void Apply_once_same_ptr()
    {
        var eng = new SimEngine();
        var stats = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f } };
        var first = eng.SpawnPlant(stats, new SimSpawnPlantRequest { Ptr = "P1", Hp = 300 });
        var second = eng.SpawnPlant(stats, new SimSpawnPlantRequest { Ptr = "P1", Hp = 300 });
        Assert.False(first.Skipped);
        Assert.True(second.Skipped);
        Assert.Single(eng.Plants);
        Assert.Equal(600, eng.Plants[0].Hp);
        Assert.Single(first.Events, e => e.Kind == "stat.applied");
        Assert.Empty(second.Events);
    }

    [Fact]
    public void ApplyStats_false_does_not_change_hp()
    {
        var eng = new SimEngine();
        var stats = new StatsConfig { ApplyStats = false, Plants = { HpPercent = 2f } };
        var r = eng.SpawnPlant(stats, new SimSpawnPlantRequest { Hp = 300 });
        Assert.DoesNotContain(r.Events, e => e.Kind == "stat.applied");
        Assert.Equal(300, eng.Plants[0].Hp);
        Assert.Contains(r.Events, e => e.Kind == "plant.spawn");
    }

    [Fact]
    public void Armor_zero_stays_zero_positive_scales()
    {
        var eng = new SimEngine();
        var stats = new StatsConfig { ApplyStats = true, Zombies = { HpPercent = 2f } };
        eng.SpawnZombie(stats, new SimSpawnZombieRequest { Hp = 270, Armor = 0 });
        Assert.Equal(0, eng.Zombies[0].Armor);
        eng.Reset();
        eng.SpawnZombie(stats, new SimSpawnZombieRequest { Hp = 270, Armor = 40, ArmorMax = 40 });
        Assert.Equal(80, eng.Zombies[0].Armor);
        Assert.Equal(80, eng.Zombies[0].ArmorMax);
    }

    [Fact]
    public void Damage_log_gated()
    {
        var eng = new SimEngine();
        var off = new StatsConfig { LogDamage = false };
        eng.SpawnPlant(off, new SimSpawnPlantRequest { Ptr = "P1" });
        var quiet = eng.DamagePlant(off, new SimDamageRequest { Ptr = "P1", Damage = 50 });
        Assert.DoesNotContain(quiet.Events, e => e.Kind == "plant.damage");
        var on = new StatsConfig { LogDamage = true, ApplyStats = true, Plants = { DefensePercent = 2f } };
        var noisy = eng.DamagePlant(on, new SimDamageRequest { Ptr = "P1", Damage = 50 });
        var dmg = Assert.Single(noisy.Events);
        Assert.Equal("plant.damage", dmg.Kind);
        var dict = Assert.IsType<Dictionary<string, object>>(dmg.Payload);
        Assert.Equal(25, Convert.ToInt32(dict["after"]));
        Assert.Equal(50, Convert.ToInt32(dict["before"]));
    }

    [Fact]
    public void Match_lifecycle_emits_kinds()
    {
        var eng = new SimEngine();
        var stats = new StatsConfig();
        var start = eng.BoardStart(new SimBoardStartRequest { LevelName = "Test" });
        Assert.False(string.IsNullOrWhiteSpace(eng.MatchKey));
        Assert.All(start.Events, e => Assert.Equal(eng.MatchKey, e.MatchKey));
        var spawn = eng.SpawnPlant(stats, null);
        Assert.Equal(eng.MatchKey, spawn.Events.Last().MatchKey);
        Assert.Contains(spawn.Events, e => e.Kind == "plant.spawn" && PayloadInt(e, "hpBase") == 300);
        Assert.Equal("Peashooter", PayloadString(spawn.Events.First(e => e.Kind == "plant.spawn"), "typeName"));
        eng.SpawnZombie(stats, null);
        eng.DiePlant(new SimDieRequest { Ptr = "P1" });
        eng.DieZombie(new SimDieRequest { Ptr = "Z1" });
        Assert.Contains(eng.BoardEnd(null).Events, e => e.Kind == "board.end");
        Assert.Empty(eng.Plants);
        Assert.Empty(eng.Zombies);
    }

    [Fact]
    public void Mower_place_start_die()
    {
        var eng = new SimEngine();
        eng.BoardStart(null);
        var place = eng.PlaceMower(new SimMowerRequest { Row = 2 });
        Assert.Contains(place.Events, e => e.Kind == "mower.place");
        Assert.Contains(eng.StartMower(new SimMowerRequest { Ptr = "M1" }).Events, e => e.Kind == "mower.start");
        Assert.Contains(eng.DieMower(new SimMowerRequest { Ptr = "M1" }).Events, e => e.Kind == "mower.die");
        Assert.True(eng.Mowers[0].Started);
        Assert.True(eng.Mowers[0].Dead);
    }

    [Fact]
    public void Catalog_types_emits_plant_and_zombie_names()
    {
        var eng = new SimEngine();
        var r = eng.CatalogTypes();
        var env = Assert.Single(r.Events);
        Assert.Equal("catalog.types", env.Kind);
        var dict = Assert.IsType<Dictionary<string, object>>(env.Payload);
        Assert.True(dict.ContainsKey("plants"));
        Assert.True(dict.ContainsKey("zombies"));
    }

    [Fact]
    public void Place_does_not_spawn()
    {
        var eng = new SimEngine();
        eng.BoardStart(null);
        var place = eng.PlacePlant(new SimPlacePlantRequest { Ptr = "P9" });
        Assert.Contains(place.Events, e => e.Kind == "plant.place");
        Assert.Empty(eng.Plants);
    }

    [Fact]
    public void Recipes_emit_parents()
    {
        var eng = new SimEngine();
        var env = Assert.Single(eng.CatalogRecipes().Events);
        Assert.Equal("catalog.recipes", env.Kind);
        var dict = Assert.IsType<Dictionary<string, object>>(env.Payload);
        Assert.True(dict.ContainsKey("entries"));
    }

    [Fact]
    public void Recapture_keeps_entity_and_changes_hp()
    {
        var eng = new SimEngine();
        var stats = new StatsConfig();
        eng.BoardStart(null);
        eng.SpawnZombie(stats, new SimSpawnZombieRequest { Ptr = "Z1", Hp = 270 });
        var recap = eng.Recapture(new SimEntityStatsRequest { Ptr = "Z1", Side = "zombie", Hp = 500, Source = "setHealthInTravel" });
        Assert.Contains(recap.Events, e => e.Kind == "entity.stats");
        Assert.Equal(500, eng.Zombies[0].Hp);
        Assert.Equal("setHealthInTravel", PayloadString(recap.Events[0], "source"));
    }

    [Fact]
    public void Plant_die_includes_reason_name()
    {
        var eng = new SimEngine();
        var stats = new StatsConfig();
        eng.SpawnPlant(stats, new SimSpawnPlantRequest { Ptr = "P1" });
        var die = eng.DiePlant(new SimDieRequest { Ptr = "P1", Reason = 8, ReasonName = "ByShovel" });
        Assert.Equal("ByShovel", PayloadString(die.Events[0], "reasonName"));
    }

    static int PayloadInt(EventEnvelope e, string key)
    {
        var dict = Assert.IsType<Dictionary<string, object>>(e.Payload);
        return Convert.ToInt32(dict[key]);
    }

    static string PayloadString(EventEnvelope e, string key)
    {
        var dict = Assert.IsType<Dictionary<string, object>>(e.Payload);
        return Convert.ToString(dict[key])!;
    }
}
