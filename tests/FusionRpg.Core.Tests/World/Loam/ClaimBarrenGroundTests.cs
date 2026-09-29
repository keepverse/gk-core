using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L14 acceptance (spec-loam-turn.md): the settlement rule needs no enforcement — the claim is
/// allowed, and a warning names the ground as temporary. Refusing it would delete a real strategy
/// (seizing a corridor to sever a chain), so `ClaimResolver` must never reject on habitability.
/// </summary>
public class ClaimBarrenGroundTests
{
    static WorldState World(WorldSector sector, WorldEntity legion) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = new[] { sector },
        Entities = new[] { legion }
    };

    /// <summary>
    /// Same as <see cref="World"/> plus an unconnected, already-owned rootbed sector — needed for
    /// any test that actually runs `LoamPhases.Pressure` to completion, so the faction has a source
    /// *somewhere* and G-C's "no source anywhere" exemption does not swallow the fixture sector's
    /// upkeep whole (the two never share a lane, so they never share a component).
    /// </summary>
    static WorldState WorldWithASourceElsewhere(WorldSector sector, WorldEntity legion) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = new[]
        {
            sector,
            new WorldSector
            {
                SectorId = "elsewhere", TypeId = "stable", OwnerFactionId = "f1",
                Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId } }
            }
        },
        Entities = new[] { legion }
    };

    static WorldEntity Legion(string sectorId) => new()
    {
        EntityId = "e1", Kind = WorldEntityKind.Legion, OwnerFactionId = "f1", AtSectorId = sectorId
    };

    static WorldCommand ClaimCommand(string sectorId) => new()
    {
        CommanderId = "f1", CommandId = "c1", Kind = WorldCommandKinds.Claim, EntityId = "e1", SectorId = sectorId
    };

    [Fact]
    public void Claiming_barren_ground_succeeds_and_warns_it_is_temporary()
    {
        var barren = new WorldSector { SectorId = "s", TypeId = "stable", OwnerFactionId = null };
        var world = World(barren, Legion("s"));

        var report = new TurnReport();
        var after = ClaimResolver.Run(world, new[] { ClaimCommand("s") }, report, "Snapshot", turn: 1);

        Assert.Equal("f1", after.Sectors.Single(x => x.SectorId == "s").OwnerFactionId);
        Assert.False(Habitability.For(after.Sectors.Single(x => x.SectorId == "s")));

        var warning = Assert.Single(report.Entries, e => e.Detail.StartsWith("claim.barren:"));
        Assert.Equal("s", warning.SectorId);
    }

    [Fact]
    public void Reclaiming_barren_ground_does_not_rescue_it_from_the_fade()
    {
        // The loophole the settlement rule closes for free: a sector that faded for want of a
        // source cannot be held by re-claiming it, because re-claiming does not create a source.
        var barren = new WorldSector { SectorId = "s", TypeId = "stable", OwnerFactionId = null, StabilityMilli = 1000, DevelopmentLevel = 2, DangerBand = 1 };
        var world = WorldWithASourceElsewhere(barren, Legion("s"));
        static WorldSector S(WorldState w) => w.Sectors.Single(x => x.SectorId == "s");

        var claimed = ClaimResolver.Run(world, new[] { ClaimCommand("s") }, new TurnReport(), "Snapshot", turn: 1);
        Assert.Equal("f1", S(claimed).OwnerFactionId);

        var turnsRun = 0;
        while (S(claimed).Phase != SectorPhase.Lost && turnsRun < 200)
        {
            var report = new TurnReport();
            claimed = LoamPhases.Pressure(LoamPhases.Production(claimed, report, "Test"), report, "Test");
            turnsRun++;
        }

        Assert.Equal(SectorPhase.Lost, S(claimed).Phase);

        // Re-claim it again — still no source, so it must fade the same way rather than holding.
        // Claiming touches ownership and phase only, never stability directly: the ground is still
        // at the zero it faded to, which is exactly why the very next Pressure pass marks it Lost
        // again almost immediately — reclaiming bought no runway at all.
        var reclaimed = ClaimResolver.Run(claimed, new[] { ClaimCommand("s") }, new TurnReport(), "Snapshot", turn: turnsRun + 1);
        Assert.Equal("f1", S(reclaimed).OwnerFactionId);
        Assert.Equal(0, S(reclaimed).StabilityMilli);

        turnsRun = 0;
        while (S(reclaimed).Phase != SectorPhase.Lost && turnsRun < 200)
        {
            var report = new TurnReport();
            reclaimed = LoamPhases.Pressure(LoamPhases.Production(reclaimed, report, "Test"), report, "Test");
            turnsRun++;
        }

        Assert.Equal(SectorPhase.Lost, S(reclaimed).Phase);
    }

    [Fact]
    public void Claiming_a_rootbed_sector_carries_no_barren_warning()
    {
        var withSource = new WorldSector
        {
            SectorId = "s", TypeId = "stable", OwnerFactionId = null,
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId } }
        };
        var world = World(withSource, Legion("s"));

        var report = new TurnReport();
        ClaimResolver.Run(world, new[] { ClaimCommand("s") }, report, "Snapshot", turn: 1);

        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("claim.barren:"));
    }
}
