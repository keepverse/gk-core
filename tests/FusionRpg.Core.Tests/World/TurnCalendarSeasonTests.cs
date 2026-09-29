using FusionRpg.Core.Battle;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-map W47 acceptance: `CalendarRoll.Season` is a pure function of the turn — no RNG, no
/// state (spec-sector-development.md §2). `season(turn) = (turn / (DaysPerWeek * WeeksPerMonth *
/// MonthsPerSeason)) % SeasonCount`, table-tested across a full cycle plus the boundaries either
/// side, and proven to draw nothing by comparing against the RNG streams computed independently.
/// </summary>
public class TurnCalendarSeasonTests
{
    [Fact]
    public void Season_of_turn_matches_the_formula_across_a_full_cycle_and_its_boundaries()
    {
        // Read from the real configured tuning (world.v5.json, world-map W42/W58) rather than a
        // hardcoded literal — this table stays correct if a balance pass ever moves these numbers.
        var daysPerSeason = TurnCalendar.DaysPerMonth * TurnCalendar.MonthsPerSeason;
        var seasonCount = TurnCalendar.SeasonCount;

        var cases = new (int Turn, int ExpectedSeason)[]
        {
            (0, 0),
            (1, 0),
            (daysPerSeason - 1, 0), // the last day still inside season 0
            (daysPerSeason, 1), // the first day of season 1 — the boundary itself
            (daysPerSeason + 1, 1),
            (daysPerSeason * 2, 2 % seasonCount),
            (daysPerSeason * 3, 3 % seasonCount),
            (daysPerSeason * seasonCount, 0), // wraps after a full cycle
            (daysPerSeason * seasonCount + 1, 0),
            (daysPerSeason * (seasonCount + 1), 1) // a second cycle, proving it is not a one-shot wrap
        };

        foreach (var (turn, expectedSeason) in cases)
        {
            Assert.Equal(expectedSeason, TurnCalendar.SeasonOf(turn));
            Assert.Equal(expectedSeason, TurnCalendar.Roll(turn, seed: 42).Season);
        }
    }

    [Fact]
    public void Season_is_meaningful_on_every_turn_not_only_a_week_boundary()
    {
        // Turn 100 is not a week boundary (100 % 7 != 0), so Roll's own early-return path is what
        // this proves reaches: the season the struct-default zero would otherwise silently paper
        // over on any non-boundary turn past the first season.
        Assert.False(TurnCalendar.Roll(100, seed: 1).WeekBoundary);
        Assert.Equal(TurnCalendar.SeasonOf(100), TurnCalendar.Roll(100, seed: 1).Season);
        Assert.NotEqual(0, TurnCalendar.SeasonOf(100)); // otherwise this test would not distinguish the bug from the fix
    }

    [Fact]
    public void The_same_turn_and_seed_always_gives_the_same_roll()
    {
        var a = TurnCalendar.Roll(280, seed: 7);
        var b = TurnCalendar.Roll(280, seed: 7);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Adding_season_drew_no_new_rng_the_week_and_month_streams_are_byte_identical()
    {
        // Reconstructs the week/month roll independently of TurnCalendar.Roll, from the exact same
        // "calendar:week:<turn>"/"calendar:month:<turn>" streams the method itself derives from —
        // if Season ever consumed a draw of its own, the two would diverge the first time a stream
        // is advanced by it.
        const int turn = 28; // a week AND month boundary
        const ulong seed = 123;

        var weekRng = SeededRng.DeriveStream(seed, "calendar:week:" + turn);
        var expectedSpecialWeek = weekRng.NextInt(1000) < TurnCalendar.SpecialWeekChanceMilli;

        var monthRng = SeededRng.DeriveStream(seed, "calendar:month:" + turn);
        var expectedSpecialMonth = monthRng.NextInt(1000) < TurnCalendar.SpecialMonthChanceMilli;
        var expectedPlague = monthRng.NextInt(1000) < TurnCalendar.PlagueChanceMilli;
        if (expectedPlague) expectedSpecialMonth = false;

        var roll = TurnCalendar.Roll(turn, seed);
        Assert.Equal(expectedSpecialWeek, roll.SpecialWeek);
        Assert.Equal(expectedSpecialMonth, roll.SpecialMonth);
        Assert.Equal(expectedPlague, roll.Plague);
    }
}
