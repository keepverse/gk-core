using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.10 (spec-delve-quests.md §2) — `QuestOffer.Satisfiable`/`Draw`: an unsatisfiable quest
/// is never offered over 256 seeds, and the D14 filter is red below `hard` (the todo's own two Verify
/// lines).</summary>
public class QuestOfferTests
{
    static DelveRoomFact Fact(string sectorId, string kind, string archetypeId, bool isSecret = false) =>
        new(Row: 0, Col: 0, SectorId: sectorId, Kind: kind, ArchetypeId: archetypeId, BaseBand: 0,
            IsSecret: isSecret, SightLanes: 1, ScoutSightLanes: 1, PartyRouteMask: 0, KeyForLaneId: null);

    static readonly IReadOnlyDictionary<string, long> CountBandMilli = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["lone"] = 100, ["few"] = 250, ["several"] = 500, ["many"] = 900,
    };

    static bool IsHighCountBand(string band) => band is "several" or "many"; // this file's own "most/all"-equivalent choice

    static bool NoCurioMatch(string archetypeId, string kind) => false;
    static bool NoLootRole(string role) => false;

    static QuestRow Row(string id, string template, string? targetRef = null, string? countBand = null) =>
        new(id, template, targetRef, countBand, "modest", "delve", null);

    // ---- QuestCounts ----

    [Fact]
    public void RoomsOfKind_explore_rooms_counts_every_non_secret_room()
    {
        var facts = new[] { Fact("r0", "fight", "a"), Fact("r1", "elite", "b", isSecret: true), Fact("r2", "cache", "c") };
        var q = Row("q1", "explore-rooms", countBand: "many");
        Assert.Equal(2, QuestCounts.RoomsOfKind(q, facts, NoCurioMatch));
    }

    [Fact]
    public void RoomsOfKind_cleanse_fights_counts_only_the_target_kind()
    {
        var facts = new[] { Fact("r0", "fight", "a"), Fact("r1", "elite", "b"), Fact("r2", "fight", "c") };
        var q = Row("q1", "cleanse-fights", targetRef: "fight", countBand: "few");
        Assert.Equal(2, QuestCounts.RoomsOfKind(q, facts, NoCurioMatch));
    }

    [Fact]
    public void RoomsOfKind_gather_curio_kind_counts_via_the_callers_own_archetype_lookup()
    {
        var facts = new[] { Fact("r0", "curio", "arch-a"), Fact("r1", "curio", "arch-b") };
        bool HasShrine(string archetypeId, string kind) => archetypeId == "arch-a" && kind == "shrine";
        var q = Row("q1", "gather-curio-kind", targetRef: "shrine", countBand: "few");
        Assert.Equal(1, QuestCounts.RoomsOfKind(q, facts, HasShrine));
    }

    [Fact]
    public void Need_applies_the_specs_own_ceiling_formula()
    {
        // 10 rooms * 250 milli = 2500 / 1000 = 2.5 -> ceil -> 3.
        Assert.Equal(3, QuestCounts.Need(Row("q1", "cleanse-fights", "fight", "few"), roomsOfKind: 10, CountBandMilli));
    }

    [Fact]
    public void Need_floors_at_1_never_0()
    {
        Assert.Equal(1, QuestCounts.Need(Row("q1", "cleanse-fights", "fight", "lone"), roomsOfKind: 1, CountBandMilli));
    }

    [Fact]
    public void Need_is_zero_for_a_count_less_template_never_reads_countBandMilli()
    {
        Assert.Equal(0, QuestCounts.Need(Row("q1", "kill-boss"), roomsOfKind: 999, CountBandMilli));
    }

    [Fact]
    public void Need_throws_on_an_unknown_countBand_member()
    {
        var q = Row("q1", "cleanse-fights", "fight", "not-a-real-band");
        Assert.Throws<ArgumentException>(() => QuestCounts.Need(q, 10, CountBandMilli));
    }

    // ---- Satisfiable ----

    [Fact]
    public void Structural_templates_are_always_satisfiable_over_an_empty_graph()
    {
        var pool = new[] { Row("q1", "kill-boss"), Row("q2", "bring-creature-home-alive"), Row("q3", "finish-under-hunger") };
        var kept = QuestOffer.Satisfiable(pool, Array.Empty<DelveRoomFact>(), NoCurioMatch, NoLootRole, CountBandMilli);
        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void Cleanse_fights_is_unsatisfiable_with_zero_matching_rooms()
    {
        var facts = new[] { Fact("r0", "elite", "a") }; // no "fight" rooms at all
        var pool = new[] { Row("q1", "cleanse-fights", "fight", "lone") };
        var kept = QuestOffer.Satisfiable(pool, facts, NoCurioMatch, NoLootRole, CountBandMilli);
        Assert.Empty(kept);
    }

    [Fact]
    public void Cleanse_fights_is_satisfiable_once_enough_matching_rooms_exist()
    {
        var facts = new[] { Fact("r0", "fight", "a"), Fact("r1", "fight", "b"), Fact("r2", "elite", "c") };
        var pool = new[] { Row("q1", "cleanse-fights", "fight", "lone") }; // need = max(1, ceil(2*100/1000)) = 1
        var kept = QuestOffer.Satisfiable(pool, facts, NoCurioMatch, NoLootRole, CountBandMilli);
        Assert.Single(kept);
    }

    [Fact]
    public void Extract_with_item_kind_reads_the_callers_own_lootBinding_delegate()
    {
        var pool = new[] { Row("q1", "extract-with-item-kind", "weapon") };
        Assert.Empty(QuestOffer.Satisfiable(pool, Array.Empty<DelveRoomFact>(), NoCurioMatch, NoLootRole, CountBandMilli));
        Assert.Single(QuestOffer.Satisfiable(pool, Array.Empty<DelveRoomFact>(), NoCurioMatch, _ => true, CountBandMilli));
    }

    [Fact]
    public void An_unsatisfiable_quest_is_never_offered_over_256_seeds()
    {
        // The literal Verify line: cleanse-fights needs a "fight" room that never exists on this
        // graph, at ANY of the 256 seeds Draw is exercised at -- Satisfiable must exclude it up
        // front, so it can never even enter the weighted draw regardless of the roll.
        var facts = new[] { Fact("r0", "elite", "a"), Fact("r1", "curio", "b") };
        var pool = new[]
        {
            Row("q1", "cleanse-fights", "fight", "lone"), // unsatisfiable -- no "fight" room
            Row("q2", "kill-boss"),
            Row("q3", "bring-creature-home-alive"),
        };
        var templates = ObjectiveTemplateCatalog.All;
        for (long seed = 0; seed < 256; seed++)
        {
            var satisfiable = QuestOffer.Satisfiable(pool, facts, NoCurioMatch, NoLootRole, CountBandMilli);
            Assert.DoesNotContain(satisfiable, q => q.QuestId == "q1");
            var offered = QuestOffer.Draw(satisfiable, templates, seed, offeredAtEntry: 2, rungOrdinal: 9, hardOrdinal: 4, IsHighCountBand);
            Assert.DoesNotContain(offered, q => q.QuestId == "q1");
        }
    }

    // ---- IsRiskQuest ----

    [Theory]
    [InlineData("kill-boss", null, true)]
    [InlineData("cleanse-fights", "lone", true)]
    [InlineData("explore-rooms", "many", true)]
    [InlineData("explore-rooms", "several", true)]
    [InlineData("explore-rooms", "few", false)]
    [InlineData("explore-rooms", "lone", false)]
    [InlineData("bring-creature-home-alive", null, false)]
    public void IsRiskQuest_matches_the_spec_own_set(string template, string? countBand, bool expected)
    {
        var q = Row("q1", template, countBand: countBand);
        Assert.Equal(expected, QuestOffer.IsRiskQuest(q, IsHighCountBand));
    }

    // ---- Draw ----

    [Fact]
    public void Draw_is_deterministic_same_seed_always_the_same_offer()
    {
        var pool = new[] { Row("q1", "kill-boss"), Row("q2", "bring-creature-home-alive"), Row("q3", "finish-under-hunger") };
        var templates = ObjectiveTemplateCatalog.All;
        var a = QuestOffer.Draw(pool, templates, 42, 2, 9, 4, IsHighCountBand);
        var b = QuestOffer.Draw(pool, templates, 42, 2, 9, 4, IsHighCountBand);
        Assert.Equal(a.Select(q => q.QuestId), b.Select(q => q.QuestId));
    }

    [Fact]
    public void Draw_never_offers_the_same_quest_twice_without_replacement()
    {
        var pool = new[] { Row("q1", "kill-boss"), Row("q2", "bring-creature-home-alive") };
        var templates = ObjectiveTemplateCatalog.All;
        for (long seed = 0; seed < 50; seed++)
        {
            var offered = QuestOffer.Draw(pool, templates, seed, 2, 9, 4, IsHighCountBand);
            Assert.Equal(offered.Count, offered.Select(q => q.QuestId).Distinct().Count());
        }
    }

    [Fact]
    public void Draw_never_offers_more_than_offeredAtEntry()
    {
        var pool = Enumerable.Range(0, 5).Select(i => Row($"q{i}", "kill-boss")).ToArray();
        var templates = ObjectiveTemplateCatalog.All;
        var offered = QuestOffer.Draw(pool, templates, 7, offeredAtEntry: 2, 9, 4, IsHighCountBand);
        Assert.True(offered.Count <= 2);
    }

    [Fact]
    public void Draw_stops_gracefully_when_fewer_eligible_quests_remain_than_offeredAtEntry()
    {
        var pool = new[] { Row("q1", "kill-boss") };
        var templates = ObjectiveTemplateCatalog.All;
        var offered = QuestOffer.Draw(pool, templates, 3, offeredAtEntry: 5, 9, 4, IsHighCountBand);
        Assert.Single(offered);
    }

    // ---- the D14 filter is red below hard (the literal Verify line) ----

    [Fact]
    public void Below_hard_an_all_sink_avoidance_pool_with_no_risk_quest_offers_nothing()
    {
        // Every candidate is sink-avoidance; nothing else in the pool can ever supply a risk quest
        // to unlock them; rung is below hard. The D14 filter must exclude every one of them, every
        // slot, over many seeds -- "red" in the sense that the offer never fills.
        var pool = new[] { Row("q1", "finish-under-hunger"), Row("q2", "survive-no-downed"), Row("q3", "spend-no-provision") };
        var templates = ObjectiveTemplateCatalog.All;
        for (long seed = 0; seed < 32; seed++)
        {
            var offered = QuestOffer.Draw(pool, templates, seed, offeredAtEntry: 2, rungOrdinal: 2, hardOrdinal: 4, IsHighCountBand);
            Assert.Empty(offered);
        }
    }

    [Fact]
    public void At_or_above_hard_sink_avoidance_quests_are_eligible_from_the_first_slot()
    {
        var pool = new[] { Row("q1", "finish-under-hunger") };
        var templates = ObjectiveTemplateCatalog.All;
        var offered = QuestOffer.Draw(pool, templates, 1, offeredAtEntry: 1, rungOrdinal: 4, hardOrdinal: 4, IsHighCountBand);
        Assert.Single(offered);
    }

    [Fact]
    public void Below_hard_a_sink_avoidance_quest_unlocks_once_a_risk_quest_is_drawn_first()
    {
        // kill-boss (a risk quest) plus a sink-avoidance quest, rung below hard, offeredAtEntry 2:
        // the sink quest must never be the FIRST one offered (non-sink draws first), but over enough
        // seeds it IS eventually offered second, once kill-boss has already landed.
        var pool = new[] { Row("q1", "kill-boss"), Row("q2", "finish-under-hunger") };
        var templates = ObjectiveTemplateCatalog.All;
        var sinkEverOffered = false;
        for (long seed = 0; seed < 64; seed++)
        {
            var offered = QuestOffer.Draw(pool, templates, seed, offeredAtEntry: 2, rungOrdinal: 2, hardOrdinal: 4, IsHighCountBand);
            if (offered.Count > 0) Assert.Equal("q1", offered[0].QuestId); // non-sink (the only risk quest) always drawn first
            if (offered.Any(q => q.QuestId == "q2")) sinkEverOffered = true;
        }
        Assert.True(sinkEverOffered, "the sink-avoidance quest should unlock in slot 2 once kill-boss lands in slot 1, over 64 seeds");
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var templates = ObjectiveTemplateCatalog.All;
        Assert.Throws<ArgumentNullException>(() => QuestOffer.Satisfiable(null!, Array.Empty<DelveRoomFact>(), NoCurioMatch, NoLootRole, CountBandMilli));
        Assert.Throws<ArgumentNullException>(() => QuestOffer.Draw(null!, templates, 1, 2, 9, 4, IsHighCountBand));
        Assert.Throws<ArgumentNullException>(() => QuestOffer.Draw(Array.Empty<QuestRow>(), null!, 1, 2, 9, 4, IsHighCountBand));
    }
}
