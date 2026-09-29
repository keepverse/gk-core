using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.11 (spec-delve-quests.md §4) — `QuestReward.Request`: a reward golden and a test that
/// no quest grants an unlock (the todo's own two Verify lines). Reuses `RarityShiftTests.cs`'s own
/// hand-verifiable 5-rung ladder fixture for consistency.</summary>
public class QuestRewardTests
{
    static RarityRung Rung(string id, int ordinal, int weight) => new(id, ordinal, 0, 0, 0, 0, weight);

    static readonly IReadOnlyList<RarityRung> Ladder = new[]
    {
        Rung("staple", 10, 1000),
        Rung("frequent", 20, 300),
        Rung("occasional", 30, 90),
        Rung("seldom", 40, 25),
        Rung("exceptional", 50, 7),
    };

    static readonly IReadOnlyDictionary<string, RewardWindow> RewardBands = new Dictionary<string, RewardWindow>(StringComparer.Ordinal)
    {
        ["modest"] = new RewardWindow("staple", "occasional"),
        ["fair"] = new RewardWindow("frequent", "seldom"),
        ["rich"] = new RewardWindow("occasional", "exceptional"),
    };

    static readonly IReadOnlyDictionary<string, string> LootBinding = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cache"] = "table.forest-cache",
        ["fight"] = "table.forest-fight",
    };

    static QuestRow Row(string id, string rewardBand) => new(id, "kill-boss", null, null, rewardBand, "delve", null);

    // ---- the reward golden ----

    [Fact]
    public void Request_builds_the_exact_source_row_and_correlation_a_reward_golden()
    {
        var quest = Row("quest.slay-the-warden", "fair");
        var result = QuestReward.Request(quest, delveId: 42, LootBinding, RewardBands, Ladder, thetaRun: 83);

        Assert.Equal("dungeon-quest", result.Source.SourceKind);
        Assert.Equal("42:quest:quest.slay-the-warden", result.Source.SourceId);
        Assert.Equal("table.forest-cache", result.Source.TableId); // the domain's own cache binding, not "fight"
        Assert.Equal(83, result.Source.ContentLevel);
        Assert.Equal("loot:delve:42:quest:quest.slay-the-warden", result.CorrelationId);
        Assert.Equal("frequent", result.Window.ComposedFloorRung); // fair's own floorRung, no other floor supplied
        Assert.Equal("seldom", result.Window.CeilRung);
    }

    [Fact]
    public void Request_composes_the_quests_floor_with_an_additional_floor_the_stronger_wins()
    {
        // "fair" floors at frequent (ordinal 20); an additional "occasional" (ordinal 30) floor from
        // elsewhere (a room kind's own floor, say) must win -- ComposeFloor takes the strongest.
        var quest = Row("quest.slay-the-warden", "fair");
        var result = QuestReward.Request(quest, 42, LootBinding, RewardBands, Ladder, 83, additionalFloor: "occasional");
        Assert.Equal("occasional", result.Window.ComposedFloorRung);
    }

    [Fact]
    public void Request_reads_the_domains_own_cache_binding_never_another_kinds_table()
    {
        var quest = Row("quest.gather-herbs", "modest");
        var result = QuestReward.Request(quest, 7, LootBinding, RewardBands, Ladder, 20);
        Assert.Equal(LootBinding["cache"], result.Source.TableId);
        Assert.NotEqual(LootBinding["fight"], result.Source.TableId);
    }

    [Fact]
    public void Request_genuinely_reads_thetaRun_not_a_hardcoded_content_level()
    {
        var quest = Row("quest.x", "modest");
        var atLow = QuestReward.Request(quest, 1, LootBinding, RewardBands, Ladder, thetaRun: 20);
        var atHigh = QuestReward.Request(quest, 1, LootBinding, RewardBands, Ladder, thetaRun: 200);
        Assert.NotEqual(atLow.Source.ContentLevel, atHigh.Source.ContentLevel);
    }

    [Fact]
    public void Request_refuses_a_domain_with_no_cache_binding()
    {
        var quest = Row("quest.x", "modest");
        var noCacheBinding = new Dictionary<string, string>(StringComparer.Ordinal) { ["fight"] = "table.forest-fight" };
        Assert.Throws<ArgumentException>(() => QuestReward.Request(quest, 1, noCacheBinding, RewardBands, Ladder, 20));
    }

    [Fact]
    public void Request_refuses_an_unknown_rewardBand()
    {
        var quest = Row("quest.x", "legendary");
        Assert.Throws<ArgumentException>(() => QuestReward.Request(quest, 1, LootBinding, RewardBands, Ladder, 20));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var quest = Row("quest.x", "modest");
        Assert.Throws<ArgumentNullException>(() => QuestReward.Request(null!, 1, LootBinding, RewardBands, Ladder, 20));
        Assert.Throws<ArgumentNullException>(() => QuestReward.Request(quest, 1, null!, RewardBands, Ladder, 20));
        Assert.Throws<ArgumentNullException>(() => QuestReward.Request(quest, 1, LootBinding, null!, Ladder, 20));
        Assert.Throws<ArgumentNullException>(() => QuestReward.Request(quest, 1, LootBinding, RewardBands, null!, 20));
    }

    // ---- "quests reward, never unlock" -- proven structurally, not assumed ----

    [Fact]
    public void QuestRewardRequest_has_no_field_shaped_like_an_unlock()
    {
        // The load-bearing form of "a quest's only outputs are a QuestVerdict and, at extraction, one
        // LootRequest" (spec §6, verbatim): if an "unlock"/"gate"/"door"/"open" field is ever added to
        // this type, this test starts failing, forcing whoever adds it to justify the change against
        // the spec's own hard boundary rather than slipping it in unnoticed.
        var fieldNames = typeof(QuestRewardRequest).GetProperties().Select(p => p.Name.ToLowerInvariant())
            .Concat(typeof(QuestRewardWindow).GetProperties().Select(p => p.Name.ToLowerInvariant()))
            .Concat(typeof(LootSourceRow).GetProperties().Select(p => p.Name.ToLowerInvariant()))
            .ToList();
        Assert.DoesNotContain(fieldNames, n => n.Contains("unlock") || n.Contains("gate") || n.Contains("door"));
    }
}
