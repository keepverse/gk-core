using FusionRpg.Core.Achievements;
using Xunit;

namespace FusionRpg.Core.Tests;

// T1: tuning loader — valid doc parses, missing keys reject naming the key.
public class AchievementTitlesTuningTests
{
    const string Valid = """
        {
          "kind": "achievement-titles",
          "version": 1,
          "hallSlots": 3,
          "tierNeedCounts": [10, 50, 200],
          "poolWeightsMilli": { "bronze": 600 },
          "equipShareMilli": { "default": 150 },
          "upkeepShareMilli": { "default": 50 },
          "stackRule": "additive-in-family-one-per-group",
          "validTurns": { "default": 20 },
          "titleRitualPrice": { "souls": 500, "essence": 50 },
          "seasonTurnWindows": { "default": 100 },
          "hiddenShareCapMilli": 312,
          "wornRule": "highestTier",
          "hallYieldCapMilli": 1000,
          "hallUpkeepCapMilli": 1000
        }
        """;

    [Fact]
    public void Valid_doc_parses()
    {
        var t = AchievementTitlesTuningLoader.Parse(Valid);
        Assert.Equal(1, t.Version);
        Assert.Equal(3, t.HallSlots);
        Assert.Equal(new[] { 10, 50, 200 }, t.TierNeedCounts);
        Assert.Equal(500, t.RitualSouls);
    }

    [Theory]
    [InlineData("hallSlots")]
    [InlineData("tierNeedCounts")]
    [InlineData("poolWeightsMilli")]
    [InlineData("equipShareMilli")]
    [InlineData("upkeepShareMilli")]
    [InlineData("stackRule")]
    [InlineData("validTurns")]
    [InlineData("titleRitualPrice")]
    [InlineData("seasonTurnWindows")]
    [InlineData("hiddenShareCapMilli")]
    [InlineData("wornRule")]
    [InlineData("hallYieldCapMilli")]
    [InlineData("hallUpkeepCapMilli")]
    public void Missing_key_rejects_naming_it(string key)
    {
        var doc = Valid.Replace($"\"{key}\":", "\"removedKey\":");
        var ex = Assert.Throws<RegistryLoadException>(
            () => AchievementTitlesTuningLoader.Parse(doc));
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void Malformed_json_rejects()
    {
        var ex = Assert.Throws<RegistryLoadException>(
            () => AchievementTitlesTuningLoader.Parse("{oops"));
        Assert.Contains("not valid JSON", ex.Message);
    }
}
