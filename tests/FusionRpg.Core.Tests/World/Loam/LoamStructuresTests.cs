using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L33 (spec-loam-structures.md): the well multiplies a rootbed's own seep — additive to
/// `LoamProduction`, not a rewrite, and a rootbed with no well behaves exactly as it does today.
/// </summary>
public class LoamStructuresTests
{
    static WorldSector Sector(params WorldSlot[] slots) => new()
    {
        SectorId = "s", TypeId = "stable", OwnerFactionId = "dave", Slots = slots
    };

    static WorldSlot Rootbed(string? structureId = null, int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId,
        StructureId = structureId, ConstructionTurnsRemaining = constructionTurnsRemaining
    };

    [Fact]
    public void A_rootbed_with_no_well_is_byte_identical_to_today()
    {
        var sector = Sector(Rootbed());
        Assert.Equal(LoamPolicy.SeepPerTurn, LoamProduction.For(sector));
    }

    [Fact]
    public void A_rootbed_with_a_well_yields_more_than_one_without()
    {
        var plain = Sector(Rootbed());
        var welled = Sector(Rootbed("well"));

        Assert.True(LoamProduction.For(welled) > LoamProduction.For(plain));
        Assert.Equal(
            LoamPolicy.SeepPerTurn * LoamPolicy.WellYieldMultiplierMilli / 1000,
            LoamProduction.For(welled));
    }

    [Fact]
    public void A_well_still_under_construction_contributes_nothing_extra()
    {
        var underConstruction = Sector(Rootbed("well", constructionTurnsRemaining: 2));
        Assert.Equal(LoamPolicy.SeepPerTurn, LoamProduction.For(underConstruction));
    }

    [Fact]
    public void A_well_finished_at_zero_remaining_is_active()
    {
        var finished = Sector(Rootbed("well", constructionTurnsRemaining: 0));
        Assert.Equal(
            LoamPolicy.SeepPerTurn * LoamPolicy.WellYieldMultiplierMilli / 1000,
            LoamProduction.For(finished));
    }

    [Fact]
    public void Multiple_rootbeds_are_each_multiplied_by_their_own_structure_independently()
    {
        var sector = Sector(
            Rootbed("well") with { SlotIndex = 0 },
            Rootbed() with { SlotIndex = 1 });

        var expected = LoamPolicy.SeepPerTurn * LoamPolicy.WellYieldMultiplierMilli / 1000
                       + LoamPolicy.SeepPerTurn;
        Assert.Equal(expected, LoamProduction.For(sector));
    }

    // ---- L34: the waystation and habitability's widened rule ---------------------------------

    static WorldSlot Seat(string? structureId = null, int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId,
        StructureId = structureId, ConstructionTurnsRemaining = constructionTurnsRemaining
    };

    [Fact]
    public void A_seat_with_an_active_waystation_is_habitable_and_yields_exactly_zero()
    {
        var sector = Sector(Seat("waystation"));

        Assert.True(Habitability.For(sector));
        Assert.Equal(0, LoamProduction.For(sector));
    }

    [Fact]
    public void A_seat_with_a_waystation_still_under_construction_is_not_yet_habitable()
    {
        var sector = Sector(Seat("waystation", constructionTurnsRemaining: 3));
        Assert.False(Habitability.For(sector));
    }

    [Fact]
    public void A_bare_seat_with_no_structure_is_not_habitable()
    {
        var sector = Sector(Seat());
        Assert.False(Habitability.For(sector));
    }

    [Fact]
    public void The_widened_belief_overload_agrees_with_the_truth_overload_for_a_waystation()
    {
        var sector = Sector(Seat("waystation"));
        Assert.Equal(
            Habitability.For(sector),
            Habitability.For(sector.Slots.Select(sl => (sl.SlotTypeId, sl.StructureId, sl.ConstructionTurnsRemaining))));
    }

    // ---- L35: construction — the exact activation turn ----------------------------------------

    [Fact]
    public void A_structure_is_inert_through_its_build_turns_and_active_on_the_exact_completion_pass()
    {
        var report = new TurnReport();
        var world = new WorldState
        {
            Sectors = new[] { Sector(Rootbed("well", constructionTurnsRemaining: LoamPolicy.WellBuildTurns)) }
        };

        for (var pass = 1; pass <= LoamPolicy.WellBuildTurns; pass++)
        {
            var before = world.Sectors[0].LoamStock;
            world = LoamPhases.Production(world, report, "production");
            var after = world.Sectors[0].LoamStock;

            var expectedGain = pass < LoamPolicy.WellBuildTurns
                ? LoamPolicy.SeepPerTurn
                : LoamPolicy.SeepPerTurn * LoamPolicy.WellYieldMultiplierMilli / 1000;

            Assert.Equal(expectedGain, after - before);
            Assert.Equal(LoamPolicy.WellBuildTurns - pass, world.Sectors[0].Slots[0].ConstructionTurnsRemaining);
        }
    }

    [Fact]
    public void Only_the_slot_actually_under_construction_decrements_a_neighbour_is_left_untouched()
    {
        // Coverage found `DecrementConstruction`'s per-slot pass-through branch had never run
        // alongside its decrement branch in the same sector — every prior fixture used a single-slot
        // sector, so a mix of "still building" and "nothing to build" slots was never exercised together.
        var sector = Sector(
            Rootbed("well", constructionTurnsRemaining: 2) with { SlotIndex = 0 },
            Rootbed() with { SlotIndex = 1 });
        var world = new WorldState { Sectors = new[] { sector } };

        var result = LoamPhases.Production(world, new TurnReport(), "production");

        Assert.Equal(1, result.Sectors[0].Slots[0].ConstructionTurnsRemaining);
        Assert.Null(result.Sectors[0].Slots[1].ConstructionTurnsRemaining);
    }

    [Fact]
    public void A_structure_with_zero_build_turns_is_active_immediately()
    {
        var sector = Sector(Rootbed("well", constructionTurnsRemaining: 0));
        Assert.Equal(
            LoamPolicy.SeepPerTurn * LoamPolicy.WellYieldMultiplierMilli / 1000,
            LoamProduction.For(sector));
    }

    // ---- L35: BuildResolver ---------------------------------------------------------------------

    static WorldEntity Founder(long carriedLoam) => new()
    {
        EntityId = "legion", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave", AtSectorId = "s",
        CarriedLoam = carriedLoam, Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } }
    };

    static WorldCommand BuildCommand(string structureId, int slotIndex = 0) => new()
    {
        CommanderId = "dave", CommandId = "b1", Kind = WorldCommandKinds.Build,
        EntityId = "legion", SectorId = "s", SlotIndex = slotIndex, StructureId = structureId
    };

    [Fact]
    public void A_well_founds_on_a_rootbed_and_spends_the_legions_own_carried_loam()
    {
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[] { Sector(Rootbed()) },
            Entities = new[] { Founder(LoamPolicy.WellCost) }
        };
        var report = new TurnReport();

        var result = BuildResolver.Run(world, new[] { BuildCommand("well") }, report, "snapshot");

        var slot = result.Sectors[0].Slots[0];
        Assert.Equal("well", slot.StructureId);
        Assert.Equal(LoamPolicy.WellBuildTurns, slot.ConstructionTurnsRemaining);
        Assert.Equal(0, result.Entities[0].CarriedLoam);
        Assert.Contains(report.Entries, e => e.Detail == "build.started:well");
    }

    [Fact]
    public void A_legion_that_cannot_afford_the_cost_is_refused()
    {
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[] { Sector(Rootbed()) },
            Entities = new[] { Founder(LoamPolicy.WellCost - 1) }
        };
        var report = new TurnReport();

        var result = BuildResolver.Run(world, new[] { BuildCommand("well") }, report, "snapshot");

        Assert.Null(result.Sectors[0].Slots[0].StructureId);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "build.cannot-afford");
    }

    [Fact]
    public void A_well_cannot_be_founded_on_a_seat()
    {
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[] { Sector(Seat()) },
            Entities = new[] { Founder(LoamPolicy.WellCost) }
        };
        var report = new TurnReport();

        var result = BuildResolver.Run(world, new[] { BuildCommand("well") }, report, "snapshot");

        Assert.Null(result.Sectors[0].Slots[0].StructureId);
        Assert.Contains(report.Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail.StartsWith("build.wrong-slot-kind"));
    }

    [Fact]
    public void An_already_occupied_slot_refuses_a_second_structure()
    {
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[] { Sector(Rootbed("well")) },
            Entities = new[] { Founder(LoamPolicy.WellCost) }
        };
        var report = new TurnReport();

        var result = BuildResolver.Run(world, new[] { BuildCommand("well") }, report, "snapshot");

        Assert.Equal("well", result.Sectors[0].Slots[0].StructureId); // unchanged
        Assert.Contains(report.Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail.StartsWith("build.occupied"));
    }

    [Fact]
    public void A_founder_who_no_longer_owns_the_sector_at_resolution_is_refused()
    {
        // A sector lost to fade (or conquest) later the same turn must not silently accept a Build
        // order admitted while it still belonged to the founder — re-validated at resolution, the
        // same discipline `ClaimResolver` already applies.
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[] { Sector(Rootbed()) with { OwnerFactionId = null, Phase = SectorPhase.Lost } },
            Entities = new[] { Founder(LoamPolicy.WellCost) }
        };
        var report = new TurnReport();

        var result = BuildResolver.Run(world, new[] { BuildCommand("well") }, report, "snapshot");

        Assert.Null(result.Sectors[0].Slots[0].StructureId);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "build.not-yours");
    }

    [Fact]
    public void A_sector_lost_to_fade_during_the_same_turn_refuses_its_own_build_order_at_resolution()
    {
        // End to end: a component in a guaranteed shortfall loses its only sector to fade during
        // Pressure, which runs before Snapshot resolves this same turn's Build order — proving the
        // refusal is a real consequence of turn order, not merely BuildResolver's own unit behaviour.
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                Sector(Rootbed()) with
                {
                    StabilityMilli = 1, DevelopmentLevel = 50, DangerBand = 50, LoamStock = 0
                }
            },
            Entities = new[] { Founder(LoamPolicy.WellCost) }
        };

        var build = new WorldCommand
        {
            CommanderId = "dave", CommandId = "b1", Kind = WorldCommandKinds.Build,
            EntityId = "legion", SectorId = "s", SlotIndex = 0, StructureId = "well"
        };

        var result = TurnEngine.Step(world, new[] { build }, seed: 1);

        Assert.Equal(SectorPhase.Lost, result.World.Sectors[0].Phase);
        Assert.Null(result.World.Sectors[0].Slots[0].StructureId);
        Assert.Contains(result.Report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "build.not-yours");
    }

    // ---- L36: `Lost` actually clears structure state -------------------------------------------

    /// <summary>
    /// "home" carries the faction's real rootbed — without it, `LoamUpkeep`'s G-C exemption ("no
    /// source anywhere → exempt entirely") would make the isolated construction site at "s" pay no
    /// upkeep at all and never fade, the same mirrored rule `SupplyGraph.cs` already has for the
    /// wild. No lane joins the two, so "s" still pools alone — its own separate, zero-production
    /// component — while the faction as a whole is a real, sourced empire.
    /// </summary>
    static WorldSector Home() => new()
    {
        SectorId = "home", TypeId = "stable", OwnerFactionId = "dave",
        Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId } }
    };

    [Fact]
    public void A_sector_lost_mid_construction_has_both_fields_cleared_the_same_turn_ownership_clears()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                Home(),
                Sector(Seat("waystation", constructionTurnsRemaining: 3)) with { StabilityMilli = 1 }
            },
            Entities = new[] { Founder(0) }
        };

        var result = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1);

        var sector = result.World.Sectors.Single(s => s.SectorId == "s");
        Assert.Equal(SectorPhase.Lost, sector.Phase);
        Assert.Null(sector.Slots[0].StructureId);
        Assert.Null(sector.Slots[0].ConstructionTurnsRemaining); // no partial refund either
    }

    [Fact]
    public void A_sustaining_legion_keeps_a_construction_site_alive_the_same_scenario_without_it_loses()
    {
        // Same starting shape both times: a barren Seat under construction, weak enough (stability
        // 100 against a 42-per-turn decay) to fade out before its four build turns finish — unless
        // something keeps paying the upkeep no production here ever will.
        WorldState Fixture() => new()
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                Home(),
                Sector(Seat("waystation", constructionTurnsRemaining: LoamPolicy.WaystationBuildTurns))
                    with { StabilityMilli = 100 }
            },
            Entities = new[] { Founder(1000) }
        };

        var sustain = new WorldCommand
        {
            CommanderId = "dave", CommandId = "sustain", Kind = WorldCommandKinds.Sustain,
            EntityId = "legion", Amount = 50
        };

        static WorldSector S(WorldState w) => w.Sectors.Single(x => x.SectorId == "s");

        var withoutSustain = Fixture();
        for (var turn = 0; turn < LoamPolicy.WaystationBuildTurns && S(withoutSustain).Phase != SectorPhase.Lost; turn++)
            withoutSustain = TurnEngine.Step(withoutSustain, Array.Empty<WorldCommand>(), seed: 1).World;

        Assert.Equal(SectorPhase.Lost, S(withoutSustain).Phase);
        Assert.Null(S(withoutSustain).Slots[0].StructureId);

        var withSustain = Fixture();
        for (var turn = 0; turn < LoamPolicy.WaystationBuildTurns; turn++)
        {
            // "legion" stands at "s" throughout — the fixture never moves it.
            withSustain = TurnEngine.Step(withSustain, new[] { sustain }, seed: 1).World;
            Assert.NotEqual(SectorPhase.Lost, S(withSustain).Phase);
        }

        Assert.Equal("waystation", S(withSustain).Slots[0].StructureId);
        Assert.Equal(0, S(withSustain).Slots[0].ConstructionTurnsRemaining);
        Assert.True(Habitability.For(S(withSustain)));
    }

    // ---- L37: the range rule (G5), and the accepted lockout -----------------------------------

    static WorldEntity FounderAt(string sectorId, long carriedLoam) => new()
    {
        EntityId = "founder", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave", AtSectorId = sectorId,
        CarriedLoam = carriedLoam, Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } }
    };

    static WorldCommand BuildWaystation(string sectorId) => new()
    {
        CommanderId = "dave", CommandId = "b1", Kind = WorldCommandKinds.Build,
        EntityId = "founder", SectorId = sectorId, SlotIndex = 0, StructureId = "waystation"
    };

    [Fact]
    public void A_waystation_founds_within_range_on_unmodified_two_hearths()
    {
        // "d-outpost" is Dave's own empty-Seat ground, two hops from "d-home" (his rootbed anchor)
        // via l-dh-df2 + l-df2-do — well inside `WaystationRangeHops`. The map itself is untouched;
        // only the founding legion (not part of the shipped scenario's forces) is added.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 1) with
        {
            Entities = new[] { FounderAt("d-outpost", LoamPolicy.WaystationCost) }
        };

        var result = BuildResolver.Run(world, new[] { BuildWaystation("d-outpost") }, new TurnReport(), "snapshot");

        var slot = result.Sectors.Single(s => s.SectorId == "d-outpost").Slots.Single(sl => sl.SlotIndex == 0);
        Assert.Equal("waystation", slot.StructureId);
    }

    [Fact]
    public void A_waystation_declines_beyond_range()
    {
        // Unmodified two-hearths has no natural far-away, founder-owned, empty-Seat target to test
        // the decline half against — by design, every Seat on the map sits close to its own side's
        // home cluster, and the corridor spine between them carries no Seat at all
        // (spec-loam-structures.md's own note on this). A dedicated shape exercises it directly.
        var shape = GraphShapes.From(600, "a-b", "b-c", "c-d", "d-e", "e-f", "f-g");
        var world = shape with
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = shape.Sectors
                .Select(s => s.SectorId switch
                {
                    "a" => s with { OwnerFactionId = "dave", Slots = new[] { Rootbed() } },
                    "g" => s with { OwnerFactionId = "dave", Slots = new[] { Seat() } },
                    _ => s with { OwnerFactionId = "dave" }
                })
                .ToList(),
            Entities = new[] { FounderAt("g", LoamPolicy.WaystationCost) }
        };

        var report = new TurnReport();
        var result = BuildResolver.Run(world, new[] { BuildWaystation("g") }, report, "snapshot");

        Assert.Null(result.Sectors.Single(s => s.SectorId == "g").Slots[0].StructureId);
        Assert.Contains(report.Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "build.out-of-range:g");
    }

    [Fact]
    public void A_faction_that_has_lost_its_only_habitable_anchors_is_permanently_locked_out_everywhere()
    {
        // Resolved, an audit finding accepted as intended (spec-loam-structures.md): losing every
        // Rootbed leaves a faction with zero eligible anchors, forever — not a bug to soften. "d-home"
        // and "d-flank-1" are the *only* two Rootbeds Dave starts with on this map; both lost (fade or
        // conquest, simulated here) leaves "d-outpost" — his own ground, one hop from where his
        // capital used to stand — permanently unbuildable too.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 1);
        var strandedDave = world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId is "d-home" or "d-flank-1" ? s with { OwnerFactionId = null } : s)
                .ToList(),
            Entities = new[] { FounderAt("d-outpost", LoamPolicy.WaystationCost) }
        };

        Assert.DoesNotContain(strandedDave.Sectors, s =>
            string.Equals(s.OwnerFactionId, "dave", StringComparison.Ordinal) && Habitability.For(s));

        var report = new TurnReport();
        var result = BuildResolver.Run(strandedDave, new[] { BuildWaystation("d-outpost") }, report, "snapshot");

        Assert.Null(result.Sectors.Single(s => s.SectorId == "d-outpost").Slots[0].StructureId);
        Assert.Contains(report.Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "build.out-of-range:d-outpost");
    }

    // ---- world-map W56: the yield kinds — soul conduit, extractor, hatchery --------------------

    static WorldSlot EssenceDeposit(string? structureId = null, int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = 0, SlotTypeId = "essence-deposit",
        StructureId = structureId, ConstructionTurnsRemaining = constructionTurnsRemaining
    };

    static WorldSlot ShardVein(string? structureId = null, int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = 0, SlotTypeId = "shard-vein",
        StructureId = structureId, ConstructionTurnsRemaining = constructionTurnsRemaining
    };

    static WorldSlot Lair(GuardState guardState, string? structureId = null, int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = 1, SlotTypeId = "lair", GuardState = guardState,
        StructureId = structureId, ConstructionTurnsRemaining = constructionTurnsRemaining
    };

    [Fact]
    public void The_three_new_rows_are_known_and_carry_the_new_Yield_kind()
    {
        foreach (var id in new[] { "soul-conduit", "extractor", "hatchery" })
        {
            Assert.True(StructureCatalog.IsKnown(id));
            Assert.Equal(StructureKind.Yield, StructureCatalog.Get(id).Kind);
        }
    }

    /// <summary>Places a structure on one real, named slot of the shipped `first-light` template —
    /// the same technique <see cref="WorldInvariantTests.Rule14_accepts_a_structure_on_the_slot_kind_it_was_built_for"/>
    /// already uses, so the *whole* sixteen-rule pipeline runs, not Rule14 in isolation.</summary>
    static WorldState WithStructureAt(string sectorId, int slotIndex, string structureId)
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var sectorIndex = world.Sectors.ToList().FindIndex(s => s.SectorId == sectorId);

        return world with
        {
            Sectors = world.Sectors
                .Select((s, i) => i != sectorIndex
                    ? s
                    : s with
                    {
                        Slots = s.Slots
                            .Select(sl => sl.SlotIndex != slotIndex ? sl : sl with { StructureId = structureId })
                            .ToList()
                    })
                .ToList()
        };
    }

    [Fact]
    public void A_soul_conduit_on_ember_hollows_essence_deposit_validates()
    {
        // ember-hollow slot 2 is an essence-deposit — the soul conduit's own RequiredSlotKind.
        WorldValidation.Validate(WithStructureAt("ember-hollow", slotIndex: 2, "soul-conduit"));
    }

    [Fact]
    public void An_extractor_on_verdant_shelfs_shard_vein_validates()
    {
        // verdant-shelf slot 2 is a shard-vein — the extractor's own RequiredSlotKind.
        WorldValidation.Validate(WithStructureAt("verdant-shelf", slotIndex: 2, "extractor"));
    }

    [Fact]
    public void A_hatchery_on_ember_hollows_lair_validates()
    {
        // ember-hollow slot 3 is a lair — the hatchery's own RequiredSlotKind.
        WorldValidation.Validate(WithStructureAt("ember-hollow", slotIndex: 3, "hatchery"));
    }

    [Fact]
    public void A_hatchery_on_a_non_lair_slot_fails_Rule14()
    {
        // ember-hollow slot 2 is an essence-deposit, not a Lair — the hatchery's own RequiredSlotKind.
        var ex = Assert.Throws<InvalidOperationException>(
            () => WorldValidation.Validate(WithStructureAt("ember-hollow", slotIndex: 2, "hatchery")));
        Assert.Contains("ember-hollow", ex.Message);
    }

    [Fact]
    public void A_soul_conduit_adds_its_own_flat_yield_additively()
    {
        var plain = Sector(Rootbed());
        var withConduit = Sector(Rootbed(), EssenceDeposit("soul-conduit") with { SlotIndex = 1 });

        Assert.Equal(
            LoamProduction.For(plain) + LoamPolicy.SoulConduitFlatYieldPerTurn,
            LoamProduction.For(withConduit));
    }

    [Fact]
    public void An_extractor_still_under_construction_contributes_no_flat_yield_yet()
    {
        var sector = Sector(ShardVein("extractor", constructionTurnsRemaining: LoamPolicy.ExtractorBuildTurns));
        Assert.Equal(0, LoamProduction.For(sector));
    }

    [Fact]
    public void An_extractor_finished_at_zero_remaining_contributes_its_flat_yield()
    {
        var sector = Sector(ShardVein("extractor", constructionTurnsRemaining: 0));
        Assert.Equal(LoamPolicy.ExtractorFlatYieldPerTurn, LoamProduction.For(sector));
    }

    [Fact]
    public void Every_existing_structure_still_carries_a_zero_flat_yield_no_existing_row_changed()
    {
        foreach (var id in new[] { "loam-source-placeholder", "well", "waystation", "granary" })
            Assert.Equal(0, StructureCatalog.Get(id).FlatYieldPerTurn);
    }

    [Fact]
    public void An_active_hatchery_on_a_cleared_lair_multiplies_the_sectors_recruit_pulse_through_PulseFor()
    {
        // world-map W56's own stated acceptance: "through W43's policy rather than through a second
        // code path" — proven by asserting the exact composed per-mille product, not merely that the
        // pulse went up.
        const long seatPulse = 100;
        const int lairMultiplier = 2000; // a bare cleared lair, no hatchery
        var seat = new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId, OwnerFactionId = "dave" };

        var bareCleared = new FusionRpg.Core.World.WorldState
        {
            WorldId = "w", TemplateId = "test", Seed = 1, CurrentTurn = 6,
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[] { new WorldSector { SectorId = "s1", TypeId = "stable", OwnerFactionId = "dave", Slots = new[] { seat, Lair(GuardState.Cleared) } } }
        };
        var withHatchery = bareCleared with
        {
            Sectors = new[]
            {
                bareCleared.Sectors[0] with
                {
                    Slots = new[] { seat, Lair(GuardState.Cleared, "hatchery", constructionTurnsRemaining: 0) }
                }
            }
        };

        var reportBare = new FusionRpg.Core.World.Turn.TurnReport();
        var reportHatchery = new FusionRpg.Core.World.Turn.TurnReport();

        var resultBare = FusionRpg.Core.World.Growth.GrowthPhases.Growth(
            bareCleared, reportBare, "Test", turn: 7, seed: 1,
            seatPulsePerWeek: seatPulse, lairMultiplierMilli: lairMultiplier, specialWeekMultiplierMilli: 1000);
        var resultHatchery = FusionRpg.Core.World.Growth.GrowthPhases.Growth(
            withHatchery, reportHatchery, "Test", turn: 7, seed: 1,
            seatPulsePerWeek: seatPulse, lairMultiplierMilli: lairMultiplier, specialWeekMultiplierMilli: 1000);

        var bareStock = resultBare.Sectors.Single().RecruitStock;
        var hatcheryStock = resultHatchery.Sectors.Single().RecruitStock;

        Assert.Equal(seatPulse * lairMultiplier / 1000, bareStock);
        var expectedCombinedMultiplier = (long)lairMultiplier * LoamPolicy.HatcheryYieldMultiplierMilli / 1000;
        Assert.Equal(seatPulse * expectedCombinedMultiplier / 1000, hatcheryStock);
        Assert.True(hatcheryStock > bareStock, "an active hatchery must multiply the pulse further, not merely match it");
    }

    [Fact]
    public void A_hatchery_still_under_construction_contributes_no_extra_multiplier()
    {
        const long seatPulse = 100;
        const int lairMultiplier = 2000;
        var seat = new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId, OwnerFactionId = "dave" };
        var world = new FusionRpg.Core.World.WorldState
        {
            WorldId = "w", TemplateId = "test", Seed = 1, CurrentTurn = 6,
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                new WorldSector
                {
                    SectorId = "s1", TypeId = "stable", OwnerFactionId = "dave",
                    Slots = new[] { seat, Lair(GuardState.Cleared, "hatchery", constructionTurnsRemaining: LoamPolicy.HatcheryBuildTurns) }
                }
            }
        };

        var result = FusionRpg.Core.World.Growth.GrowthPhases.Growth(
            world, new FusionRpg.Core.World.Turn.TurnReport(), "Test", turn: 7, seed: 1,
            seatPulsePerWeek: seatPulse, lairMultiplierMilli: lairMultiplier, specialWeekMultiplierMilli: 1000);

        Assert.Equal(seatPulse * lairMultiplier / 1000, result.Sectors.Single().RecruitStock);
    }

    [Fact]
    public void An_intact_lair_with_an_active_hatchery_still_contributes_no_multiplier_at_all()
    {
        // A hatchery only ever matters once its own lair is actually cleared — matching PulseFor's
        // own "lairCleared" gate, never special-cased separately here.
        const long seatPulse = 100;
        const int lairMultiplier = 2000;
        var seat = new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId, OwnerFactionId = "dave" };
        var world = new FusionRpg.Core.World.WorldState
        {
            WorldId = "w", TemplateId = "test", Seed = 1, CurrentTurn = 6,
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                new WorldSector
                {
                    SectorId = "s1", TypeId = "stable", OwnerFactionId = "dave",
                    Slots = new[] { seat, Lair(GuardState.Intact, "hatchery", constructionTurnsRemaining: 0) }
                }
            }
        };

        var result = FusionRpg.Core.World.Growth.GrowthPhases.Growth(
            world, new FusionRpg.Core.World.Turn.TurnReport(), "Test", turn: 7, seed: 1,
            seatPulsePerWeek: seatPulse, lairMultiplierMilli: lairMultiplier, specialWeekMultiplierMilli: 1000);

        Assert.Equal(seatPulse, result.Sectors.Single().RecruitStock); // no lair multiplier at all — intact
    }
}
