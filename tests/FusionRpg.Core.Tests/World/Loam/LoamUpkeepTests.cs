using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>L7 acceptance (spec-loam-calc.md #3): the row overload is the one worked by hand.</summary>
public class LoamUpkeepTests
{
    static long UnmultipliedSum(int garrisonMembers, int developmentLevel, int dangerBand) =>
        LoamPolicy.BaseUpkeepPerSector
        + (long)garrisonMembers * LoamPolicy.GarrisonUpkeepPerMember
        + LoamPolicy.DevelopmentAndDangerUpkeep(developmentLevel, dangerBand);

    [Fact]
    public void Baseline_intensity_and_handicap_cost_exactly_the_unmultiplied_sum()
    {
        var sum = UnmultipliedSum(garrisonMembers: 4, developmentLevel: 2, dangerBand: 1);
        Assert.Equal(sum, LoamUpkeep.For(4, 2, 1, intensityMilli: 1000, handicapMilli: 1000, seasonMilli: 1000));
    }

    [Fact]
    public void Half_intensity_costs_half_and_double_intensity_costs_double()
    {
        var sum = UnmultipliedSum(garrisonMembers: 4, developmentLevel: 2, dangerBand: 1);

        Assert.Equal(sum / 2, LoamUpkeep.For(4, 2, 1, intensityMilli: 500, handicapMilli: 1000, seasonMilli: 1000));
        Assert.Equal(sum * 2, LoamUpkeep.For(4, 2, 1, intensityMilli: 2000, handicapMilli: 1000, seasonMilli: 1000));
    }

    [Fact]
    public void The_handicap_scales_the_same_way_intensity_does()
    {
        var sum = UnmultipliedSum(garrisonMembers: 0, developmentLevel: 0, dangerBand: 0);
        Assert.Equal(sum / 2, LoamUpkeep.For(0, 0, 0, intensityMilli: 1000, handicapMilli: 500, seasonMilli: 1000));
    }

    /// <summary>world-map W48: the season term scales the same way intensity/handicap do, at identity elsewhere.</summary>
    [Fact]
    public void The_season_scales_the_same_way_intensity_and_handicap_do()
    {
        var sum = UnmultipliedSum(garrisonMembers: 0, developmentLevel: 0, dangerBand: 0);
        Assert.Equal(sum / 2, LoamUpkeep.For(0, 0, 0, intensityMilli: 1000, handicapMilli: 1000, seasonMilli: 500));
        Assert.Equal(sum * 2, LoamUpkeep.For(0, 0, 0, intensityMilli: 1000, handicapMilli: 1000, seasonMilli: 2000));
    }

    [Fact]
    public void The_formula_divides_only_once_not_once_per_multiplier()
    {
        // Round inputs (500/1000/2000/3000, handicap 1000) cannot tell "multiply everything, divide
        // once" apart from "divide by each multiplier's 1000 separately" — the intermediate
        // truncation happens to land on the same floor either way. Deliberately ugly numbers make
        // the two shapes diverge: 3×333×777×444 ÷ 1e9 = 0 (integer floor), but dividing per-factor
        // along the way would round the intermediate steps up to a nonzero total.
        var sum = UnmultipliedSum(garrisonMembers: 3, developmentLevel: 0, dangerBand: 0);
        var singleDivision = sum * 333 * 777 * 444 / 1_000_000_000;

        Assert.Equal(singleDivision, LoamUpkeep.For(3, 0, 0, intensityMilli: 333, handicapMilli: 777, seasonMilli: 444));
    }

    [Fact]
    public void No_overflow_at_the_largest_legal_intensity_and_handicap()
    {
        // A sum large enough that, as an `int` multiplied by the three ceilings before any division,
        // would already have wrapped past int.MaxValue (~2.147e9) — the bug this module's `long`
        // quantities exist to remove as a class rather than patch as an instance. Season's own
        // ceiling is not a `WorldValidation` rule (no rule bounds it, unlike intensity/handicap),
        // so a generous but plausible balance-pass value (3000, matching `MaxIntensityMilli`'s own
        // ceiling reasoning) stands in for "the top of its legal range" here.
        const int hugeSum = 1_000_000;
        const int seasonCeiling = 3000;
        var upkeep = LoamUpkeep.For(
            garrisonMembers: hugeSum, developmentLevel: 0, dangerBand: 0,
            intensityMilli: WorldValidation.MaxIntensityMilli, handicapMilli: WorldValidation.MaxHandicapMilli,
            seasonMilli: seasonCeiling);

        var expectedSum = UnmultipliedSum(hugeSum, 0, 0);
        var expected = expectedSum * WorldValidation.MaxIntensityMilli * WorldValidation.MaxHandicapMilli * seasonCeiling / 1_000_000_000;

        Assert.True(upkeep > 0, "upkeep silently wrapped negative at the legal maximum");
        Assert.Equal(expected, upkeep);
    }

    [Fact]
    public void A_forced_overflow_throws_rather_than_wrapping()
    {
        // Pushes the four-factor product itself past long.MaxValue (~9.22e18) — not merely the
        // pre-division numerator of the previous test, which stays comfortably inside range. This is
        // the `checked` block's own reason to exist: AGENTS.md's overflow rule says silently
        // wrapping into a negative or truncated upkeep is worse than crashing loudly.
        Assert.Throws<OverflowException>(() => LoamUpkeep.For(
            garrisonMembers: int.MaxValue, developmentLevel: int.MaxValue, dangerBand: int.MaxValue,
            intensityMilli: WorldValidation.MaxIntensityMilli, handicapMilli: WorldValidation.MaxHandicapMilli,
            seasonMilli: 3000));
    }

    [Fact]
    public void Development_and_danger_upkeep_scales_linearly_and_independently()
    {
        // The formula's *shape* — additive, independent terms, each contributing something — pinned
        // without hardcoding the exact provisional rates (LoamPolicy.DevelopmentUpkeepPerLevel and
        // DangerUpkeepPerBand are tuning values L9's harness owns, not this test).
        var neither = LoamPolicy.DevelopmentAndDangerUpkeep(0, 0);
        var developmentOnly = LoamPolicy.DevelopmentAndDangerUpkeep(1, 0);
        var dangerOnly = LoamPolicy.DevelopmentAndDangerUpkeep(0, 1);
        var both = LoamPolicy.DevelopmentAndDangerUpkeep(1, 1);

        Assert.Equal(0, neither);
        Assert.True(developmentOnly > neither, "one level of development must cost something");
        Assert.True(dangerOnly > neither, "one band of danger must cost something");
        Assert.Equal(developmentOnly + dangerOnly, both); // additive, not multiplied together
    }

    // ---- the truth overload: ownership and the faction-wide source exemption ----

    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldState WorldWith(WorldSector sector, WorldFaction faction, params WorldSector[] otherSectors) => new()
    {
        WorldId = "w", TemplateId = "test", Seed = 1,
        Factions = new[] { faction },
        Sectors = new[] { sector }.Concat(otherSectors).OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList()
    };

    [Fact]
    public void An_unowned_sector_costs_nothing()
    {
        var sector = new WorldSector { SectorId = "s", OwnerFactionId = null, DevelopmentLevel = 5, DangerBand = 5 };
        var world = WorldWith(sector, new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" });

        Assert.Equal(0, LoamUpkeep.For(world, sector));
    }

    [Fact]
    public void A_faction_with_a_rootbed_somewhere_pays_upkeep_on_a_sector_that_has_none()
    {
        var barren = new WorldSector { SectorId = "s-barren", OwnerFactionId = "f1" };
        var withSource = new WorldSector { SectorId = "s-source", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        var world = WorldWith(barren, new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" }, withSource);

        Assert.True(LoamUpkeep.For(world, barren) > 0);
    }

    [Fact]
    public void A_standing_garrison_raises_the_truth_overloads_upkeep()
    {
        // The row overload's garrison term is tested directly elsewhere; this is the truth
        // overload's own path — reading `world.Entities` for whoever stands in the sector — which
        // nothing else in this file exercises (every other truth-overload fixture has no entities
        // at all, so that `.Where(...).Sum(...)` lambda otherwise never runs).
        var withSource = new WorldSector { SectorId = "s-source", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        var garrisoned = new WorldSector { SectorId = "s-garrison", OwnerFactionId = "f1" };
        var legion = new WorldEntity
        {
            EntityId = "e1", Kind = WorldEntityKind.Legion, OwnerFactionId = "f1", AtSectorId = "s-garrison",
            Members = new[]
            {
                new WorldEntityMember { SpeciesId = "sp1" },
                new WorldEntityMember { SpeciesId = "sp2" }
            }
        };
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "test", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { garrisoned, withSource }.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),
            Entities = new[] { legion }
        };

        var withGarrison = LoamUpkeep.For(world, garrisoned);
        var withoutGarrison = LoamUpkeep.For(0, garrisoned.DevelopmentLevel, garrisoned.DangerBand, garrisoned.FractureIntensityMilli, 1000, 1000);

        Assert.Equal(withoutGarrison + 2 * LoamPolicy.GarrisonUpkeepPerMember, withGarrison);
    }

    [Fact]
    public void A_faction_with_no_loam_source_anywhere_is_exempt_entirely()
    {
        var only = new WorldSector { SectorId = "s", OwnerFactionId = "f1", DevelopmentLevel = 5, DangerBand = 5 };
        var world = WorldWith(only, new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" });

        Assert.Equal(0, LoamUpkeep.For(world, only));
    }

    // world-stage W10 (re-homed from `world-numbers`): the breakdown must recombine to exactly the
    // same total `For` already returns — the whole point of a decomposition is that it never drifts
    // from the number it decomposes.

    [Theory]
    [InlineData(4, 2, 1, 1000, 1000, 1000)]
    [InlineData(4, 2, 1, 500, 1000, 1000)]
    [InlineData(4, 2, 1, 2000, 1000, 1000)]
    [InlineData(0, 0, 0, 1000, 500, 1000)]
    [InlineData(3, 0, 0, 333, 777, 444)]
    public void The_row_breakdowns_operands_recombine_to_exactly_the_same_total_as_For(
        int garrisonMembers, int developmentLevel, int dangerBand, int intensityMilli, int handicapMilli, int seasonMilli)
    {
        var breakdown = LoamUpkeep.Breakdown(garrisonMembers, developmentLevel, dangerBand, intensityMilli, handicapMilli, seasonMilli);
        var expected = LoamUpkeep.For(garrisonMembers, developmentLevel, dangerBand, intensityMilli, handicapMilli, seasonMilli);

        Assert.Equal(
            LoamPolicy.BaseUpkeepPerSector
            + (long)garrisonMembers * LoamPolicy.GarrisonUpkeepPerMember
            + LoamPolicy.DevelopmentAndDangerUpkeep(developmentLevel, dangerBand),
            breakdown.Sum);
        Assert.Equal(expected, breakdown.Total);
    }

    [Fact]
    public void The_truth_overloads_breakdown_recombines_to_exactly_the_same_total_as_For()
    {
        var withSource = new WorldSector { SectorId = "s-source", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        var sector = new WorldSector { SectorId = "s-target", OwnerFactionId = "f1", DevelopmentLevel = 3, DangerBand = 2 };
        var world = WorldWith(sector, new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" }, withSource);

        var breakdown = LoamUpkeep.BreakdownFor(world, sector);
        Assert.Equal(LoamUpkeep.For(world, sector), breakdown.Total);
        Assert.True(breakdown.Total > 0, "a developed, dangerous, owned sector with a loam source must cost something");
    }

    /// <summary>
    /// world-map W48: `BreakdownFor` reads the real season off `world.CurrentTurn` — every one of
    /// the four authored seasons must index cleanly into `Seasons.UpkeepMilli` (catching an
    /// off-by-one in the tuning array itself), at whatever turn actually lands in each.
    ///
    /// Updated by world-map W58, which turned the season upkeep term on for real
    /// (gk-core/data/tuning/world.v5.json: `upkeepMilli` [1000,1000,1000,1000] → [1000,850,1100,1400],
    /// `monthsPerSeason` 3 → 1): the days-per-season and the expected multiplier are now both read
    /// from the live configured tuning rather than hardcoded (`84` assumed `monthsPerSeason` stayed
    /// 3 forever, and `1000` assumed every season stayed identity forever — both assumptions this
    /// task's own tuning change breaks), matching `TurnCalendarSeasonTests`' own established
    /// "read the real tuning, not a literal" discipline one file over.
    /// </summary>
    [Theory]
    [InlineData(0)] // season 0
    [InlineData(1)] // season 1
    [InlineData(2)] // season 2
    [InlineData(3)] // season 3
    [InlineData(4)] // wraps back to season 0
    public void The_truth_overload_reads_the_real_season_for_every_authored_season_index(int seasonMultiple)
    {
        var daysPerSeason = TurnCalendar.DaysPerMonth * TurnCalendar.MonthsPerSeason;
        var turn = seasonMultiple * daysPerSeason;
        var expectedSeason = seasonMultiple % TurnCalendar.SeasonCount;

        var withSource = new WorldSector { SectorId = "s-source", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        var sector = new WorldSector { SectorId = "s-target", OwnerFactionId = "f1" };
        var world = WorldWith(sector, new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" }, withSource) with
        {
            CurrentTurn = turn
        };

        var breakdown = LoamUpkeep.BreakdownFor(world, sector);
        Assert.Equal(expectedSeason, TurnCalendar.SeasonOf(turn)); // the test's own premise, proven rather than assumed
        Assert.Equal(WorldTuningHub.Tuning.Seasons.UpkeepMilli[expectedSeason], breakdown.SeasonMilli);
    }

    /// <summary>Hold-allowance (spec-hold-allowance.md §What does NOT change): upkeep is
    /// stance-agnostic — a holder eats exactly what a marcher eats. Stance appears in neither
    /// `LoamUpkeep` signature; this pins the property, not the signature.</summary>
    [Fact]
    public void Upkeep_ignores_stance_a_holder_costs_what_a_marcher_costs()
    {
        var withSource = new WorldSector { SectorId = "s-source", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        var garrisoned = new WorldSector { SectorId = "s-garrison", OwnerFactionId = "f1" };
        WorldEntity legion(string stance) => new()
        {
            EntityId = "e1", Kind = WorldEntityKind.Legion, OwnerFactionId = "f1", AtSectorId = "s-garrison",
            Stance = stance,
            Members = new[]
            {
                new WorldEntityMember { SpeciesId = "sp1" },
                new WorldEntityMember { SpeciesId = "sp2" }
            }
        };
        WorldState worldWith(string stance) => new()
        {
            WorldId = "w", TemplateId = "test", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { garrisoned, withSource }.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),
            Entities = new[] { legion(stance) }
        };

        var marching = LoamUpkeep.For(worldWith(MovementPolicy.March), garrisoned);
        var holding = LoamUpkeep.For(worldWith(MovementPolicy.Hold), garrisoned);

        Assert.True(marching > 0, "non-vacuity: the garrisoned sector must cost something");
        Assert.Equal(marching, holding);
        Assert.Equal(
            LoamUpkeep.BreakdownFor(worldWith(MovementPolicy.March), garrisoned),
            LoamUpkeep.BreakdownFor(worldWith(MovementPolicy.Hold), garrisoned));
    }

    [Fact]
    public void An_unowned_sectors_breakdown_is_every_field_zero_not_just_a_zero_total()
    {
        var sector = new WorldSector { SectorId = "s", OwnerFactionId = null, DevelopmentLevel = 5, DangerBand = 5 };
        var world = WorldWith(sector, new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" });

        var breakdown = LoamUpkeep.BreakdownFor(world, sector);
        Assert.Equal(0, breakdown.Base);
        Assert.Equal(0, breakdown.Garrison);
        Assert.Equal(0, breakdown.Development);
        Assert.Equal(0, breakdown.Danger);
        Assert.Equal(0, breakdown.Total);
    }
}
