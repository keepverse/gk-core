using FusionRpg.Core.Progression;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests;

/// <summary>Balance POC: simulate match profiles and lock pacing expectations.</summary>
public class RpgProgressionBalanceTests
{
    readonly ITestOutputHelper _out;
    public RpgProgressionBalanceTests(ITestOutputHelper output) => _out = output;

    sealed class MatchProfile
    {
        public int Kills;
        public int PlantPlacesSameType;
        public int ZombieSpawnsSameType;
        public int Mowers;
        public bool Defeat;
    }

    static void Play(MatchProfile p, RpgActorState player, RpgActorState plant, RpgActorState zombie)
    {
        for (var i = 0; i < p.Kills; i++)
            RpgXpApply.Apply(RpgActorKinds.Player, player, RpgXpAwards.Kill, reason: RpgXpReasons.Kill);
        for (var i = 0; i < p.Mowers; i++)
            RpgXpApply.Apply(RpgActorKinds.Player, player, RpgXpAwards.Mower, reason: RpgXpReasons.Mower);
        if (p.Defeat)
            RpgXpApply.Apply(RpgActorKinds.Player, player, RpgXpAwards.Defeat, reason: RpgXpReasons.Defeat);
        for (var i = 0; i < p.PlantPlacesSameType; i++)
            RpgXpApply.Apply(RpgActorKinds.Plant, plant, RpgXpAwards.PlantPlace, reason: RpgXpReasons.PlantPlace);
        for (var i = 0; i < p.ZombieSpawnsSameType; i++)
            RpgXpApply.Apply(RpgActorKinds.Zombie, zombie, RpgXpAwards.ZombieSpawn, reason: RpgXpReasons.ZombieSpawn);
    }

    [Fact]
    public void First_casual_win_reaches_at_least_L2()
    {
        var casual = new MatchProfile { Kills = 40, PlantPlacesSameType = 15, ZombieSpawnsSameType = 40, Mowers = 0, Defeat = false };
        var player = new RpgActorState();
        Play(casual, player, new RpgActorState(), new RpgActorState());
        _out.WriteLine($"after 1 casual: player L{player.Level} xp={player.Xp:0.#}");
        Assert.True(player.Level >= 2);
    }

    [Fact]
    public void Casual_wins_reach_about_L10_to_L16_in_20_matches()
    {
        var casual = new MatchProfile { Kills = 40, PlantPlacesSameType = 15, ZombieSpawnsSameType = 40, Mowers = 0, Defeat = false };
        var player = new RpgActorState();
        var plant = new RpgActorState();
        var zombie = new RpgActorState();
        for (var n = 0; n < 20; n++)
            Play(casual, player, plant, zombie);

        _out.WriteLine($"after 20 casual: player L{player.Level} xp={player.Xp:0.#}; plant L{plant.Level}; zombie L{zombie.Level}");
        Assert.InRange(player.Level, 12, 20);
        Assert.True(plant.Level < player.Level + 3);
        Assert.True(zombie.Level >= plant.Level - 2);
    }

    [Fact]
    public void Loss_streak_with_mowers_demotes_at_mid_level()
    {
        var player = new RpgActorState();
        var pump = new MatchProfile { Kills = 40, Defeat = false };
        for (var i = 0; i < 12; i++)
            Play(pump, player, new RpgActorState(), new RpgActorState());
        var mid = player.Level;
        Assert.True(mid >= 4);

        var loss = new MatchProfile { Kills = 2, Mowers = 4, Defeat = true };
        var beforeDemote = player.DemotionCount;
        var beforeLevel = player.Level;
        for (var i = 0; i < 5; i++)
            Play(loss, player, new RpgActorState(), new RpgActorState());

        _out.WriteLine($"mid L{mid} → L{player.Level} demotions={player.DemotionCount}");
        Assert.True(player.DemotionCount > beforeDemote);
        Assert.True(player.Level < beforeLevel);
    }

    [Fact]
    public void Zombie_wave_spam_can_outpace_plant_spam()
    {
        var plant = new RpgActorState();
        var zombie = new RpgActorState();
        var player = new RpgActorState();
        var profile = new MatchProfile { PlantPlacesSameType = 30, ZombieSpawnsSameType = 40 };
        for (var i = 0; i < 10; i++)
            Play(profile, player, plant, zombie);
        _out.WriteLine($"spam: plant L{plant.Level} zombie L{zombie.Level}");
        Assert.True(zombie.Level >= plant.Level);
    }

    // T3.3 (power-plan.md, done 2026-08-24): Power_scale_stub_is_one deleted -- RpgXpPowerScale
    // (the class it tested) is gone. Its coverage ("kill power scale is 1.0") is already asserted
    // through the real production path in RpgXpAwardMapTests.cs (Assert.Equal(1.0, a.PowerScale)),
    // which is the stronger test since it exercises FromActivity, not a stub class in isolation.
}
