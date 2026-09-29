using FusionRpg.Core.Battle;

namespace FusionRpg.Core.World.Turn;

/// <summary>
/// What a calendar boundary produced. Wave 1 records the rolls; the economic effects land with
/// sector-development, which is the module that owns growth.
/// </summary>
public readonly record struct CalendarRoll(
    bool WeekBoundary, bool MonthBoundary, bool SpecialWeek, bool SpecialMonth, bool Plague, int Season);

/// <summary>
/// A turn is a day, seven days a week, four weeks a month (spec-turn-engine.md §Calendar). The
/// boundaries are where the world gets its heartbeat: recruits arrive in pulses, and a rare plague
/// month is what tells a player whether their defence depended on fresh bodies.
///
/// Pure in (turn, seed): the same day of the same world always rolls the same way, so a replay
/// cannot drift and a client can honestly show next week before it arrives.
/// </summary>
public static class TurnCalendar
{
    public static int DaysPerWeek => World.WorldTuningHub.Tuning.Calendar.DaysPerWeek;
    public static int WeeksPerMonth => World.WorldTuningHub.Tuning.Calendar.WeeksPerMonth;
    public static int DaysPerMonth => DaysPerWeek * WeeksPerMonth;

    // Genre-proven rates: a quarter of weeks are special, plague is rare enough to be a story.
    public static int SpecialWeekChanceMilli => World.WorldTuningHub.Tuning.Calendar.SpecialWeekChanceMilli;
    public static int SpecialMonthChanceMilli => World.WorldTuningHub.Tuning.Calendar.SpecialMonthChanceMilli;
    public static int PlagueChanceMilli => World.WorldTuningHub.Tuning.Calendar.PlagueChanceMilli;

    /// <summary>world-map W47 (spec-sector-development.md §2): count and length come from the same tuning file, beside `Calendar`, because a season *is* the calendar.</summary>
    public static int SeasonCount => World.WorldTuningHub.Tuning.Seasons.Count;
    public static int MonthsPerSeason => World.WorldTuningHub.Tuning.Seasons.MonthsPerSeason;

    /// <summary>
    /// world-map W47: a season is a pure function of the turn — no RNG, no state
    /// (spec-sector-development.md §2). Unlike every other member of <see cref="CalendarRoll"/>,
    /// this is meaningful on **every** turn, not only a week boundary — "what season is it" is never
    /// fogged and never gated, so it is computed unconditionally before any of <see cref="Roll"/>'s
    /// early returns rather than defaulting to zero on a turn that happens not to be a boundary.
    /// </summary>
    public static int SeasonOf(int turn) => turn / (DaysPerMonth * MonthsPerSeason) % SeasonCount;

    public static CalendarRoll Roll(int turn, ulong seed)
    {
        var season = SeasonOf(turn);
        if (turn <= 0) return new CalendarRoll(false, false, false, false, false, season);

        var weekBoundary = turn % DaysPerWeek == 0;
        var monthBoundary = turn % DaysPerMonth == 0;
        if (!weekBoundary) return new CalendarRoll(false, false, false, false, false, season);

        // One stream per boundary kind, derived from the turn: an extra roll in one never shifts
        // the other, which is the same discipline the battle engine uses.
        var weekRng = SeededRng.DeriveStream(seed, "calendar:week:" + turn);
        var specialWeek = weekRng.NextInt(1000) < SpecialWeekChanceMilli;

        var specialMonth = false;
        var plague = false;
        if (monthBoundary)
        {
            var monthRng = SeededRng.DeriveStream(seed, "calendar:month:" + turn);
            specialMonth = monthRng.NextInt(1000) < SpecialMonthChanceMilli;
            plague = monthRng.NextInt(1000) < PlagueChanceMilli;

            // A month cannot both double growth and halt it; the plague wins, because the month
            // people remember is the bad one.
            if (plague) specialMonth = false;
        }

        return new CalendarRoll(weekBoundary, monthBoundary, specialWeek, specialMonth, plague, season);
    }
}
