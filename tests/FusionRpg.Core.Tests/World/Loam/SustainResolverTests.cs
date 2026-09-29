using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L28 (spec-loam-legions.md): `Sustain` spends a legion's own carried loam, 1:1, into the sector
/// it stands on — resolved at the very top of `Pressure`, before the component's automatic
/// accounting, so the spend can change whether that component even has a shortfall this turn.
/// </summary>
public class SustainResolverTests
{
    const string Dave = "dave";

    static WorldEntity Legion(string atSectorId, long carriedLoam) => new()
    {
        EntityId = "legion", Kind = WorldEntityKind.Legion, OwnerFactionId = Dave, AtSectorId = atSectorId,
        CarriedLoam = carriedLoam,
        Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } }
    };

    /// <summary>
    /// One rootbed sector ("home", production 50, upkeep 10) and one barren, expensive sector
    /// ("poor", production 30, upkeep 73 once the legion garrisoning it is counted) in the same
    /// component. Available (post-Production) is 80, upkeep is 83 — a three-unit shortfall, just
    /// enough to make `Weakest` pick "poor" (the worse per-sector balance) without a Sustain spend,
    /// and small enough that a modest spend closes it. Both start at full stability so the fade
    /// this causes is observable without also releasing the ground outright.
    ///
    /// world-map W55 (empire-economy-ssot.md A8) note: "poor"'s own production is no longer zero —
    /// `LoamProduction.For` now reads `DevelopmentLevel` too, so `DangerBand` here is
    /// `2 + 2 * DevelopmentLevel(5) = 12`, not the original `2`, to compensate the new yield term
    /// exactly (`DevelopmentYieldPerLevel(6) / DangerUpkeepPerBand(3) == 2` at the real configured
    /// tuning) and keep the shortfall at the same three units the acceptance test below still names.
    /// </summary>
    static WorldState Fixture(WorldEntity? entity = null) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = Dave, Kind = WorldFactionKind.Player, Name = "Dave" } },
        Sectors = new[]
        {
            new WorldSector
            {
                SectorId = "home", TypeId = "stable", OwnerFactionId = Dave, Phase = SectorPhase.Held,
                StabilityMilli = 1000,
                Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId } }
            },
            new WorldSector
            {
                SectorId = "poor", TypeId = "stable", OwnerFactionId = Dave, Phase = SectorPhase.Held,
                StabilityMilli = 1000, DevelopmentLevel = 5, DangerBand = 2 + 2 * 5
            }
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l-home-poor", FromSectorId = "home", ToSectorId = "poor", TypeId = LaneTypeCatalog.RiftLaneTypeId }
        },
        Entities = entity is null ? Array.Empty<WorldEntity>() : new[] { entity }
    };

    static WorldCommand Sustain(string entityId, long amount) => new()
    {
        CommanderId = Dave, CommandId = "s1", Kind = WorldCommandKinds.Sustain, EntityId = entityId, Amount = amount
    };

    static WorldCommand Stand() => new() { CommanderId = Dave, CommandId = "stand", Kind = WorldCommandKinds.StandFast };

    // ---- the acceptance criterion: changes whether the sector is picked as weakest ----

    [Fact]
    public void Without_a_sustain_spend_the_shortfall_picks_the_poor_sector_as_weakest()
    {
        var world = Fixture(Legion("poor", carriedLoam: 10));
        var result = TurnEngine.Step(world, new[] { Stand() }, seed: 1);

        Assert.Contains(result.Report.Entries, e => e.Detail == "loam.shortfall:3" && e.SectorId == "poor");
        var poor = result.World.Sectors.Single(s => s.SectorId == "poor");
        Assert.True(poor.StabilityMilli < 1000);
    }

    [Fact]
    public void A_sustain_spend_covers_the_shortfall_so_no_sector_is_picked_as_weakest()
    {
        var world = Fixture(Legion("poor", carriedLoam: 10));
        var result = TurnEngine.Step(world, new[] { Sustain("legion", amount: 10) }, seed: 1);

        Assert.DoesNotContain(result.Report.Entries, e => e.Detail.StartsWith("loam.shortfall"));

        // Paid in full this turn: every member of the component recovers (clamped at 1000) instead
        // of fading — the opposite outcome from the no-Sustain baseline above.
        var poor = result.World.Sectors.Single(s => s.SectorId == "poor");
        Assert.Equal(1000, poor.StabilityMilli);
    }

    [Fact]
    public void The_spend_lands_in_the_sectors_stock_and_is_deducted_from_the_legion()
    {
        var world = Fixture(Legion("poor", carriedLoam: 10));
        var report = new TurnReport();

        var result = SustainResolver.Run(world, new[] { Sustain("legion", amount: 10) }, report, "pressure");

        Assert.Equal(10, result.Sectors.Single(s => s.SectorId == "poor").LoamStock);
        Assert.Equal(0, result.Entities.Single(e => e.EntityId == "legion").CarriedLoam);
        Assert.Contains(report.Entries, e => e.Detail == "sustain:10");
    }

    // ---- bounded by what the legion actually carries ----

    [Fact]
    public void The_spend_is_bounded_by_what_the_legion_actually_carries()
    {
        var world = Fixture(Legion("poor", carriedLoam: 3));
        var report = new TurnReport();

        var result = SustainResolver.Run(world, new[] { Sustain("legion", amount: 10) }, report, "pressure");

        Assert.Equal(3, result.Sectors.Single(s => s.SectorId == "poor").LoamStock);
        Assert.Equal(0, result.Entities.Single(e => e.EntityId == "legion").CarriedLoam);
    }

    [Fact]
    public void A_legion_carrying_nothing_has_its_order_dropped_rather_than_spending_zero()
    {
        var world = Fixture(Legion("poor", carriedLoam: 0));
        var report = new TurnReport();

        var result = SustainResolver.Run(world, new[] { Sustain("legion", amount: 10) }, report, "pressure");

        Assert.Equal(0, result.Sectors.Single(s => s.SectorId == "poor").LoamStock);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "sustain.nothing-carried");
    }

    // ---- validity gates ----

    [Fact]
    public void A_legion_not_standing_on_its_own_factions_ground_cannot_sustain_it()
    {
        var world = Fixture() with
        {
            Factions = new[]
            {
                new WorldFaction { FactionId = Dave, Kind = WorldFactionKind.Player, Name = "Dave" },
                new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Zomboss" }
            },
            Sectors = new[]
            {
                new WorldSector { SectorId = "home", TypeId = "stable", OwnerFactionId = Dave, Phase = SectorPhase.Held },
                new WorldSector { SectorId = "poor", TypeId = "stable", OwnerFactionId = "zomboss", Phase = SectorPhase.Held }
            },
            Entities = new[] { Legion("poor", carriedLoam: 10) }
        };
        var report = new TurnReport();

        var result = SustainResolver.Run(world, new[] { Sustain("legion", amount: 10) }, report, "pressure");

        Assert.Equal(0, result.Sectors.Single(s => s.SectorId == "poor").LoamStock);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "sustain.not-yours");
    }

    [Fact]
    public void A_legion_that_no_longer_exists_has_its_order_dropped()
    {
        var world = Fixture(Legion("poor", carriedLoam: 10));
        var report = new TurnReport();

        var result = SustainResolver.Run(world, new[] { Sustain("nonexistent", amount: 10) }, report, "pressure");

        Assert.Equal(0, result.Sectors.Single(s => s.SectorId == "poor").LoamStock);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "entity.gone");
    }

    [Fact]
    public void A_routed_legion_cannot_sustain_this_turn()
    {
        var world = Fixture(Legion("poor", carriedLoam: 10) with { Routed = true });
        var report = new TurnReport();

        var result = SustainResolver.Run(world, new[] { Sustain("legion", amount: 10) }, report, "pressure");

        Assert.Equal(0, result.Sectors.Single(s => s.SectorId == "poor").LoamStock);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "entity.routed");
    }

    // ---- admission ----

    [Fact]
    public void Admission_rejects_a_sustain_order_with_no_amount_or_a_non_positive_one()
    {
        var world = Fixture(Legion("poor", carriedLoam: 10));

        var missing = WorldCommandAdmission.Admit(world, new WorldCommand
        {
            CommanderId = Dave, CommandId = "s1", Kind = WorldCommandKinds.Sustain, EntityId = "legion"
        });
        Assert.False(missing.Ok);
        Assert.Equal("amount.invalid", missing.Reason);

        var zero = WorldCommandAdmission.Admit(world, Sustain("legion", amount: 0));
        Assert.False(zero.Ok);
        Assert.Equal("amount.invalid", zero.Reason);

        var negative = WorldCommandAdmission.Admit(world, Sustain("legion", amount: -5));
        Assert.False(negative.Ok);
        Assert.Equal("amount.invalid", negative.Reason);
    }

    [Fact]
    public void Admission_accepts_a_well_formed_sustain_order()
    {
        var world = Fixture(Legion("poor", carriedLoam: 10));
        var (ok, reason) = WorldCommandAdmission.Admit(world, Sustain("legion", amount: 10));

        Assert.True(ok, reason);
    }
}
