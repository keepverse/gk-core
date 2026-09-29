using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L38 (spec-loam-texture.md): the granary raises capacity, additive to `LoamProduction`'s existing
/// shape — the same discipline `loam-structures`' own well multiplier already followed.
/// </summary>
public class LoamTextureTests
{
    static WorldSector Sector(long loamStock, params WorldSlot[] slots) => new()
    {
        SectorId = "s", TypeId = "stable", OwnerFactionId = "dave", LoamStock = loamStock, Slots = slots
    };

    static WorldSlot Rootbed() => new() { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSlot Granary(int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = 1, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId,
        StructureId = "granary", ConstructionTurnsRemaining = constructionTurnsRemaining
    };

    [Fact]
    public void A_sector_with_an_active_granary_has_a_raised_effective_capacity()
    {
        var withGranary = Sector(0, Rootbed(), Granary());
        var without = Sector(0, Rootbed());

        Assert.Equal(LoamPolicy.LoamCapacity + LoamPolicy.GranaryCapacityBonus, LoamPhases.EffectiveCapacity(withGranary));
        Assert.Equal(LoamPolicy.LoamCapacity, LoamPhases.EffectiveCapacity(without));
    }

    [Fact]
    public void A_granary_still_under_construction_does_not_raise_capacity_yet()
    {
        var sector = Sector(0, Rootbed(), Granary(constructionTurnsRemaining: 2));
        Assert.Equal(LoamPolicy.LoamCapacity, LoamPhases.EffectiveCapacity(sector));
    }

    [Fact]
    public void A_sector_with_a_granary_accepts_more_stock_before_overflowing()
    {
        var world = new WorldState
        {
            Sectors = new[] { Sector(LoamPolicy.LoamCapacity - 10, Rootbed(), Granary()) }
        };
        var report = new TurnReport();

        var result = LoamPhases.Production(world, report, "production");

        var stock = result.Sectors[0].LoamStock;
        Assert.True(stock > LoamPolicy.LoamCapacity, $"granary-equipped stock {stock} did not exceed the base cap");
        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("loam.overflow"));
    }

    [Fact]
    public void Overflow_above_the_raised_cap_is_still_lost_and_still_reported()
    {
        var world = new WorldState
        {
            Sectors = new[] { Sector(LoamPolicy.LoamCapacity + LoamPolicy.GranaryCapacityBonus - 5, Rootbed(), Granary()) }
        };
        var report = new TurnReport();

        var result = LoamPhases.Production(world, report, "production");

        Assert.Equal(LoamPolicy.LoamCapacity + LoamPolicy.GranaryCapacityBonus, result.Sectors[0].LoamStock);
        Assert.Contains(report.Entries, e => e.Detail.StartsWith("loam.overflow") && e.SectorId == "s");
    }

    [Fact]
    public void A_rootbed_with_no_granary_overflows_exactly_as_it_does_today()
    {
        var world = new WorldState
        {
            Sectors = new[] { Sector(LoamPolicy.LoamCapacity - 5, Rootbed()) }
        };
        var report = new TurnReport();

        var result = LoamPhases.Production(world, report, "production");

        Assert.Equal(LoamPolicy.LoamCapacity, result.Sectors[0].LoamStock);
        Assert.Contains(report.Entries, e => e.Detail.StartsWith("loam.overflow"));
    }

    // ---- L39/L40: fade contagion and Fracture surges, built and proven together -----------------

    [Fact]
    public void Contagion_adds_directly_to_the_pre_clamp_sum()
    {
        var withoutPressure = FadePolicy.DecayFor(deficitMagnitude: 0);
        var withPressure = FadePolicy.DecayFor(deficitMagnitude: 0, pressureMilli: 50);

        Assert.Equal(withoutPressure + 50, withPressure);
    }

    [Fact]
    public void A_surge_scales_the_whole_pre_clamp_sum_not_the_clamped_output()
    {
        // A deficit small enough that neither the base decay nor the surge alone reaches the
        // ceiling, so the multiplication is actually visible in the result.
        var baseline = FadePolicy.DecayFor(deficitMagnitude: 100);
        var surged = FadePolicy.DecayFor(deficitMagnitude: 100, surge: true);

        Assert.True(surged > baseline);
        Assert.Equal(baseline * LoamPolicy.SurgeDecayMultiplierMilli / 1000, surged);
    }

    [Fact]
    public void The_single_most_important_test_a_maxed_out_sector_under_contagion_and_surge_together_never_exceeds_the_ceiling()
    {
        // A deficit already large enough to hit MaxDecayMilli on its own, contagion pressure piled
        // on top, and a surge active the same turn — the exact combination the audit's first draft
        // would have let overshoot (a contagion-inflated decay already clamped to 300, then scaled
        // x1.5, becomes 450). The corrected shape scales the *input*, so it cannot.
        var hugeDeficit = 100_000L;
        var maxedOut = FadePolicy.DecayFor(hugeDeficit, pressureMilli: LoamPolicy.MaxPressureMilli, surge: true);

        Assert.Equal(LoamPolicy.MaxDecayMilli, maxedOut);
    }

    [Fact]
    public void A_recovering_sector_is_never_touched_by_pressure_or_surge()
    {
        // "A surge makes holding harder, it does not make giving up easier" — Apply's positive
        // (recovering) branch must ignore both parameters entirely.
        var withoutExtras = FadePolicy.Apply(500, balance: 1);
        var withExtras = FadePolicy.Apply(500, balance: 1, pressureMilli: 999, surge: true);

        Assert.Equal(withoutExtras, withExtras);
    }

    /// <summary>
    /// A two-sector component joined by one lane — "poor" (no rootbed of its own, high development,
    /// a real drag on the shared pool) and "rich" (holds the component's only rootbed). Together
    /// they still run a shortfall, so `Weakest` (worse per-sector balance) always picks "poor" —
    /// proving the pressure raise on "rich" is contagion from its neighbour fading, not "rich"
    /// fading on its own account.
    ///
    /// `DangerBand = 20` (world-map W55, empire-economy-ssot.md A8): once `LoamProduction.For` gains
    /// a real development-yield term, `DevelopmentLevel` alone can no longer make a sourceless
    /// sector a net drag — A8 requires yield to exceed upkeep, so any positive `DevelopmentLevel` is
    /// now a net *contributor*. `DangerBand = 2 * DevelopmentLevel` compensates exactly
    /// (`DevelopmentYieldPerLevel(6) / DangerUpkeepPerBand(3) == 2` at the real configured tuning),
    /// reproducing this fixture's original pre-W55 upkeep-minus-production balance precisely.
    /// </summary>
    static WorldState ContagionFixture(int poorPressure = 0, int richPressure = 0) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
        Sectors = new[]
        {
            Sector(0, Rootbed()) with { SectorId = "rich", StabilityMilli = 1000, PressureMilli = richPressure },
            new WorldSector
            {
                SectorId = "poor", TypeId = "stable", OwnerFactionId = "dave", StabilityMilli = 1000,
                DevelopmentLevel = 10, DangerBand = 20, PressureMilli = poorPressure
            }
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l-rich-poor", FromSectorId = "rich", ToSectorId = "poor", TypeId = LaneTypeCatalog.RiftLaneTypeId }
        }
    };

    [Fact]
    public void A_sector_lane_adjacent_to_a_fading_neighbour_has_its_pressure_raised()
    {
        var world = ContagionFixture();
        var report = new TurnReport();

        var produced = LoamPhases.Production(world, report, "production");
        var pressured = LoamPhases.Pressure(produced, report, "pressure");

        // "poor" is disconnected from "rich" (each its own component — no shared pool), development
        // 10 guarantees a real shortfall against zero production, so "poor" fades every turn.
        Assert.Contains(report.Entries, e => e.Detail.StartsWith("loam.shortfall") && e.SectorId == "poor");
        Assert.Equal(
            Math.Min(LoamPolicy.MaxPressureMilli, LoamPolicy.ContagionPressurePerTurn),
            pressured.Sectors.Single(s => s.SectorId == "rich").PressureMilli);
    }

    [Fact]
    public void A_sector_with_no_fading_neighbour_decays_its_own_pressure_back_toward_zero()
    {
        var world = ContagionFixture(poorPressure: 0, richPressure: 200);
        var report = new TurnReport();

        // Give "poor" enough of its own stock that it pays its own way this turn — no shortfall, no
        // contagion source, so "rich"'s own elevated pressure should simply decay.
        var solvent = world with
        {
            Sectors = world.Sectors.Select(s => s.SectorId == "poor" ? s with { DevelopmentLevel = 0, LoamStock = 1000 } : s).ToList()
        };

        var produced = LoamPhases.Production(solvent, report, "production");
        var pressured = LoamPhases.Pressure(produced, report, "pressure");

        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("loam.shortfall"));
        Assert.Equal(
            Math.Max(0, 200 - LoamPolicy.PressureDecayPerTurn),
            pressured.Sectors.Single(s => s.SectorId == "rich").PressureMilli);
    }

    static (int Turn, ulong Seed) FindAPlagueTurn()
    {
        for (var turn = 28; turn <= 28 * 20; turn += 28)
            for (ulong seed = 0; seed < 500; seed++)
                if (FusionRpg.Core.World.Turn.TurnCalendar.Roll(turn, seed).Plague)
                    return (turn, seed);

        throw new InvalidOperationException("no Plague roll found in the search range — widen it");
    }

    [Fact]
    public void A_real_plague_turn_decays_a_fading_sector_further_than_the_same_turn_without_one()
    {
        var (plagueTurn, seed) = FindAPlagueTurn();
        Assert.True(FusionRpg.Core.World.Turn.TurnCalendar.Roll(plagueTurn, seed).Plague);

        var world = ContagionFixture();
        var report = new TurnReport();
        var produced = LoamPhases.Production(world, report, "production");

        var withoutSurge = LoamPhases.Pressure(produced, new TurnReport(), "pressure", turn: 1, seed: seed);
        var withSurge = LoamPhases.Pressure(produced, new TurnReport(), "pressure", turn: plagueTurn, seed: seed);

        var stabilityWithoutSurge = withoutSurge.Sectors.Single(s => s.SectorId == "poor").StabilityMilli;
        var stabilityWithSurge = withSurge.Sectors.Single(s => s.SectorId == "poor").StabilityMilli;

        Assert.True(stabilityWithSurge < stabilityWithoutSurge,
            $"a Plague turn should decay 'poor' further (without: {stabilityWithoutSurge}, with: {stabilityWithSurge})");
    }

    // ---- L41: the Unmade -----------------------------------------------------------------------

    static WorldSector LostBarren(int neglectedTurns) => new()
    {
        SectorId = "ruins", TypeId = "stable", Phase = SectorPhase.Lost, OwnerFactionId = null,
        NeglectedTurns = neglectedTurns,
        Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId } }
    };

    [Fact]
    public void The_unmade_spawn_on_a_map_with_a_wild_faction()
    {
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild" } },
            Sectors = new[] { LostBarren(LoamPolicy.UnmadeSpawnAfterTurns - 1) }
        };
        var report = new TurnReport();

        var result = LoamPhases.Pressure(world, report, "pressure");

        Assert.Contains(result.Entities, e =>
            string.Equals(e.OwnerFactionId, "wild", StringComparison.Ordinal) && e.AtSectorId == "ruins");
        Assert.Contains(report.Entries, e => e.Detail == "unmade.spawned:ruins");
        Assert.Equal(0, result.Sectors[0].NeglectedTurns); // the countdown resets — the ground is no longer empty
    }

    [Fact]
    public void The_unmade_do_not_spawn_on_a_map_with_no_wild_faction()
    {
        // Re-proven explicitly, per this program's own G-C lesson: an exemption is checked at every
        // place its logic could apply, not assumed to survive into a new mechanic untested.
        var world = new WorldState
        {
            Factions = Array.Empty<WorldFaction>(),
            Sectors = new[] { LostBarren(LoamPolicy.UnmadeSpawnAfterTurns - 1) }
        };
        var report = new TurnReport();

        var result = LoamPhases.Pressure(world, report, "pressure");

        Assert.Empty(result.Entities);
        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("unmade"));
        // The counter still climbs even with nobody to receive it — the exemption is on the spawn
        // itself, not on tracking neglect.
        Assert.Equal(LoamPolicy.UnmadeSpawnAfterTurns, result.Sectors[0].NeglectedTurns);
    }

    [Fact]
    public void The_unmade_do_not_spawn_before_the_neglect_threshold_is_reached()
    {
        // A mutation pass (2026-08-23, loam-texture Checkpoint 10) found that dropping the
        // `>= UnmadeSpawnAfterTurns` check entirely survived undetected — every other test happened
        // to start at exactly threshold-1. Starting fresh (0) proves the threshold is actually load-bearing.
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild" } },
            Sectors = new[] { LostBarren(0) }
        };

        var result = LoamPhases.Pressure(world, new TurnReport(), "pressure");

        Assert.Empty(result.Entities);
        Assert.Equal(1, result.Sectors[0].NeglectedTurns);
    }

    [Fact]
    public void The_unmade_do_not_spawn_onto_ground_something_already_occupies()
    {
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild" } },
            Sectors = new[] { LostBarren(LoamPolicy.UnmadeSpawnAfterTurns - 1) },
            Entities = new[]
            {
                new WorldEntity
                {
                    EntityId = "e-dave-1", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave", AtSectorId = "ruins",
                    Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } }
                }
            }
        };

        var result = LoamPhases.Pressure(world, new TurnReport(), "pressure");

        Assert.Single(result.Entities); // only the legion that was already there
    }

    [Fact]
    public void The_neglect_counter_resets_once_the_ground_is_no_longer_barren_and_lost()
    {
        var world = new WorldState
        {
            Sectors = new[] { LostBarren(3) with { Phase = SectorPhase.Held, OwnerFactionId = "dave" } }
        };

        var result = LoamPhases.Pressure(world, new TurnReport(), "pressure");

        Assert.Equal(0, result.Sectors[0].NeglectedTurns);
    }

    // ---- L42: wardens — retired (warden-freeze-fix, RulesetVersion 13) ---------------------------
    //
    // The owner withdrew the warden freeze on 2026-09-13 (warden-mortality-ideal.md). These tests
    // used to prove a warded sector neither faded nor recovered. They now prove the opposite contract:
    // a `WardenBindingId` left on a sector is data only, so `Pressure` treats that sector exactly
    // like its unwarded twin. Each test runs both worlds and compares, rather than trusting one
    // hand-computed number.

    static WorldFaction Dave() => new() { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" };

    /// <summary>The same world with every binding cleared: the comparison each warden test needs.</summary>
    static WorldState Unwarded(WorldState world) => world with
    {
        Sectors = world.Sectors.Select(s => s with { WardenBindingId = null }).ToList()
    };

    static IEnumerable<string> Lines(TurnReport report) =>
        report.Entries.Select(e => $"{e.Kind}|{e.Subject}|{e.Detail}|{e.SectorId}|{e.Audience}");

    /// <summary>A rootbed elsewhere so `LoamUpkeep`'s G-C exemption ("a faction with no loam source
    /// anywhere pays nothing") does not zero out the warded sector's own upkeep — unconnected, so it
    /// is its own separate component and cannot subsidise the warded one.</summary>
    static WorldSector HomeWithRootbed() => new()
    {
        SectorId = "home", TypeId = "stable", OwnerFactionId = "dave", StabilityMilli = 1000,
        LoamStock = 10_000, Slots = new[] { Rootbed() }
    };

    [Fact]
    public void A_warded_sector_fades_like_any_other_when_its_whole_component_cannot_pay()
    {
        // No rootbed, high development, zero stock — a guaranteed, un-payable shortfall — and it is
        // the only member of its own component. Under RulesetVersion 12 this was the "every member
        // is warded" case: no fade target, `loam.shortfall.unresolved`, stability frozen at 1000.
        var warded = new WorldSector
        {
            SectorId = "warded", TypeId = "stable", OwnerFactionId = "dave", StabilityMilli = 1000,
            DevelopmentLevel = 10, WardenBindingId = "creature-1",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId } }
        };
        var world = new WorldState { Factions = new[] { Dave() }, Sectors = new[] { HomeWithRootbed(), warded } };
        var report = new TurnReport();
        var twinReport = new TurnReport();

        var result = LoamPhases.Pressure(world, report, "pressure");
        var twin = LoamPhases.Pressure(Unwarded(world), twinReport, "pressure");

        var stability = result.Sectors.Single(s => s.SectorId == "warded").StabilityMilli;
        Assert.True(stability < 1000, $"a warded sector must fade under an unpaid shortfall, stayed at {stability}");
        Assert.Equal(twin.Sectors.Single(s => s.SectorId == "warded").StabilityMilli, stability);
        Assert.Contains(report.Entries, e => e.Detail.StartsWith("loam.shortfall:") && e.SectorId == "warded");
        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("loam.shortfall.unresolved:"));
        Assert.Equal(Lines(twinReport), Lines(report));
        // The binding itself is untouched: it stays on the sector as data.
        Assert.Equal("creature-1", result.Sectors.Single(s => s.SectorId == "warded").WardenBindingId);
    }

    [Fact]
    public void Weakest_picks_a_warded_sector_when_it_has_the_worse_balance()
    {
        // Two sectors sharing one empty pool: "warded" carries the worse balance and is warded;
        // "frontier" has a milder shortfall of its own. Under RulesetVersion 12 the exclusion pushed
        // the fade onto "frontier". With the freeze retired the worst balance takes it, binding or
        // not. The gap comes from `DangerBand` (upkeep with no matching yield), not development:
        // since world-map W55 development yields more than it costs, so a high `DevelopmentLevel`
        // would make "warded" the *better* balance. The precondition is asserted below, not assumed.
        var warded = new WorldSector
        {
            SectorId = "warded", TypeId = "stable", OwnerFactionId = "dave", StabilityMilli = 1000,
            DangerBand = 10, WardenBindingId = "creature-1",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId } }
        };
        var frontier = new WorldSector
        {
            SectorId = "frontier", TypeId = "stable", OwnerFactionId = "dave", StabilityMilli = 1000,
            DangerBand = 1,
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId } }
        };
        var world = new WorldState
        {
            Factions = new[] { Dave() },
            Sectors = new[] { HomeWithRootbed(), warded, frontier },
            Lanes = new[]
            {
                new WorldLane { LaneId = "l", FromSectorId = "warded", ToSectorId = "frontier", TypeId = LaneTypeCatalog.RiftLaneTypeId }
            }
        };
        Assert.True(
            LoamBalance.PerSector(world, warded) < LoamBalance.PerSector(world, frontier),
            "fixture precondition: the warded sector must carry the worse balance");
        var report = new TurnReport();
        var twinReport = new TurnReport();

        var result = LoamPhases.Pressure(world, report, "pressure");
        var twin = LoamPhases.Pressure(Unwarded(world), twinReport, "pressure");

        Assert.True(result.Sectors.Single(s => s.SectorId == "warded").StabilityMilli < 1000);
        Assert.Equal(1000, result.Sectors.Single(s => s.SectorId == "frontier").StabilityMilli);
        Assert.Contains(report.Entries, e => e.Detail.StartsWith("loam.shortfall:") && e.SectorId == "warded");
        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("loam.shortfall:") && e.SectorId == "frontier");
        Assert.Equal(
            twin.Sectors.Select(s => (s.SectorId, s.StabilityMilli)),
            result.Sectors.Select(s => (s.SectorId, s.StabilityMilli)));
        Assert.Equal(Lines(twinReport), Lines(report));
    }

    [Fact]
    public void A_warded_sector_recovers_like_any_other_when_its_component_pays_in_full()
    {
        // A solvent component (rich stock, low upkeep) recovers every member. Under RulesetVersion
        // 12 the warded member was skipped and held at 500. It now recovers like its unwarded twin.
        var warded = new WorldSector
        {
            SectorId = "warded", TypeId = "stable", OwnerFactionId = "dave", StabilityMilli = 500,
            LoamStock = 10_000, WardenBindingId = "creature-1",
            Slots = new[] { Rootbed() }
        };
        var world = new WorldState { Factions = new[] { Dave() }, Sectors = new[] { warded } };
        var report = new TurnReport();

        var result = LoamPhases.Pressure(world, report, "pressure");
        var twin = LoamPhases.Pressure(Unwarded(world), new TurnReport(), "pressure");

        var stability = result.Sectors.Single(s => s.SectorId == "warded").StabilityMilli;
        Assert.Equal(FadePolicy.Apply(500, balance: 1), stability);
        Assert.NotEqual(500, stability);
        Assert.Equal(twin.Sectors.Single(s => s.SectorId == "warded").StabilityMilli, stability);
    }

    [Fact]
    public void Capturing_a_warded_sector_ends_the_binding()
    {
        var warded = new WorldSector
        {
            SectorId = "warded", TypeId = "stable", OwnerFactionId = "zomboss", StabilityMilli = 1000,
            WardenBindingId = "creature-1",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = "seat", GuardState = GuardState.Cleared } }
        };
        var raider = new WorldEntity
        {
            EntityId = "e-dave-1", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave", AtSectorId = "warded",
            Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } }
        };
        var world = new WorldState
        {
            Factions = new[] { Dave(), new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Zomboss" } },
            Sectors = new[] { warded },
            Entities = new[] { raider }
        };
        var command = new WorldCommand
        {
            CommandId = "c1", Kind = WorldCommandKinds.Claim, CommanderId = "dave", EntityId = "e-dave-1", SectorId = "warded"
        };

        var result = FusionRpg.Core.World.Movement.ClaimResolver.Run(world, new[] { command }, new TurnReport(), "claim", turn: 1);

        var captured = result.Sectors.Single(s => s.SectorId == "warded");
        Assert.Equal("dave", captured.OwnerFactionId);
        Assert.Null(captured.WardenBindingId);
    }
}
