using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Delve.Report;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.11 (spec-delve-quests.md §3) — `QuestProgress.Evaluate`: pure and idempotent on
/// `(quest, need, report)`, one arm per real template (the spec's own worked pseudocode).</summary>
public class QuestProgressTests
{
    const string HungerExhausted = "status.hunger-exhausted"; // this test's own fixture value -- Evaluate takes it as a plain parameter, never a hardcoded literal

    static DelveReport EmptyReport() => new(
        Array.Empty<DelveReportRoom>(), Array.Empty<DelveReportKill>(), Array.Empty<DelveReportEvent>(),
        Array.Empty<DelveReportDecision>(), Array.Empty<DelveReportMember>(), Array.Empty<DelveReportHaul>());

    static QuestRow Row(string id, string template, string? targetRef = null) =>
        new(id, template, targetRef, null, "modest", "delve", null);

    // ---- explore-rooms ----

    [Fact]
    public void Explore_rooms_counts_visited_non_secret_rooms_against_need()
    {
        var report = EmptyReport() with
        {
            Rooms = new[]
            {
                new DelveReportRoom(0, 0, "fight", Visited: true, Cleared: true, IsSecret: false, EventId: null),
                new DelveReportRoom(0, 1, "elite", Visited: true, Cleared: false, IsSecret: true, EventId: null), // secret -- never counts
                new DelveReportRoom(1, 0, "cache", Visited: false, Cleared: false, IsSecret: false, EventId: null), // not visited
            },
        };
        var verdict = QuestProgress.Evaluate(Row("q1", "explore-rooms"), need: 2, report, HungerExhausted);
        Assert.Equal(1, verdict.Have);
        Assert.False(verdict.Done);
    }

    // ---- cleanse-fights ----

    [Fact]
    public void Cleanse_fights_counts_cleared_rooms_of_the_target_kind()
    {
        var report = EmptyReport() with
        {
            Rooms = new[]
            {
                new DelveReportRoom(0, 0, "fight", Visited: true, Cleared: true, IsSecret: false, EventId: null),
                new DelveReportRoom(0, 1, "fight", Visited: true, Cleared: false, IsSecret: false, EventId: null), // not cleared
                new DelveReportRoom(1, 0, "elite", Visited: true, Cleared: true, IsSecret: false, EventId: null), // wrong kind
            },
        };
        var verdict = QuestProgress.Evaluate(Row("q1", "cleanse-fights", "fight"), need: 1, report, HungerExhausted);
        Assert.Equal(1, verdict.Have);
        Assert.True(verdict.Done);
    }

    // ---- gather-curio-kind ----

    [Fact]
    public void Gather_curio_kind_counts_events_of_the_target_kind_not_left_and_not_nothing()
    {
        var report = EmptyReport() with
        {
            Events = new[]
            {
                new DelveReportEvent("r0", "ev1", "shrine", "good", "pray"),
                new DelveReportEvent("r1", "ev2", "shrine", "nothing", "loot"), // outcome nothing -- excluded
                new DelveReportEvent("r2", "ev3", "shrine", "good", "leave"), // choice leave -- excluded
                new DelveReportEvent("r3", "ev4", "trap", "good", "disarm"), // wrong kind
            },
        };
        var verdict = QuestProgress.Evaluate(Row("q1", "gather-curio-kind", "shrine"), need: 1, report, HungerExhausted);
        Assert.Equal(1, verdict.Have);
        Assert.True(verdict.Done);
    }

    // ---- kill-boss ----

    [Fact]
    public void Kill_boss_is_done_only_when_a_boss_role_kill_is_recorded()
    {
        var withBoss = EmptyReport() with { Kills = new[] { new DelveReportKill("r5", "species.a", "boss") } };
        var withoutBoss = EmptyReport() with { Kills = new[] { new DelveReportKill("r5", "species.a", "elite") } };

        Assert.True(QuestProgress.Evaluate(Row("q1", "kill-boss"), 0, withBoss, HungerExhausted).Done);
        Assert.False(QuestProgress.Evaluate(Row("q1", "kill-boss"), 0, withoutBoss, HungerExhausted).Done);
    }

    // ---- extract-with-item-kind ----

    [Fact]
    public void Extract_with_item_kind_is_done_when_the_haul_holds_the_role()
    {
        var report = EmptyReport() with { Haul = new[] { new DelveReportHaul(0, "item.sword", "weapon") } };
        Assert.True(QuestProgress.Evaluate(Row("q1", "extract-with-item-kind", "weapon"), 0, report, HungerExhausted).Done);
        Assert.False(QuestProgress.Evaluate(Row("q1", "extract-with-item-kind", "armor"), 0, report, HungerExhausted).Done);
    }

    // ---- bring-creature-home-alive ----

    [Fact]
    public void Bring_creature_home_alive_requires_every_member_standing_at_extraction()
    {
        var allAlive = EmptyReport() with { Members = new[] { Member(downed: false), Member(downed: false) } };
        var oneDowned = EmptyReport() with { Members = new[] { Member(downed: false), Member(downed: true) } };

        Assert.True(QuestProgress.Evaluate(Row("q1", "bring-creature-home-alive"), 0, allAlive, HungerExhausted).Done);
        Assert.False(QuestProgress.Evaluate(Row("q1", "bring-creature-home-alive"), 0, oneDowned, HungerExhausted).Done);
    }

    // ---- finish-under-hunger ----

    [Fact]
    public void Finish_under_hunger_requires_no_member_carrying_the_exhaustion_status()
    {
        var clean = EmptyReport() with { Members = new[] { Member(statuses: new[] { "status.blessed" }) } };
        var exhausted = EmptyReport() with { Members = new[] { Member(statuses: new[] { HungerExhausted }) } };

        Assert.True(QuestProgress.Evaluate(Row("q1", "finish-under-hunger"), 0, clean, HungerExhausted).Done);
        Assert.False(QuestProgress.Evaluate(Row("q1", "finish-under-hunger"), 0, exhausted, HungerExhausted).Done);
    }

    // ---- survive-no-downed ----

    [Fact]
    public void Survive_no_downed_is_strictly_harder_than_bring_home_alive_never_downed_even_once()
    {
        // Revived-then-standing still fails: DownedOnce, not Downed, is what this template reads.
        var revived = EmptyReport() with { Members = new[] { Member(downed: false, downedOnce: true) } };
        Assert.False(QuestProgress.Evaluate(Row("q1", "survive-no-downed"), 0, revived, HungerExhausted).Done);
        Assert.True(QuestProgress.Evaluate(Row("q1", "bring-creature-home-alive"), 0, revived, HungerExhausted).Done);
    }

    // ---- spend-no-provision ----

    [Fact]
    public void Spend_no_provision_fails_only_on_a_pack_drop_by_use()
    {
        var clean = EmptyReport() with { Decisions = new[] { new DelveReportDecision("route", "player") } };
        var spent = EmptyReport() with { Decisions = new[] { new DelveReportDecision("pack.drop", "use") } };
        var droppedNotUsed = EmptyReport() with { Decisions = new[] { new DelveReportDecision("pack.drop", "player") } };

        Assert.True(QuestProgress.Evaluate(Row("q1", "spend-no-provision"), 0, clean, HungerExhausted).Done);
        Assert.False(QuestProgress.Evaluate(Row("q1", "spend-no-provision"), 0, spent, HungerExhausted).Done);
        Assert.True(QuestProgress.Evaluate(Row("q1", "spend-no-provision"), 0, droppedNotUsed, HungerExhausted).Done);
    }

    // ---- an unknown template throws, never silently false (registry != code: loud) ----

    [Fact]
    public void An_unregistered_template_throws_rather_than_silently_returning_false()
    {
        Assert.Throws<ArgumentException>(() => QuestProgress.Evaluate(Row("q1", "not-a-real-template"), 0, EmptyReport(), HungerExhausted));
    }

    // ---- the predicate gate ----

    [Fact]
    public void A_failing_predicateHolds_delegate_forces_Done_false_even_when_the_template_is_satisfied()
    {
        var withBoss = EmptyReport() with { Kills = new[] { new DelveReportKill("r5", "species.a", "boss") } };
        var verdict = QuestProgress.Evaluate(Row("q1", "kill-boss"), 0, withBoss, HungerExhausted, predicateHolds: () => false);
        Assert.False(verdict.Done);
    }

    [Fact]
    public void A_null_predicateHolds_delegate_means_no_predicate_never_blocks_completion()
    {
        var withBoss = EmptyReport() with { Kills = new[] { new DelveReportKill("r5", "species.a", "boss") } };
        var verdict = QuestProgress.Evaluate(Row("q1", "kill-boss"), 0, withBoss, HungerExhausted, predicateHolds: null);
        Assert.True(verdict.Done);
    }

    // ---- pure and idempotent (the literal Verify line) ----

    [Fact]
    public void Evaluating_twice_yields_one_identical_verdict()
    {
        var report = EmptyReport() with { Kills = new[] { new DelveReportKill("r5", "species.a", "boss") } };
        var quest = Row("q1", "kill-boss");
        var first = QuestProgress.Evaluate(quest, 0, report, HungerExhausted);
        var second = QuestProgress.Evaluate(quest, 0, report, HungerExhausted);
        Assert.Equal(first, second);
    }

    [Fact]
    public void An_impossible_quest_reads_Done_false_never_throws_or_disappears()
    {
        var verdict = QuestProgress.Evaluate(Row("q1", "cleanse-fights", "fight"), need: 999, EmptyReport(), HungerExhausted);
        Assert.False(verdict.Done);
        Assert.Equal("q1", verdict.QuestId);
        Assert.Equal(999, verdict.Need);
    }

    static DelveReportMember Member(bool downed = false, bool downedOnce = false, IReadOnlyList<string>? statuses = null) =>
        new(0, "creature.instance-1", downed, downedOnce, statuses ?? Array.Empty<string>());
}
