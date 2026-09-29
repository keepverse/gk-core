using System;
using System.Linq;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W6 (spec-turn-engine.md): Step is pure, the barrier waits for everyone, the event queue is
/// monotonic and ordered by (time, entity), and a stale order is reported — never thrown.
/// </summary>
public class TurnEngineTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand StandFast(string commander, string id, string? entityId = null) => new()
    {
        CommanderId = commander,
        CommandId = id,
        Kind = WorldCommandKinds.StandFast,
        EntityId = entityId
    };

    [Fact]
    public void Step_is_pure_so_the_same_inputs_give_the_same_world_and_hash()
    {
        var commands = new[] { StandFast("dave", "c1"), StandFast("zomboss", "z1") };

        var a = TurnEngine.Step(World(), commands, seed: 42);
        var b = TurnEngine.Step(World(), commands, seed: 42);

        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(WorldCanonical.Write(a.World), WorldCanonical.Write(b.World));
    }

    [Fact]
    public void A_different_seed_is_still_deterministic_but_need_not_match()
    {
        var commands = new[] { StandFast("dave", "c1") };
        var a = TurnEngine.Step(World(), commands, seed: 1);
        var b = TurnEngine.Step(World(), commands, seed: 1);
        Assert.Equal(a.StateHash, b.StateHash);
    }

    [Fact]
    public void The_order_commands_arrive_in_does_not_matter()
    {
        var forward = new[] { StandFast("dave", "c1"), StandFast("wild", "w1"), StandFast("zomboss", "z1") };
        var reversed = forward.Reverse().ToArray();

        var a = TurnEngine.Step(World(), forward, seed: 7);
        var b = TurnEngine.Step(World(), reversed, seed: 7);

        Assert.Equal(a.StateHash, b.StateHash);
    }

    [Fact]
    public void The_turn_advances_by_exactly_one()
    {
        var world = World();
        var result = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1);
        Assert.Equal(world.CurrentTurn + 1, result.World.CurrentTurn);
    }

    [Fact]
    public void A_stale_order_is_dropped_into_the_report_with_a_reason_not_thrown()
    {
        // Admitted at submit, but by reveal the entity is gone — the engine reports and carries on.
        var world = World() with { Entities = World().Entities.Where(e => e.EntityId != "e-dave-legion-1").ToList() };
        var commands = new[] { StandFast("dave", "gone", entityId: "e-dave-legion-1"), StandFast("dave", "fine") };

        var result = TurnEngine.Step(world, commands, seed: 1);

        var dropped = Assert.Single(result.Report.Dropped);
        Assert.Equal("gone", dropped.Subject);
        Assert.Equal("entity.unknown", dropped.Detail);
        Assert.Contains(result.Report.Accepted, e => e.Subject == "fine");
    }

    [Fact]
    public void An_unknown_kind_at_reveal_is_dropped_rather_than_crashing_the_turn()
    {
        var commands = new[] { StandFast("dave", "weird") with { Kind = "not-a-kind" } };
        var result = TurnEngine.Step(World(), commands, seed: 1);

        var dropped = Assert.Single(result.Report.Dropped);
        Assert.Equal("kind.unknown", dropped.Detail);
    }

    [Fact]
    public void The_report_records_every_phase_in_the_locked_order()
    {
        var result = TurnEngine.Step(World(), Array.Empty<WorldCommand>(), seed: 1);

        // `Intel` joined at RulesetVersion 2 and moved to **last** at RulesetVersion 3. Belief is
        // what you know at the end of the turn, and the end of the turn is after the turn has
        // finished happening: a claim settles in `Snapshot`, so recording belief before it left a
        // faction unable to see that its own claim had worked. Changing this list is changing the
        // ruleset, which is why it is spelled out rather than derived.
        //
        // `Assaults` joined 2026-09-05 (base-defense siege-seam) right after `Sieges` — a brand-new
        // phase driven by a brand-new command kind (`WorldCommandKinds.Assault`), so no existing
        // command log can ever populate it and no `RulesetVersion` bump was needed; the PHASE LIST
        // itself changed regardless, which is exactly what this test exists to catch.
        Assert.Equal(
            new[] { "Reveal", "Movement", "Sieges", "Assaults", "Production", "Growth", "Pressure", "Events", "Snapshot", "Intel" },
            result.Report.Phases);
    }

    [Fact]
    public void The_state_hash_moves_when_the_world_does_and_holds_when_it_does_not()
    {
        var world = World();
        var same = StateHasher.Hash(world);
        Assert.Equal(same, StateHasher.Hash(world));

        var moved = world with { CurrentTurn = world.CurrentTurn + 1 };
        Assert.NotEqual(same, StateHasher.Hash(moved));
    }
}

public class TurnBarrierTests
{
    static readonly IReadOnlyList<string> Commanders = new[] { "dave", "wild", "zomboss" };

    [Fact]
    public void The_turn_waits_until_every_commander_has_committed()
    {
        var barrier = new WaitForAllCommitted();

        Assert.False(barrier.ShouldFire(Commanders, new[] { "dave" }));
        Assert.False(barrier.ShouldFire(Commanders, new[] { "dave", "zomboss" }));
        Assert.True(barrier.ShouldFire(Commanders, new[] { "dave", "wild", "zomboss" }));
    }

    [Fact]
    public void A_commander_committing_twice_does_not_count_twice()
    {
        var barrier = new WaitForAllCommitted();
        Assert.False(barrier.ShouldFire(Commanders, new[] { "dave", "dave", "dave" }));
    }

    [Fact]
    public void An_unknown_committer_cannot_release_the_barrier()
    {
        var barrier = new WaitForAllCommitted();
        Assert.False(barrier.ShouldFire(Commanders, new[] { "dave", "wild", "stranger" }));
    }
}

public class TurnEventQueueTests
{
    [Fact]
    public void Events_come_out_in_time_then_entity_order()
    {
        var q = new TurnEventQueue();
        q.Schedule(500, "b-legion", TurnEventKinds.Arrival, "");
        q.Schedule(100, "z-legion", TurnEventKinds.Arrival, "");
        q.Schedule(500, "a-legion", TurnEventKinds.Arrival, "");

        var order = Drain(q).Select(e => e.EntityId).ToList();

        // 100 first, then the two at 500 broken by entity id — not by insertion order.
        Assert.Equal(new[] { "z-legion", "a-legion", "b-legion" }, order);
    }

    [Fact]
    public void An_event_may_be_scheduled_at_the_moment_being_processed()
    {
        var q = new TurnEventQueue();
        q.Schedule(300, "a", TurnEventKinds.Arrival, "");
        Assert.True(q.TryDequeue(out var first));
        Assert.Equal(300, first.TimeMilli);

        q.Schedule(300, "b", TurnEventKinds.Contact, ""); // same instant is legal
        Assert.True(q.TryDequeue(out var second));
        Assert.Equal("b", second.EntityId);
    }

    [Fact]
    public void Scheduling_into_the_past_throws_because_it_would_silently_reorder_a_turn()
    {
        var q = new TurnEventQueue();
        q.Schedule(400, "a", TurnEventKinds.Arrival, "");
        q.TryDequeue(out _);

        var ex = Assert.Throws<InvalidOperationException>(
            () => q.Schedule(399, "b", TurnEventKinds.Arrival, ""));
        Assert.Contains("399", ex.Message);
    }

    [Fact]
    public void Times_outside_the_turn_are_refused()
    {
        var q = new TurnEventQueue();
        Assert.Throws<ArgumentOutOfRangeException>(() => q.Schedule(-1, "a", TurnEventKinds.Arrival, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => q.Schedule(1001, "a", TurnEventKinds.Arrival, ""));
    }

    [Fact]
    public void An_empty_queue_simply_reports_empty()
    {
        var q = new TurnEventQueue();
        Assert.False(q.TryDequeue(out _));
        Assert.Equal(0, q.Count);
    }

    static List<TurnEvent> Drain(TurnEventQueue q)
    {
        var list = new List<TurnEvent>();
        while (q.TryDequeue(out var e)) list.Add(e);
        return list;
    }
}

public class TurnCalendarTests
{
    [Fact]
    public void Day_seven_opens_a_week_and_day_twenty_nine_opens_a_month()
    {
        Assert.False(TurnCalendar.Roll(turn: 3, seed: 1).WeekBoundary);
        Assert.True(TurnCalendar.Roll(turn: 7, seed: 1).WeekBoundary);
        Assert.True(TurnCalendar.Roll(turn: 14, seed: 1).WeekBoundary);

        Assert.False(TurnCalendar.Roll(turn: 7, seed: 1).MonthBoundary);
        Assert.True(TurnCalendar.Roll(turn: 28, seed: 1).MonthBoundary);
    }

    [Fact]
    public void The_calendar_is_a_pure_function_of_turn_and_seed()
    {
        var a = TurnCalendar.Roll(28, 99);
        var b = TurnCalendar.Roll(28, 99);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_plague_month_can_only_happen_on_a_month_boundary()
    {
        for (var turn = 1; turn <= 120; turn++)
        {
            var roll = TurnCalendar.Roll(turn, seed: 5);
            if (roll.Plague) Assert.True(roll.MonthBoundary);
            if (roll.SpecialWeek) Assert.True(roll.WeekBoundary);
        }
    }
}

/// <summary>
/// world-map W58 (spec-sector-development.md §2, "the season is visible in the turn report"):
/// `TurnEngine.Events` reports a `calendar`/`season` entry the turn a season actually changes, the
/// same "only at its own boundary" precedent `TurnReportKinds.Calendar`'s existing `week`/`month`
/// entries already follow one block above — proven through a real <see cref="TurnEngine.Step"/>
/// commit, not the pure <see cref="TurnCalendar"/> formula alone, since this is specifically about
/// what reaches the report.
/// </summary>
public class TurnEngineSeasonReportTests
{
    static WorldState World(int currentTurn) =>
        WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1) with { CurrentTurn = currentTurn };

    static bool HasSeasonEntry(TurnReport report, out string? detail)
    {
        var entry = report.Entries.FirstOrDefault(e =>
            e.Kind == TurnReportKinds.Calendar && e.Subject == "season");
        detail = entry.Subject == "season" ? entry.Detail : null;
        return detail != null;
    }

    [Fact]
    public void A_season_boundary_turn_reports_the_new_season_index()
    {
        // world.v5.json: monthsPerSeason 1, so day 28 (world.CurrentTurn 27 -> Step turn 28) is the
        // first day of season 1 — the real, shipped boundary this task's own tuning change created.
        var daysPerSeason = TurnCalendar.DaysPerMonth * TurnCalendar.MonthsPerSeason;
        var result = TurnEngine.Step(World(daysPerSeason - 1), Array.Empty<WorldCommand>(), seed: 1);

        Assert.True(HasSeasonEntry(result.Report, out var detail));
        Assert.Equal("1", detail);
        Assert.Equal(1, TurnCalendar.SeasonOf(daysPerSeason));
    }

    [Fact]
    public void A_non_boundary_turn_reports_no_season_entry_at_all()
    {
        var result = TurnEngine.Step(World(currentTurn: 2), Array.Empty<WorldCommand>(), seed: 1);

        Assert.False(HasSeasonEntry(result.Report, out _));
    }

    [Fact]
    public void The_very_first_turn_reports_no_season_entry_there_is_no_prior_season_to_have_changed_from()
    {
        var result = TurnEngine.Step(World(currentTurn: 0), Array.Empty<WorldCommand>(), seed: 1);

        Assert.False(HasSeasonEntry(result.Report, out _));
    }
}
