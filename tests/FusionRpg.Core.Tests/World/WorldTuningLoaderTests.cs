using System.IO;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-map W48: `seasons.*Milli` arrays are read by index (`TurnCalendar.SeasonOf(turn) % Count`)
/// — found while wiring the first real reader (`LoamUpkeep.BreakdownFor`) that a mismatched array
/// length was not yet a loader-time rejection, only a future `IndexOutOfRangeException` waiting for
/// whichever turn landed on the missing entry. This is that gap closed at the boot-time gate instead.
/// </summary>
public class WorldTuningLoaderTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FusionRpg.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    static string ValidMovementJson() =>
        // Closed vocabulary: the 7 movement keys the loader owns (dowse + 6 act-price-table rows).
        // Pinned literally because the loader rejects any missing member by name (T5).
        "\"dowseBudgetMilli\": 250, \"claimCostMilli\": 250, \"depositCostMilli\": 100, " +
        "\"withdrawCostMilli\": 100, \"loadCostMilli\": 50, \"unloadCostMilli\": 50, " +
        "\"holdAllowanceMilli\": 250";

    static string ValidDocument(string movementInner) =>
        "{" +
        "\"schemaVersion\": 1, \"version\": 1," +
        "\"laneCostMultiplierMilli\": {}, \"worldSizeNodes\": {}, \"strengthBands\": []," +
        "\"calendar\": { \"daysPerWeek\": 7, \"weeksPerMonth\": 4, \"specialWeekChanceMilli\": 0, \"specialMonthChanceMilli\": 0, \"plagueChanceMilli\": 0 }," +
        "\"movement\": { " + movementInner + " }," +
        "\"growth\": { \"seatPulsePerWeek\": 0, \"lairMultiplierMilli\": 1000, \"specialWeekMultiplierMilli\": 1000, \"raiseCostPoints\": 0, \"raiseMemberHp\": 110, \"legionTarget\": { \"min\": 6, \"max\": 10, \"byTurn\": 40 } }," +
        "\"seasons\": { \"count\": 4, \"monthsPerSeason\": 3, \"yieldMilli\": [1000,1000,1000,1000], \"upkeepMilli\": [1000,1000,1000,1000], \"movementMilli\": [1000,1000,1000,1000] }" +
        "}";

    [Fact]
    public void The_real_shipped_world_tuning_file_parses_with_matching_season_array_lengths()
    {
        var path = Path.Combine(RepoRoot(), "data", "tuning", "world.v6.json");
        var tuning = WorldTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(tuning.Seasons.Count, tuning.Seasons.YieldMilli.Count);
        Assert.Equal(tuning.Seasons.Count, tuning.Seasons.UpkeepMilli.Count);
        Assert.Equal(tuning.Seasons.Count, tuning.Seasons.MovementMilli.Count);
    }

    [Fact]
    public void A_seasons_upkeepMilli_array_shorter_than_count_is_rejected_at_load_not_at_first_read()
    {
        var json = "{" +
            "\"schemaVersion\": 1, \"version\": 1," +
            "\"laneCostMultiplierMilli\": {}, \"worldSizeNodes\": {}, \"strengthBands\": []," +
            "\"placeholderBattle\": { \"defenderBonusMilli\": 0, \"wipeoutRatioMilli\": 0, \"routWoundMilli\": 0, \"guardWoundMilli\": 0 }," +
            "\"calendar\": { \"daysPerWeek\": 7, \"weeksPerMonth\": 4, \"specialWeekChanceMilli\": 0, \"specialMonthChanceMilli\": 0, \"plagueChanceMilli\": 0 }," +
            "\"movement\": { " + ValidMovementJson() + " }," +
            "\"growth\": { \"seatPulsePerWeek\": 0, \"lairMultiplierMilli\": 1000, \"specialWeekMultiplierMilli\": 1000, \"raiseCostPoints\": 0, \"raiseMemberHp\": 110, \"legionTarget\": { \"min\": 6, \"max\": 10, \"byTurn\": 40 } }," +
            "\"seasons\": { \"count\": 4, \"monthsPerSeason\": 3, \"yieldMilli\": [1000,1000,1000,1000], \"upkeepMilli\": [1000,1000,1000], \"movementMilli\": [1000,1000,1000,1000] }" +
            "}";

        var ex = Assert.Throws<WorldTuningRejection>(() => WorldTuningLoader.Parse(json));
        Assert.Contains("seasons.upkeepMilli", ex.Message);
    }

    [Theory]
    [InlineData("claimCostMilli")]
    [InlineData("depositCostMilli")]
    [InlineData("withdrawCostMilli")]
    [InlineData("loadCostMilli")]
    [InlineData("unloadCostMilli")]
    [InlineData("holdAllowanceMilli")]
    public void Missing_movement_key_rejects_naming_it(string missingKey)
    {
        var parts = new System.Collections.Generic.List<string>
        {
            "\"dowseBudgetMilli\": 250",
            "\"claimCostMilli\": 250",
            "\"depositCostMilli\": 100",
            "\"withdrawCostMilli\": 100",
            "\"loadCostMilli\": 50",
            "\"unloadCostMilli\": 50",
            "\"holdAllowanceMilli\": 250",
        };
        parts.RemoveAll(p => p.Contains($"\"{missingKey}\""));
        var json = ValidDocument(string.Join(", ", parts));

        var ex = Assert.Throws<WorldTuningRejection>(() => WorldTuningLoader.Parse(json));
        Assert.Contains($"movement.{missingKey}", ex.Message);
    }

    [Theory]
    [InlineData("claimCostMilli", "\"quarter-turn\"")]
    [InlineData("depositCostMilli", "\"a-tenth\"")]
    [InlineData("withdrawCostMilli", "\"a-tenth\"")]
    [InlineData("loadCostMilli", "\"cheap\"")]
    [InlineData("unloadCostMilli", "\"cheap\"")]
    [InlineData("holdAllowanceMilli", "\"allowance\"")]
    public void String_movement_value_rejects(string key, string rawValue)
    {
        var json = ValidDocument(MovementWithRaw(key, rawValue));
        var ex = Assert.Throws<WorldTuningRejection>(() => WorldTuningLoader.Parse(json));
        Assert.Contains($"movement.{key}", ex.Message);
    }

    [Theory]
    [InlineData("claimCostMilli")]
    [InlineData("depositCostMilli")]
    [InlineData("withdrawCostMilli")]
    [InlineData("loadCostMilli")]
    [InlineData("unloadCostMilli")]
    [InlineData("holdAllowanceMilli")]
    public void Float_movement_value_rejects(string key)
    {
        var json = ValidDocument(MovementWithRaw(key, "12.5"));
        var ex = Assert.Throws<WorldTuningRejection>(() => WorldTuningLoader.Parse(json));
        Assert.Contains($"movement.{key}", ex.Message);
    }

    [Theory]
    [InlineData("claimCostMilli")]
    [InlineData("depositCostMilli")]
    [InlineData("withdrawCostMilli")]
    [InlineData("loadCostMilli")]
    [InlineData("unloadCostMilli")]
    [InlineData("holdAllowanceMilli")]
    public void Negative_movement_value_rejects_at_load_not_at_debit(string key)
    {
        var json = ValidDocument(MovementWithRaw(key, "-1"));
        var ex = Assert.Throws<WorldTuningRejection>(() => WorldTuningLoader.Parse(json));
        Assert.Contains($"movement.{key}", ex.Message);
    }

    static string MovementWithRaw(string targetKey, string rawValue)
    {
        string Cell(string key, string def) =>
            $"\"{key}\": {(key == targetKey ? rawValue : def)}";
        return string.Join(", ", new[]
        {
            Cell("dowseBudgetMilli", "250"),
            Cell("claimCostMilli", "250"),
            Cell("depositCostMilli", "100"),
            Cell("withdrawCostMilli", "100"),
            Cell("loadCostMilli", "50"),
            Cell("unloadCostMilli", "50"),
            Cell("holdAllowanceMilli", "250"),
        });
    }

    [Fact]
    public void Shipped_v6_parses_to_provisional_values_through_the_hub()
    {
        var path = Path.Combine(RepoRoot(), "data", "tuning", "world.v6.json");
        var tuning = WorldTuningLoader.Parse(File.ReadAllText(path));
        WorldTuningHub.Configure(tuning);
        var mv = WorldTuningHub.Tuning.Movement;

        Assert.Equal(250, mv.DowseBudgetMilli);
        Assert.Equal(250, mv.ClaimCostMilli);
        Assert.Equal(100, mv.DepositCostMilli);
        Assert.Equal(100, mv.WithdrawCostMilli);
        Assert.Equal(50, mv.LoadCostMilli);
        Assert.Equal(50, mv.UnloadCostMilli);
        Assert.Equal(250, mv.HoldAllowanceMilli);
    }

    [Fact]
    public void Reserved_clear_and_sustain_keys_are_not_required_nor_read()
    {
        // Reserved names (spec-act-price-table Design 3): no rows ship, loader has no arms for them.
        // A document without them parses; a document carrying them as extra JSON is ignored, never read.
        var without = WorldTuningLoader.Parse(ValidDocument(ValidMovementJson()));
        var withReserved = WorldTuningLoader.Parse(ValidDocument(
            ValidMovementJson() + ", \"clearCostMilli\": 999, \"sustainCostMilli\": 999, \"buildCostMilli\": 999"));

        Assert.Equal(without.Movement.ClaimCostMilli, withReserved.Movement.ClaimCostMilli);
        Assert.Equal(250, withReserved.Movement.ClaimCostMilli);
        // No Clear/Sustain/Build surface on the record itself — the compiler proves it, this pins the
        // closed vocabulary so a half-present key cannot become a boot failure later.
        Assert.False(typeof(MovementTuning).GetProperty("ClearCostMilli") != null);
        Assert.False(typeof(MovementTuning).GetProperty("SustainCostMilli") != null);
        Assert.False(typeof(MovementTuning).GetProperty("BuildCostMilli") != null);
    }

}
