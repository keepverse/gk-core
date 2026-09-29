using FusionRpg.Core.World;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Growth;

/// <summary>
/// world-map W50 acceptance, updated by W58: the real, shipped pulse moved off identity (0), so the
/// live-tuning test below now proves growth genuinely accrues through the real production caller
/// rather than proving it stays inert. With a non-zero pulse supplied **locally** (never the shared
/// `RecruitPolicy` singleton) in every other test in this file, stock accrues on week boundaries
/// only, in stable sector-id order, and the report gains one structural entry per accruing sector.
/// </summary>
public class GrowthPhasesTests
{
    const string Phase = "Test";
    const long SeatPulse = 100;
    const int LairMultiplier = 1000; // identity — isolates the seat pulse alone unless a test says otherwise
    const int SpecialWeekMultiplier = 1000;

    static WorldSlot Seat(string? owner = null) =>
        new() { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId, OwnerFactionId = owner };

    static WorldSlot Lair(GuardState guardState) =>
        new() { SlotIndex = 1, SlotTypeId = "lair", GuardState = guardState };

    static WorldSector Sector(string id, string? owner, IReadOnlyList<WorldSlot> slots, long recruitStock = 0) =>
        new() { SectorId = id, TypeId = "stable", OwnerFactionId = owner, Slots = slots, RecruitStock = recruitStock };

    static WorldState World(int currentTurn, params WorldSector[] sectors) => new()
    {
        WorldId = "w", TemplateId = "test", Seed = 1, CurrentTurn = currentTurn,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = sectors.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList()
    };

    static WorldState RunGrowth(WorldState world, TurnReport report, int turn, ulong seed = 1,
        long seatPulse = SeatPulse, int lairMultiplier = LairMultiplier, int specialWeekMultiplier = SpecialWeekMultiplier) =>
        GrowthPhases.Growth(world, report, Phase, turn, seed, seatPulse, lairMultiplier, specialWeekMultiplier);

    [Fact]
    public void At_the_real_shipped_pulse_a_held_seat_accrues_exactly_what_PulseFor_computes()
    {
        // world-map W58 turned growth on for real (gk-core/data/tuning/world.v5.json) — this now proves the
        // real production caller (TurnEngine.Growth → RecruitPolicy's own live-configured accessors)
        // composes correctly, rather than proving growth stays inert the way this test did pre-W58.
        // The expected value is derived from the same pure `PulseFor` the production path itself
        // calls, rather than a hand-picked literal, so this stays correct through a future balance
        // pass without needing to track whichever week/special-week roll turn 7/seed 1 happens to
        // produce.
        const int turn = 7;
        const ulong seed = 1;
        var world = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1") }));
        var report = new TurnReport();

        var result = GrowthPhases.Growth(world, report, Phase, turn, seed,
            seatPulsePerWeek: RecruitPolicy.SeatPulsePerWeek,
            lairMultiplierMilli: RecruitPolicy.LairMultiplierMilli,
            specialWeekMultiplierMilli: RecruitPolicy.SpecialWeekMultiplierMilli);

        var expectedPulse = RecruitPolicy.PulseFor(
            hasSeat: true, lairCleared: false, TurnCalendar.Roll(turn, seed),
            RecruitPolicy.SeatPulsePerWeek, RecruitPolicy.LairMultiplierMilli, RecruitPolicy.SpecialWeekMultiplierMilli);

        Assert.True(expectedPulse > 0, "the real shipped pulse must be non-zero since world-map W58 turned growth on");
        Assert.Equal(expectedPulse, result.Sectors.Single().RecruitStock);
        Assert.Single(report.Entries);
    }

    [Fact]
    public void A_held_seat_accrues_the_pulse_on_a_week_boundary_turn_and_only_then()
    {
        var world = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1") }));

        var onBoundary = RunGrowth(world, new TurnReport(), turn: 7); // 7 % DaysPerWeek(7) == 0
        Assert.Equal(SeatPulse, onBoundary.Sectors.Single().RecruitStock);

        var offBoundary = RunGrowth(world, new TurnReport(), turn: 8);
        Assert.Equal(0, offBoundary.Sectors.Single().RecruitStock);
    }

    [Fact]
    public void A_sector_with_no_seat_accrues_nothing_regardless_of_its_lair()
    {
        var world = World(currentTurn: 6, Sector("s1", "f1", new[] { Lair(GuardState.Cleared) }));
        var result = RunGrowth(world, new TurnReport(), turn: 7);

        Assert.Equal(0, result.Sectors.Single().RecruitStock);
    }

    [Fact]
    public void An_unowned_sector_accrues_nothing_even_with_a_seat_slot()
    {
        var world = World(currentTurn: 6, Sector("s1", null, new[] { Seat() }));
        var result = RunGrowth(world, new TurnReport(), turn: 7);

        Assert.Equal(0, result.Sectors.Single().RecruitStock);
    }

    [Fact]
    public void A_cleared_lair_multiplies_its_own_sectors_pulse_and_an_intact_one_does_not()
    {
        var withCleared = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1"), Lair(GuardState.Cleared) }));
        var withIntact = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1"), Lair(GuardState.Intact) }));

        var cleared = RunGrowth(withCleared, new TurnReport(), turn: 7, lairMultiplier: 2000);
        var intact = RunGrowth(withIntact, new TurnReport(), turn: 7, lairMultiplier: 2000);

        Assert.Equal(SeatPulse * 2, cleared.Sectors.Single().RecruitStock);
        Assert.Equal(SeatPulse, intact.Sectors.Single().RecruitStock);
    }

    [Fact]
    public void Stock_accrues_onto_whatever_was_already_there_rather_than_overwriting_it()
    {
        var world = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1") }, recruitStock: 50));
        var result = RunGrowth(world, new TurnReport(), turn: 7);

        Assert.Equal(50 + SeatPulse, result.Sectors.Single().RecruitStock);
    }

    [Fact]
    public void An_accruing_sector_gets_one_structural_report_entry_naming_it_by_SectorId()
    {
        var world = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1") }));
        var report = new TurnReport();

        RunGrowth(world, report, turn: 7);

        var entry = Assert.Single(report.Entries);
        Assert.Equal("s1", entry.SectorId);
        Assert.Equal(TurnReportKinds.Event, entry.Kind);
        Assert.StartsWith("growth.pulse:", entry.Detail);
    }

    // ---- W52: sector-wide projects advance here, never in Production ---------------------------

    static WorldSector SectorWithProject(string id, string? owner, string? projectId, int? turnsRemaining) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = owner,
            ProjectId = projectId, ProjectTurnsRemaining = turnsRemaining
        };

    [Fact]
    public void A_project_with_more_than_one_turn_remaining_only_decrements()
    {
        var world = World(currentTurn: 6, SectorWithProject("s1", "f1", "raise-development-placeholder", 3));
        var report = new TurnReport();

        var result = RunGrowth(world, report, turn: 8); // not a week boundary — isolates the project alone

        var sector = result.Sectors.Single();
        Assert.Equal("raise-development-placeholder", sector.ProjectId);
        Assert.Equal(2, sector.ProjectTurnsRemaining);
        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("develop.completed"));
    }

    [Fact]
    public void A_project_reaching_zero_completes_clears_and_reports()
    {
        var world = World(currentTurn: 6, SectorWithProject("s1", "f1", "raise-development-placeholder", 1));
        var report = new TurnReport();

        var result = RunGrowth(world, report, turn: 8);

        var sector = result.Sectors.Single();
        Assert.Null(sector.ProjectId);
        Assert.Null(sector.ProjectTurnsRemaining);
        Assert.Contains(report.Entries,
            e => e.Kind == TurnReportKinds.Event && e.Detail == "develop.completed:raise-development-placeholder");
    }

    // ---- W53: a completed project is what raises DevelopmentLevel -------------------------------

    [Fact]
    public void A_completed_project_raises_DevelopmentLevel_by_its_authored_amount_once_and_reports_it()
    {
        var project = ProjectCatalog.Get("raise-development-placeholder");
        var world = World(currentTurn: 6, SectorWithProject("s1", "f1", project.ProjectId, 1) with { DevelopmentLevel = 5 });
        var report = new TurnReport();

        var result = RunGrowth(world, report, turn: 8);

        var sector = result.Sectors.Single();
        Assert.Equal(5 + project.DevelopmentBonus, sector.DevelopmentLevel);
        Assert.Contains(report.Entries,
            e => e.Kind == TurnReportKinds.Event && e.Detail == "development.raised:" + project.DevelopmentBonus
                 && e.SectorId == "s1");
    }

    [Fact]
    public void A_sector_with_no_project_never_has_its_DevelopmentLevel_touched()
    {
        var world = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1") }) with { DevelopmentLevel = 7 });

        var result = RunGrowth(world, new TurnReport(), turn: 8);

        Assert.Equal(7, result.Sectors.Single().DevelopmentLevel);
    }

    [Fact]
    public void No_number_of_Growth_passes_ever_lowers_a_sectors_DevelopmentLevel()
    {
        // AGENTS.md's no-hard-progression-ceiling rule cuts both ways here: there is no
        // de-development anywhere. A long run of ordinary turns (no project, no seat) must never see
        // the level move at all, let alone downward.
        var world = World(currentTurn: 6, Sector("s1", "f1", Array.Empty<WorldSlot>()) with { DevelopmentLevel = 4 });
        var seed = world;

        for (var turn = 7; turn <= 30; turn++)
        {
            var before = seed.Sectors.Single().DevelopmentLevel;
            seed = RunGrowth(seed, new TurnReport(), turn);
            Assert.True(seed.Sectors.Single().DevelopmentLevel >= before);
        }

        Assert.Equal(4, seed.Sectors.Single().DevelopmentLevel);
    }

    [Fact]
    public void A_sector_with_no_project_is_untouched_by_project_advancement()
    {
        var world = World(currentTurn: 6, Sector("s1", "f1", new[] { Seat("f1") }));
        var report = new TurnReport();

        RunGrowth(world, report, turn: 8);

        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("develop."));
    }

    [Fact]
    public void An_unowned_sectors_project_still_advances_matching_LoamPhases_Production_own_precedent()
    {
        // `LoamPhases.Production`'s own `DecrementConstruction` counts a structure down unconditional
        // on ownership — a project follows the identical rule here, one phase over. A lost sector's
        // half-finished project is cleared explicitly, in `LoamPhases.Pressure`'s own `Lost` branch,
        // never silently frozen here by an ownership check this phase does not otherwise need.
        var world = World(currentTurn: 6, SectorWithProject("s1", null, "raise-development-placeholder", 1));
        var report = new TurnReport();

        var result = RunGrowth(world, report, turn: 8);

        Assert.Null(result.Sectors.Single().ProjectId);
        Assert.Contains(report.Entries, e => e.Detail == "develop.completed:raise-development-placeholder");
    }

    [Fact]
    public void A_completing_project_and_a_recruit_pulse_both_report_in_the_same_pass_independently()
    {
        var world = World(currentTurn: 6,
            SectorWithProject("s1", "f1", "raise-development-placeholder", 1) with { Slots = new[] { Seat("f1") } });
        var report = new TurnReport();

        var result = RunGrowth(world, report, turn: 7); // a real week boundary — both effects fire together

        var sector = result.Sectors.Single();
        Assert.Null(sector.ProjectId);
        Assert.Equal(SeatPulse, sector.RecruitStock);
        Assert.Contains(report.Entries, e => e.Detail == "develop.completed:raise-development-placeholder");
        Assert.Contains(report.Entries, e => e.Detail.StartsWith("growth.pulse:"));
    }

    [Fact]
    public void Every_held_seat_accrues_independently_in_stable_sector_id_order()
    {
        var world = World(currentTurn: 6,
            Sector("s2", "f1", new[] { Seat("f1") }),
            Sector("s1", "f1", new[] { Seat("f1") }));
        var report = new TurnReport();

        var result = RunGrowth(world, report, turn: 7);

        Assert.Equal(new[] { "s1", "s2" }, result.Sectors.Select(s => s.SectorId));
        Assert.All(result.Sectors, s => Assert.Equal(SeatPulse, s.RecruitStock));
        Assert.Equal(new[] { "s1", "s2" }, report.Entries.Select(e => e.SectorId));
    }
}
