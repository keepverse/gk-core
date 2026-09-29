using FusionRpg.Contracts;
using FusionRpg.Core;
using Xunit;

namespace FusionRpg.Core.Tests;

/// <summary>
/// Regression: same entity ptr across matches must not reuse StatSystem baselines
/// (otherwise second-match HP sticks at the first baseline, e.g. 270 instead of 2700).
/// </summary>
public class SimEngineMatchIsolationTests
{
    [Fact]
    public void BoardStart_clears_baselines_so_second_match_uses_new_hp()
    {
        var eng = new SimEngine();
        var stats = new StatsConfig { ApplyStats = true, Zombies = { HpPercent = 1f } };

        eng.BoardStart(new SimBoardStartRequest { LevelName = "A" });
        var first = eng.SpawnZombie(stats, new SimSpawnZombieRequest
        {
            Ptr = "Z1",
            Type = 0,
            Hp = 270,
            MaxHp = 270
        });
        Assert.False(first.Skipped);
        Assert.Equal(270, eng.Zombies[0].Hp);

        eng.BoardEnd(new SimBoardEndRequest { Summary = new Dictionary<string, object>() });

        eng.BoardStart(new SimBoardStartRequest { LevelName = "B" });
        var second = eng.SpawnZombie(stats, new SimSpawnZombieRequest
        {
            Ptr = "Z1",
            Type = 0,
            Hp = 2700,
            MaxHp = 2700
        });
        Assert.False(second.Skipped);
        Assert.Single(eng.Zombies);
        Assert.Equal(2700, eng.Zombies[0].Hp);
        Assert.Equal(2700, eng.Zombies[0].MaxHp);
        Assert.True(eng.Stats.TryGetBaseline("Z1", out var baseline));
        Assert.Equal(2700, baseline.Hp);
    }
}
