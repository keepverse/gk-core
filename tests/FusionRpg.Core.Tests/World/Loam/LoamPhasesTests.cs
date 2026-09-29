using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L12/L13 acceptance (spec-loam-turn.md): the named scenarios. Each fixture is built to make one
/// thing true, not against `first-light`.
/// </summary>
public class LoamPhasesTests
{
    const string Phase = "Test";

    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSector Sector(string id, string? owner, long stock = 0, int stability = 1000, int development = 0, int danger = 0, IReadOnlyList<WorldSlot>? slots = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = owner, LoamStock = stock, StabilityMilli = stability,
            DevelopmentLevel = development, DangerBand = danger, Slots = slots ?? Array.Empty<WorldSlot>()
        };

    static WorldLane Lane(string id, string from, string to, LaneState state = LaneState.Open) => new()
    {
        LaneId = id, FromSectorId = from, ToSectorId = to, TypeId = LaneTypeCatalog.RiftLaneTypeId, State = state
    };

    static WorldState Turn(WorldState world) =>
        LoamPhases.Pressure(LoamPhases.Production(world, new TurnReport(), Phase), new TurnReport(), Phase);

    static WorldSector Find(WorldState world, string id) => world.Sectors.Single(s => s.SectorId == id);

    [Fact]
    public void L15_ruleset_version_has_moved_past_3_now_that_production_and_pressure_are_not_pass_throughs()
    {
        // >= rather than a hardcoded value: later modules (L27) bump it again for their own real
        // behaviour changes, and this fact — L15's own — stays true regardless.
        Assert.True(TurnEngine.RulesetVersion >= 4);
    }

    // ---- Production (L12) ----

    [Fact]
    public void A_rootbed_sector_gains_its_seep_each_turn()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { Sector("s", "f1", stock: 0, slots: new[] { Rootbed(0) }) }
        };

        var after = LoamPhases.Production(world, new TurnReport(), Phase);
        Assert.Equal(LoamPolicy.SeepPerTurn, Find(after, "s").LoamStock);
    }

    [Fact]
    public void Overflow_above_capacity_is_lost_and_reported_naming_the_sector()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { Sector("s", "f1", stock: LoamPolicy.LoamCapacity - 10, slots: new[] { Rootbed(0) }) }
        };

        var report = new TurnReport();
        var after = LoamPhases.Production(world, report, Phase);

        Assert.Equal(LoamPolicy.LoamCapacity, Find(after, "s").LoamStock);
        var overflow = Assert.Single(report.Entries, e => e.Detail.StartsWith("loam.overflow:"));
        Assert.Equal("s", overflow.SectorId);
    }

    [Fact]
    public void Production_is_pure_and_deterministic()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { Sector("s", "f1", slots: new[] { Rootbed(0) }) }
        };

        var a = LoamPhases.Production(world, new TurnReport(), Phase);
        var b = LoamPhases.Production(world, new TurnReport(), Phase);
        Assert.Equal(WorldCanonical.Write(a), WorldCanonical.Write(b));
    }

    // ---- Pressure (L13) ----

    [Fact]
    public void A_chained_rootbed_sector_holds_forever()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { Sector("s", "f1", stock: 0, slots: new[] { Rootbed(0) }) }
        };

        for (var i = 0; i < 50; i++)
            world = Turn(world);

        Assert.Equal(1000, Find(world, "s").StabilityMilli);
        Assert.NotEqual(SectorPhase.Lost, Find(world, "s").Phase);
    }

    [Fact]
    public void A_cut_sector_runs_down_and_is_lost()
    {
        // No rootbed, no lane to anything else — its own singleton component from turn one, so
        // every turn's upkeep is a pure shortfall against a stock that only ever shrinks. A second,
        // unconnected sector gives the faction a rootbed *somewhere* so G-C's "no source anywhere"
        // exemption does not swallow "s"'s upkeep whole — the two never share a component.
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[]
            {
                Sector("s", "f1", stock: 0, stability: 1000, development: 2, danger: 1),
                Sector("elsewhere", "f1", stock: 0, slots: new[] { Rootbed(0) })
            }
        };

        var turnsRun = 0;
        while (Find(world, "s").Phase != SectorPhase.Lost && turnsRun < 200)
        {
            world = Turn(world);
            turnsRun++;
        }

        Assert.Equal(SectorPhase.Lost, Find(world, "s").Phase);
        Assert.Null(Find(world, "s").OwnerFactionId);
        Assert.True(turnsRun is > 1 and < 200, $"expected a countdown of several turns, took {turnsRun}");
    }

    [Fact]
    public void A_component_is_saved_by_its_own_yield_the_same_turn()
    {
        // Production runs before Pressure. A sector with a rootbed and modest upkeep should never
        // see a shortfall, because its own seep covers what it owes before Pressure ever asks.
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { Sector("s", "f1", stock: 0, slots: new[] { Rootbed(0) }) } // upkeep 10, seep 50
        };

        var report = new TurnReport();
        var produced = LoamPhases.Production(world, report, Phase);
        var pressured = LoamPhases.Pressure(produced, report, Phase);

        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("loam.shortfall:"));
        Assert.Equal(1000, Find(pressured, "s").StabilityMilli);
    }

    [Fact]
    public void A_rich_core_carries_a_poor_frontier_indefinitely()
    {
        var rich = Sector("rich", "f1", stock: 0, slots: new[] { Rootbed(0) });
        var poor = Sector("poor", "f1", stock: 0, development: 3, danger: 2); // no source of its own
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { rich, poor },
            Lanes = new[] { Lane("l", "rich", "poor") }
        };

        for (var i = 0; i < 50; i++)
            world = Turn(world);

        Assert.NotEqual(SectorPhase.Lost, Find(world, "poor").Phase);
        Assert.True(Find(world, "poor").StabilityMilli > 0);
    }

    [Fact]
    public void Severing_splits_the_economy_and_the_far_half_starves_alone()
    {
        var rich = Sector("rich", "f1", stock: 0, slots: new[] { Rootbed(0) });
        var poor = Sector("poor", "f1", stock: 0, development: 3, danger: 2);
        var severed = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { rich, poor },
            Lanes = new[] { Lane("l", "rich", "poor", LaneState.Severed) }
        };

        var world = severed;
        var turnsRun = 0;
        while (Find(world, "poor").Phase != SectorPhase.Lost && turnsRun < 200)
        {
            world = Turn(world);
            turnsRun++;
        }

        Assert.Equal(SectorPhase.Lost, Find(world, "poor").Phase);
        Assert.Equal(1000, Find(world, "rich").StabilityMilli); // the near half is untouched
    }

    [Fact]
    public void A_non_default_handicap_is_announced_exactly_once_per_faction_per_turn()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[]
            {
                new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1", UpkeepHandicapMilli = 500 },
                new WorldFaction { FactionId = "f2", Kind = WorldFactionKind.Zomboss, Name = "F2" }   // default 1000 — silent
            },
            Sectors = new[]
            {
                Sector("a", "f1", slots: new[] { Rootbed(0) }),
                Sector("b", "f1", development: 1),
                Sector("c", "f2", slots: new[] { Rootbed(0) })
            },
            Lanes = new[] { Lane("l-ab", "a", "b") }
        };

        var report = new TurnReport();
        LoamPhases.Pressure(world, report, Phase);

        var handicapEntries = report.Entries.Where(e => e.Detail.StartsWith("loam.handicap:")).ToList();
        var f1Entry = Assert.Single(handicapEntries);
        Assert.Equal("f1", f1Entry.Subject);
        Assert.Equal("loam.handicap:500", f1Entry.Detail);
        Assert.DoesNotContain(report.Entries, e => e.Subject == "f2" && e.Detail.StartsWith("loam.handicap:"));

        // world-stage W12 (fog defect A): a faction-scoped line names no ground, so `Audience` is
        // the only thing that stops it reaching every viewer.
        Assert.Equal("f1", f1Entry.Audience);
    }

    [Fact]
    public void The_weakest_ground_goes_first_not_an_arbitrary_sector()
    {
        // world-map W55 (empire-economy-ssot.md A8): `danger: N + 2 * development` compensates the
        // new development-yield term — `DevelopmentYieldPerLevel(6) / DangerUpkeepPerBand(3) == 2`
        // at the real configured tuning — reproducing the exact pre-W55 upkeep-minus-production
        // balance for a sourceless sector, so "harsh" stays the worse balance it always was.
        var rootbed = Sector("source", "f1", stock: 0, slots: new[] { Rootbed(0) });
        var mild = Sector("mild", "f1", stock: 0, development: 1, danger: 0 + 2 * 1);
        var harsh = Sector("harsh", "f1", stock: 0, development: 5, danger: 4 + 2 * 5); // worse balance than "mild"
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { rootbed, mild, harsh },
            Lanes = new[] { Lane("l1", "source", "mild"), Lane("l2", "mild", "harsh") }
        };

        var report = new TurnReport();
        // Drain the rootbed's own stock advantage first by running a few turns with no production
        // gap — cheaper to just run several turns and check "harsh" degrades before "mild".
        for (var i = 0; i < 30 && Find(world, "harsh").StabilityMilli == 1000; i++)
        {
            report = new TurnReport();
            world = LoamPhases.Pressure(LoamPhases.Production(world, report, Phase), report, Phase);
        }

        Assert.True(Find(world, "harsh").StabilityMilli < 1000, "the harsher sector must degrade before the milder one");
        Assert.Equal(1000, Find(world, "mild").StabilityMilli);
    }

    /// <summary>W25: `Pressure`'s own `ceded` map is threaded straight into `LoamForecast.Weakest` —
    /// filing "mild" as the faction's own choice overrides the default worst-balance pick ("harsh")
    /// without a second rule, and leaves "harsh" untouched instead.</summary>
    [Fact]
    public void Pressures_ceded_map_overrides_which_sector_absorbs_the_shortfall()
    {
        var mild = Sector("mild", "f1", stock: 0, stability: 1000, development: 1, danger: 0);
        var harsh = Sector("harsh", "f1", stock: 0, stability: 1000, development: 5, danger: 4);
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { mild, harsh, Sector("elsewhere", "f1", stock: 0, slots: new[] { Rootbed(0) }) },
            Lanes = new[] { Lane("l", "mild", "harsh") }
        };

        var produced = LoamPhases.Production(world, new TurnReport(), Phase);
        var withoutCede = LoamPhases.Pressure(produced, new TurnReport(), Phase);
        var withCede = LoamPhases.Pressure(produced, new TurnReport(), Phase,
            ceded: new Dictionary<string, string> { ["f1"] = "mild" });

        Assert.True(Find(withoutCede, "harsh").StabilityMilli < 1000, "default ordering fades the harsher sector");
        Assert.Equal(1000, Find(withoutCede, "mild").StabilityMilli);

        Assert.True(Find(withCede, "mild").StabilityMilli < 1000, "the ceded sector must absorb the shortfall instead");
        Assert.Equal(1000, Find(withCede, "harsh").StabilityMilli);
    }
}
