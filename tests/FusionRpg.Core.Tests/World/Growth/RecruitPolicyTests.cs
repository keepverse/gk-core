using System;
using System.IO;
using System.Linq;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Growth;

/// <summary>
/// world-map W42 acceptance: every new `growth` tuning key reaches its call site through a named
/// accessor, never a bare literal — and `legionTarget` is read only by the acceptance harness, never
/// by the engine. The pulse/raise mechanism itself is W43's own scope; this proves the accessors and
/// the harness-only rule for the value they will read.
/// </summary>
public class RecruitPolicyTests
{
    [Fact]
    public void Every_accessor_reads_the_configured_growth_tuning()
    {
        // Values match ContractTuningTestBootstrap.DefaultWorld.Growth, already configured once for
        // this whole assembly (tunables-ssot.md §7.2) — read back, not reconfigured here. Real,
        // non-identity numbers since world-map W58 (gk-core/data/tuning/world.v5.json).
        Assert.Equal(20, RecruitPolicy.SeatPulsePerWeek);
        Assert.Equal(4000, RecruitPolicy.LairMultiplierMilli);
        Assert.Equal(1500, RecruitPolicy.SpecialWeekMultiplierMilli);
        Assert.Equal(100, RecruitPolicy.RaiseCostPoints);
        Assert.Equal(110, RecruitPolicy.RaiseMemberHp);
        Assert.Equal(6, RecruitPolicy.LegionTarget.Min);
        Assert.Equal(10, RecruitPolicy.LegionTarget.Max);
        Assert.Equal(40, RecruitPolicy.LegionTarget.ByTurn);
    }

    [Fact]
    public void LegionTarget_is_referenced_by_no_file_under_src_except_its_own_accessor()
    {
        // A legion count the engine enforced would be a hard progression ceiling (AGENTS.md) — the
        // target is read only by the acceptance harness (W59), which does not exist yet. This walks
        // the real source tree rather than trusting a comment, matching this module's own precedent
        // (WorldDeterminismGuardTests scans `src/` the same way).
        // WorldTuning.cs and RecruitPolicy.cs are the schema and the accessor — plumbing that
        // carries the value, never engine logic that could act on it. Everything else under src/
        // is a real consumer, and none may exist yet.
        var plumbing = new[] { "World/WorldTuning.cs", "World/Growth/RecruitPolicy.cs" };
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !plumbing.Any(p => f.Replace('\\', '/').EndsWith(p)))
            .Where(f => File.ReadAllText(f).Contains("LegionTarget"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"LegionTarget referenced outside its schema/accessor: {string.Join(", ", offenders)}");
    }

    // Local, isolated test values — deliberately independent of whatever the real tuning ships
    // (world-map W58 turned it on for real, but these tests still want their own fixed numbers so a
    // future balance pass cannot silently change what this file proves). PulseFor takes every number
    // as an explicit parameter for exactly this reason (never reads RecruitPolicy's own accessors
    // internally — a real xUnit-parallelism hazard, since Configure sets one static field shared by
    // the whole assembly).
    const long SeatPulse = 100;
    const int LairMultiplier = 2000; // a cleared lair doubles the pulse
    const int SpecialWeekMultiplier = 1500; // a special week adds half again

    static readonly CalendarRoll WeekOnly = new(WeekBoundary: true, MonthBoundary: false, SpecialWeek: false, SpecialMonth: false, Plague: false, Season: 0);
    static readonly CalendarRoll NotABoundary = default;
    static readonly CalendarRoll SpecialWeekRoll = WeekOnly with { SpecialWeek = true };
    static readonly CalendarRoll PlagueRoll = WeekOnly with { MonthBoundary = true, Plague = true };
    static readonly CalendarRoll PlagueAndSpecialWeekRoll = WeekOnly with { MonthBoundary = true, SpecialWeek = true, Plague = true };

    [Fact]
    public void A_pulse_fires_on_week_boundaries_and_only_on_week_boundaries()
    {
        Assert.Equal(0, RecruitPolicy.PulseFor(hasSeat: true, lairCleared: false, NotABoundary, SeatPulse, LairMultiplier, SpecialWeekMultiplier));
        Assert.Equal(SeatPulse, RecruitPolicy.PulseFor(hasSeat: true, lairCleared: false, WeekOnly, SeatPulse, LairMultiplier, SpecialWeekMultiplier));
    }

    [Fact]
    public void A_sector_with_no_seat_contributes_nothing_regardless_of_its_lair()
    {
        Assert.Equal(0, RecruitPolicy.PulseFor(hasSeat: false, lairCleared: true, WeekOnly, SeatPulse, LairMultiplier, SpecialWeekMultiplier));
    }

    [Fact]
    public void A_cleared_lair_multiplies_its_sectors_pulse_and_an_intact_one_does_not()
    {
        var withoutLair = RecruitPolicy.PulseFor(hasSeat: true, lairCleared: false, WeekOnly, SeatPulse, LairMultiplier, SpecialWeekMultiplier);
        var withClearedLair = RecruitPolicy.PulseFor(hasSeat: true, lairCleared: true, WeekOnly, SeatPulse, LairMultiplier, SpecialWeekMultiplier);

        Assert.Equal(SeatPulse, withoutLair);
        Assert.Equal(SeatPulse * LairMultiplier / 1000, withClearedLair);
    }

    [Fact]
    public void A_special_week_scales_the_pulse()
    {
        var ordinary = RecruitPolicy.PulseFor(hasSeat: true, lairCleared: false, WeekOnly, SeatPulse, LairMultiplier, SpecialWeekMultiplier);
        var special = RecruitPolicy.PulseFor(hasSeat: true, lairCleared: false, SpecialWeekRoll, SeatPulse, LairMultiplier, SpecialWeekMultiplier);

        Assert.Equal(SeatPulse, ordinary);
        Assert.Equal(SeatPulse * SpecialWeekMultiplier / 1000, special);
    }

    [Fact]
    public void A_plague_month_suppresses_growth_outright_and_beats_a_special_week()
    {
        // Matches the identical rule TurnCalendar.cs:52-54 already applies to its own month term.
        Assert.Equal(0, RecruitPolicy.PulseFor(hasSeat: true, lairCleared: true, PlagueRoll, SeatPulse, LairMultiplier, SpecialWeekMultiplier));
        Assert.Equal(0, RecruitPolicy.PulseFor(hasSeat: true, lairCleared: true, PlagueAndSpecialWeekRoll, SeatPulse, LairMultiplier, SpecialWeekMultiplier));
    }

    [Fact]
    public void The_lair_and_special_week_multipliers_compose_multiplicatively()
    {
        var roll = SpecialWeekRoll;
        var pulse = RecruitPolicy.PulseFor(hasSeat: true, lairCleared: true, roll, SeatPulse, LairMultiplier, SpecialWeekMultiplier);

        Assert.Equal(SeatPulse * LairMultiplier * SpecialWeekMultiplier / 1_000_000, pulse);
    }

    [Fact]
    public void Zero_seat_pulse_per_week_stays_zero_regardless_of_multipliers()
    {
        // A hypothetical zero pulse (the real shipped value moved off 0 at world-map W58,
        // gk-core/data/tuning/world.v5.json) — the pure function's own zero-stays-zero property, proven
        // independent of whatever the live tuning currently ships.
        Assert.Equal(0, RecruitPolicy.PulseFor(hasSeat: true, lairCleared: true, SpecialWeekRoll, seatPulsePerWeek: 0, LairMultiplier, SpecialWeekMultiplier));
    }

    // Matches WorldDeterminismGuardTests' own root-finding convention (gk-core/tests/FusionRpg.Guard.Tests).
    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FusionRpg.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
