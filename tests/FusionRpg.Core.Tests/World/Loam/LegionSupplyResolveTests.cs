using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L27 (spec-loam-legions.md): `LegionSupply.Resolve` wired after `LoamPhases.Pressure` — a legion
/// beyond supply burns toward zero and is destroyed outright, one inside supply tops up for free
/// from whatever the pool has left after sector upkeep, never burning.
/// </summary>
public class LegionSupplyResolveTests
{
    const string Dave = "dave";

    static WorldEntity Legion(string id, string atSectorId, int fighters, int bearers, long carriedLoam) => new()
    {
        EntityId = id, Kind = WorldEntityKind.Legion, OwnerFactionId = Dave, AtSectorId = atSectorId,
        CarriedLoam = carriedLoam,
        Members = Enumerable.Range(0, fighters)
            .Select(_ => new WorldEntityMember { SpeciesId = "grunt" })
            .Concat(Enumerable.Range(0, bearers)
                .Select(_ => new WorldEntityMember { SpeciesId = "grunt", Role = WorldEntityMemberRole.Bearer }))
            .ToList()
    };

    static WorldState Fixture(long homeStock = 0) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = Dave, Kind = WorldFactionKind.Player, Name = "Dave" } },
        Sectors = new[]
        {
            new WorldSector
            {
                SectorId = "home", TypeId = "stable", OwnerFactionId = Dave, Phase = SectorPhase.Held,
                LoamStock = homeStock,
                Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId, State = SlotState.Claimed, OwnerFactionId = Dave } }
            },
            new WorldSector { SectorId = "field", TypeId = "stable", Phase = SectorPhase.Unknown }
        }
    };

    [Fact]
    public void A_legion_beyond_supply_survives_exactly_capacity_over_burn_turns()
    {
        var legion = Legion("legion", atSectorId: "field", fighters: 2, bearers: 1, carriedLoam: 0);
        var capacity = LegionSupply.Capacity(legion);
        var burn = LegionSupply.Burn(legion);
        var expectedLeash = (int)(capacity / burn);

        var world = Fixture() with { Entities = new[] { legion with { CarriedLoam = capacity } } };

        for (var i = 0; i < expectedLeash; i++)
        {
            world = LegionSupply.Resolve(world, new TurnReport(), "pressure");
            Assert.Contains(world.Entities, e => e.EntityId == "legion");
        }

        world = LegionSupply.Resolve(world, new TurnReport(), "pressure");
        Assert.DoesNotContain(world.Entities, e => e.EntityId == "legion");
    }

    [Fact]
    public void Destruction_is_reported_the_turn_it_happens()
    {
        var legion = Legion("legion", atSectorId: "field", fighters: 3, bearers: 0, carriedLoam: 0);
        var world = Fixture() with { Entities = new[] { legion } };

        var report = new TurnReport();
        LegionSupply.Resolve(world, report, "pressure");

        Assert.Contains(report.Entries, e => e.Detail.StartsWith("legion.starved"));
    }

    [Fact]
    public void A_legion_in_supply_tops_up_from_what_the_pool_has_left_after_upkeep()
    {
        var legion = Legion("legion", atSectorId: "home", fighters: 2, bearers: 1, carriedLoam: 0);
        var capacity = LegionSupply.Capacity(legion);

        // Enough left in the pool (post-Pressure, by construction of this fixture) to fully cover
        // the legion's demand.
        var world = Fixture(homeStock: capacity + 100) with { Entities = new[] { legion } };

        var result = LegionSupply.Resolve(world, new TurnReport(), "pressure");

        Assert.Equal(capacity, result.Entities.Single(e => e.EntityId == "legion").CarriedLoam);
        Assert.Equal(100, result.Sectors.Single(s => s.SectorId == "home").LoamStock);
    }

    [Fact]
    public void A_top_up_is_reported_with_the_factions_own_audience()
    {
        // world-stage W12 (fog defect A): `legion.topup` names no ground, so `Audience` is the only
        // thing that stops it reaching every viewer.
        var legion = Legion("legion", atSectorId: "home", fighters: 2, bearers: 1, carriedLoam: 0);
        var capacity = LegionSupply.Capacity(legion);
        var world = Fixture(homeStock: capacity + 100) with { Entities = new[] { legion } };

        var report = new TurnReport();
        LegionSupply.Resolve(world, report, "pressure");

        var topup = Assert.Single(report.Entries, e => e.Detail.StartsWith("legion.topup:"));
        Assert.Equal(Dave, topup.Audience);
    }

    [Fact]
    public void A_top_up_never_exceeds_what_the_pool_actually_holds()
    {
        var legion = Legion("legion", atSectorId: "home", fighters: 2, bearers: 1, carriedLoam: 0);
        var capacity = LegionSupply.Capacity(legion);

        // Only half of what the legion wants is actually available.
        var world = Fixture(homeStock: capacity / 2) with { Entities = new[] { legion } };

        var report = new TurnReport();
        var result = LegionSupply.Resolve(world, report, "pressure");

        Assert.Equal(capacity / 2, result.Entities.Single(e => e.EntityId == "legion").CarriedLoam);
        Assert.Equal(0, result.Sectors.Single(s => s.SectorId == "home").LoamStock);
        // world-stage W11: a partial refill that leaves the legion still short of capacity is not
        // a restoration — the signal must not fire on "received something", only on "made whole".
        Assert.DoesNotContain(report.Entries, e => e.Detail == "supply.restored");
    }

    [Fact]
    public void Supply_restored_fires_exactly_once_the_turn_a_cut_legions_deficit_is_fully_erased()
    {
        // world-stage W11 (re-homed from `world-playback` and ideal §2.3): the counterpart to
        // `supply.cut:` — nothing reported the reverse before this task. Simulated across three
        // separate `Resolve` calls (Core has no per-legion cut memory, so the test drives the
        // legion's position/loam directly rather than through movement) — this is what "cut, then
        // restored, then stable" looks like from `LegionSupply`'s own point of view.
        var burn = LegionSupply.Burn(Legion("probe", "field", fighters: 2, bearers: 1, carriedLoam: 0));
        // Starts with exactly one burn's worth, so the first cut turn lands it at 0 — depleted, but
        // not destroyed (destruction needs the burn to take it negative).
        var legion = Legion("legion", atSectorId: "field", fighters: 2, bearers: 1, carriedLoam: burn);
        var capacity = LegionSupply.Capacity(legion);

        // Turn 1: cut (standing on "field", which Fixture() gives no owner/rootbed, so it is never
        // part of any connected component) — burns down, and must not claim to be restored.
        var cutWorld = Fixture() with { Entities = new[] { legion } };
        var turn1Report = new TurnReport();
        var afterCut = LegionSupply.Resolve(cutWorld, turn1Report, "pressure");
        Assert.DoesNotContain(turn1Report.Entries, e => e.Detail == "supply.restored");
        var carriedAfterBurn = afterCut.Entities.Single(e => e.EntityId == "legion").CarriedLoam;
        Assert.Equal(0, carriedAfterBurn);

        // Turn 2: reconnected — now standing at "home", with enough pool to erase the whole deficit
        // in one turn.
        var reconnected = legion with { AtSectorId = "home", CarriedLoam = carriedAfterBurn };
        var restoredWorld = Fixture(homeStock: capacity + 100) with { Entities = new[] { reconnected } };
        var turn2Report = new TurnReport();
        var afterRestore = LegionSupply.Resolve(restoredWorld, turn2Report, "pressure");

        var restored = Assert.Single(turn2Report.Entries, e => e.Detail == "supply.restored");
        Assert.Equal("legion", restored.Subject);
        Assert.Equal("home", restored.SectorId);
        Assert.Equal(Dave, restored.Audience);
        Assert.Equal(capacity, afterRestore.Entities.Single(e => e.EntityId == "legion").CarriedLoam);

        // Turn 3: already whole — must not repeat.
        var stableWorld = restoredWorld with { Entities = afterRestore.Entities };
        var turn3Report = new TurnReport();
        LegionSupply.Resolve(stableWorld, turn3Report, "pressure");
        Assert.DoesNotContain(turn3Report.Entries, e => e.Detail == "supply.restored");
    }

    [Fact]
    public void A_top_ups_rounding_remainder_goes_to_the_first_legion_in_ordinal_order()
    {
        // Coverage found `DistributeToLegions`'s remainder-settlement line (mirroring
        // `LoamPhases.DrawProportionally`'s own) had never actually run: every prior test used a
        // single legion, so an even 50/50 split with one unit of drawn stock left over — the exact
        // case that line exists for — was never exercised.
        var legionA = Legion("legion-a", atSectorId: "home", fighters: 0, bearers: 1, carriedLoam: 0);
        var legionB = Legion("legion-b", atSectorId: "home", fighters: 0, bearers: 1, carriedLoam: 0);
        var demandEach = LegionSupply.Capacity(legionA);
        var totalDemand = demandEach * 2;

        // One short of covering both in full — the even split truncates, leaving exactly one unit
        // of drawn stock unaccounted for by the proportional shares alone.
        var world = Fixture(homeStock: totalDemand - 1) with { Entities = new[] { legionB, legionA } };

        var result = LegionSupply.Resolve(world, new TurnReport(), "pressure");

        var a = result.Entities.Single(e => e.EntityId == "legion-a").CarriedLoam;
        var b = result.Entities.Single(e => e.EntityId == "legion-b").CarriedLoam;
        Assert.Equal(totalDemand - 1, a + b);
        Assert.True(a > b, "the remainder must land on the first entity in ordinal id order, not array order");
    }

    [Fact]
    public void An_in_supply_legion_never_burns_even_with_no_bearers()
    {
        var legion = Legion("legion", atSectorId: "home", fighters: 3, bearers: 0, carriedLoam: 5);
        var world = Fixture(homeStock: 0) with { Entities = new[] { legion } };

        var report = new TurnReport();
        var result = LegionSupply.Resolve(world, report, "pressure");

        Assert.Equal(5, result.Entities.Single(e => e.EntityId == "legion").CarriedLoam);
        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("legion.starved"));
    }
}
